using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace OpenUsage.Core.Providers.Ollama;

public sealed class OllamaSigningKey
{
    public required string PublicKeyBase64 { get; init; }
    public required byte[] Seed { get; init; }
}

public static class OpenSshEd25519Key
{
    private static readonly byte[] Magic = "openssh-key-v1\0"u8.ToArray();
    private static readonly byte[] KeyType = "ssh-ed25519"u8.ToArray();

    public static OllamaSigningKey? Parse(string pem)
    {
        var blob = Base64Body(pem);
        if (blob is null || blob.Length < Magic.Length || !blob.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return null;
        var reader = new ByteReader(blob, Magic.Length);
        if (!reader.String().SequenceEqual("none"u8.ToArray())) return null;
        if (!reader.String().SequenceEqual("none"u8.ToArray())) return null;
        reader.String();
        if (reader.UInt32() != 1) return null;
        var publicKeyBlob = reader.String();
        var privateSection = reader.String();
        var privateReader = new ByteReader(privateSection.ToArray(), 0);
        privateReader.UInt32();
        privateReader.UInt32();
        if (!privateReader.String().SequenceEqual(KeyType.AsSpan())) return null;
        privateReader.String();
        var privateKey = privateReader.String();
        if (privateKey.Length != 64) return null;
        var seed = privateKey[..32];
        return new OllamaSigningKey
        {
            PublicKeyBase64 = Convert.ToBase64String(publicKeyBlob.ToArray()),
            Seed = seed.ToArray()
        };
    }

    public static string Authorization(OllamaSigningKey key, string method, string requestUri)
    {
        var message = Encoding.UTF8.GetBytes($"{method},{requestUri}");
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(key.Seed, 0));
        signer.BlockUpdate(message, 0, message.Length);
        var signature = signer.GenerateSignature();
        return $"{key.PublicKeyBase64}:{Convert.ToBase64String(signature)}";
    }

    private static byte[]? Base64Body(string pem)
    {
        var body = string.Join("", pem.Split('\n', '\r')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
        try { return Convert.FromBase64String(body); }
        catch { return null; }
    }

    private ref struct ByteReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;
        public ByteReader(byte[] data, int offset)
        {
            _data = data;
            _offset = offset;
        }
        public uint UInt32()
        {
            if (_offset + 4 > _data.Length) return 0;
            var value = BinaryPrimitives.ReadUInt32BigEndian(_data[_offset..]);
            _offset += 4;
            return value;
        }
        public ReadOnlySpan<byte> String()
        {
            var length = (int)UInt32();
            if (length < 0 || _offset + length > _data.Length) return [];
            var slice = _data.Slice(_offset, length);
            _offset += length;
            return slice;
        }
    }
}

/// <summary>
/// What an account response said about the plan. "No badge" has two causes: an account with no plan
/// (ordinary) and a response OpenUsage couldn't read (worth a warning, since the badge just disappears).
/// </summary>
public sealed record OllamaPlan(string? Name, bool IsUnreadable = false)
{
    public static readonly OllamaPlan Absent = new((string?)null);
    public static readonly OllamaPlan Unreadable = new(null, true);
    public static OllamaPlan Named(string name) => new(name);
}

/// <summary>
/// Maps ollama.com <c>GET /api/balance</c> plus the plan name from <c>POST /api/me</c>.
/// Current plans carry a monthly dollar allowance (<c>included.balance_usd</c> left of
/// <c>included.allowance_usd</c> over <c>included.period</c>); legacy plans keep session and weekly limits
/// as <c>remaining_percent</c> with <c>resets_at</c>. Both may carry <c>purchased.balance_usd</c>.
/// See https://docs.ollama.com/api/balance.
/// </summary>
public static class OllamaUsageMapper
{
    public const long SessionPeriodMs = 5L * 60 * 60 * 1000;
    public const long WeeklyPeriodMs = 7L * 24 * 60 * 60 * 1000;

    /// <summary>
    /// <paramref name="accountBody"/> is null when the account request itself failed, which the provider
    /// already reports — classifying it as unreadable here would warn about the same thing twice.
    /// </summary>
    public static (OllamaPlan Plan, List<MetricLine> Lines) Map(JsonObject? balance, string? accountBody)
    {
        var plan = accountBody is null ? OllamaPlan.Absent : Plan(accountBody);
        return (plan, BalanceLines(balance));
    }

    /// <summary>Session and Weekly (legacy plans) or Monthly (current plans), then Purchased Credits.</summary>
    public static List<MetricLine> BalanceLines(JsonObject? root)
    {
        // `included` is the plan allowance this provider exists to show; missing, or matching neither
        // documented shape, is a loud failure rather than an empty dashboard.
        var included = JsonUtil.Object(root?["included"]) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var lines = new List<MetricLine>();
        if (LegacyLine(included["session"], "Session", SessionPeriodMs) is { } session) lines.Add(session);
        if (LegacyLine(included["weekly"], "Weekly", WeeklyPeriodMs) is { } weekly) lines.Add(weekly);
        if (MonthlyLine(included) is { } monthly) lines.Add(monthly);
        if (lines.Count == 0) throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        if (PurchasedLine(root!["purchased"]) is { } purchased) lines.Add(purchased);
        return lines;
    }

    /// <summary>
    /// The account's plan, title-cased for the badge. ollama.com capitalizes its keys (<c>Plan</c>); the
    /// local Ollama proxy lowercases them, so both are accepted. A body that isn't an account, or whose
    /// plan field is missing or not a string, is unreadable; an explicitly empty plan is just absent.
    /// </summary>
    public static OllamaPlan Plan(string body)
    {
        var root = JsonUtil.ParseObject(body);
        if (root is null) return OllamaPlan.Unreadable;
        var raw = root["Plan"] ?? root["plan"];
        if (raw is not JsonValue value || !value.TryGetValue<string>(out var text)) return OllamaPlan.Unreadable;
        var name = text.Trim();
        if (name.Length == 0) return OllamaPlan.Absent;
        return OllamaPlan.Named(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()));
    }

    /// <summary>A legacy limit as a used meter. Ollama reports what remains, so used = 100 - remaining.</summary>
    private static MetricLine? LegacyLine(JsonNode? entry, string label, long periodMs)
    {
        var obj = JsonUtil.Object(entry);
        if (obj is null || JsonUtil.Number(obj["remaining_percent"]) is not { } remaining) return null;
        // The period only pairs with a real reset; alone it would render a static "Resets in 5h".
        var resetsAt = Iso8601.Parse(JsonUtil.String(obj["resets_at"]));
        return MetricLine.Progress(label, JsonUtil.ClampPercent(100 - remaining), 100, ProgressFormat.Percent,
            resetsAt, resetsAt is null ? null : periodMs);
    }

    /// <summary>The monthly allowance as a dollar meter: used = allowance - balance, resetting at period.until.</summary>
    private static MetricLine? MonthlyLine(JsonObject included)
    {
        if (JsonUtil.Number(included["allowance_usd"]) is not { } allowance || allowance <= 0) return null;
        if (JsonUtil.Number(included["balance_usd"]) is not { } balance) return null;
        var period = JsonUtil.Object(included["period"]);
        var from = Iso8601.Parse(JsonUtil.String(period?["from"]));
        var until = Iso8601.Parse(JsonUtil.String(period?["until"]));
        long? periodMs = from is { } f && until is { } u && u > f ? (long)(u - f).TotalMilliseconds : null;
        return MetricLine.Progress("Monthly", Math.Min(allowance, Math.Max(0, allowance - balance)), allowance,
            ProgressFormat.Dollars, until, periodMs);
    }

    /// <summary>Unexpired purchased credits as an unbounded dollar row. A real zero is shown.</summary>
    private static MetricLine? PurchasedLine(JsonNode? purchased)
    {
        var obj = JsonUtil.Object(purchased);
        if (obj is null || JsonUtil.Number(obj["balance_usd"]) is not { } balance) return null;
        return MetricLine.Values("Purchased Credits", [new MetricValue(Math.Max(0, balance), MetricKind.Dollars)]);
    }
}

public sealed class OllamaProvider : IProviderRuntime
{
    public const string Host = "https://ollama.com";
    public const string BalancePath = "/api/balance";
    public const string AccountPath = "/api/me";

    /// <summary>
    /// One notice for every way the plan can go missing, so a failed request and an unreadable response
    /// read the same — the badge is gone for a reason, and the meters are still current.
    /// </summary>
    public const string PlanWarning = "Couldn't read your Ollama plan. Usage below is still up to date.";

    private readonly AppHttp _http;
    private readonly Func<OllamaSigningKey?> _loadKey;

    public OllamaProvider(AppHttp http, Func<OllamaSigningKey?>? loadKey = null)
    {
        _http = http;
        _loadKey = loadKey ?? LoadKey;
    }

    public Provider Provider { get; } = new(
        "ollama", "Ollama", "ollama",
        [
            new ProviderLink("Usage", "https://ollama.com/settings"),
            new ProviderLink("API Keys", "https://ollama.com/settings/keys")
        ]);

    // Session and Weekly exist only on legacy plans; current plans get monthly credits instead.
    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("ollama.session", Provider, "Session"),
        WidgetDescriptor.Percent("ollama.weekly", Provider, "Weekly"),
        WidgetDescriptor.BoundedDollars("ollama.monthly", Provider, "Monthly", 60, limitNoun: "included"),
        WidgetDescriptor.DollarBalance("ollama.purchasedCredits", Provider, "Purchased Credits", valueWord: "left")
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        OllamaSigningKey? key;
        try { key = _loadKey(); }
        catch (Exception ex) { return ProviderSnapshot.Error(Provider, ex); }
        if (key is null)
            return ProviderSnapshot.Error(Provider, "No Ollama key found. Install Ollama and run `ollama signin` to track cloud usage.");

        AppHttpResponse balance;
        try { balance = await Send("GET", BalancePath, key, cancellationToken); }
        catch (Exception ex)
        {
            AppLog.Warn("ollama", $"balance request failed: {ex.Message}");
            return ProviderSnapshot.Error(Provider, ProviderErrors.ConnectionFailed);
        }
        if (balance.StatusCode is 401 or 403)
            return ProviderSnapshot.Error(Provider, "Not signed in to Ollama Cloud. Run `ollama signin` to see usage.");
        if (!balance.IsSuccess)
            return ProviderSnapshot.Error(Provider, ProviderErrors.RequestFailed(balance.StatusCode));

        // The account call is best-effort (plan badge only): it must not blank out the meters, but a
        // failure is logged and explained with an amber notice instead of silently dropping the badge.
        string? accountBody = null;
        string? warning = null;
        try
        {
            var account = await Send("POST", AccountPath, key, cancellationToken);
            if (account.IsSuccess)
            {
                accountBody = account.Text;
            }
            else
            {
                AppLog.Warn("ollama", $"plan lookup failed (HTTP {account.StatusCode}); meters unaffected");
                warning = PlanWarning;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("ollama", $"plan lookup failed ({ex.Message}); meters unaffected");
            warning = PlanWarning;
        }

        try
        {
            var mapped = OllamaUsageMapper.Map(balance.Json, accountBody);
            if (warning is null && mapped.Plan.IsUnreadable)
            {
                AppLog.Warn("ollama", "plan response unreadable; meters unaffected");
                warning = PlanWarning;
            }
            return ProviderSnapshot.Make(Provider, mapped.Plan.Name, mapped.Lines, DateTimeOffset.Now, warning);
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
    }

    private static OllamaSigningKey? LoadKey()
    {
        var path = AppPaths.Expand("~/.ollama/id_ed25519");
        if (!File.Exists(path)) return null;
        try
        {
            var key = OpenSshEd25519Key.Parse(File.ReadAllText(path));
            if (key is null) throw new InvalidOperationException("~/.ollama/id_ed25519 isn't a usable Ollama signing key.");
            return key;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Couldn't read ~/.ollama/id_ed25519. Check the file's permissions.", ex);
        }
    }

    private Task<AppHttpResponse> Send(string method, string path, OllamaSigningKey key, CancellationToken ct)
    {
        // The signature covers the exact URI that goes on the wire, so the header and URL share one string.
        var requestUri = $"{path}?ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var authorization = OpenSshEd25519Key.Authorization(key, method, requestUri);
        return _http.SendAsync(new AppHttpRequest(
            method,
            new Uri(Host + requestUri),
            new Dictionary<string, string>
            {
                ["Authorization"] = authorization,
                ["Accept"] = "application/json"
            },
            TimeoutSeconds: 15), ct);
    }
}
