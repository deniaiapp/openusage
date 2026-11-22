namespace OpenUsage.Core.Providers.Copilot;

internal sealed class CopilotDomain
{
    public static readonly CopilotDomain Public = new("github.com");
    public const string InvalidMessage = "Enter github.com or a GitHub Enterprise Cloud address like octocorp.ghe.com.";
    private const string EnterpriseSuffix = ".ghe.com";

    private CopilotDomain(string host) => Host = host;

    public string Host { get; }
    public Uri WebUrl(string path) => new($"https://{Host}{path}");
    public Uri ApiUrl(string path) => new($"https://api.{Host}{path}");

    public static CopilotDomain? Parse(string input)
    {
        var text = input.Trim().ToLowerInvariant();
        if (text.Length == 0) return Public;
        if (text.StartsWith("https://", StringComparison.Ordinal)) text = text["https://".Length..];
        var separator = text.IndexOfAny(['/', '?', '#']);
        var host = separator < 0 ? text : text[..separator];
        if (host is "github.com" or "www.github.com") return Public;
        var name = host.EndsWith(EnterpriseSuffix, StringComparison.Ordinal) ? host[..^EnterpriseSuffix.Length] : host;
        if (name.Length is < 1 or > 63 || name[0] == '-' || name[^1] == '-' ||
            name.Any(ch => ch is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            return null;
        }

        return new CopilotDomain(name + EnterpriseSuffix);
    }
}
