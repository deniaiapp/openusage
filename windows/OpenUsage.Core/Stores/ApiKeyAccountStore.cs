using System.Security.Cryptography;
using System.Text.Json;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Stores;

public sealed record ApiKeyAccount(Guid Id, string ProviderId, string Label)
{
    public string CardId => $"{ProviderId}@{Id:N}";
}

public sealed class ApiKeyAccountStore(string? path = null)
{
    private readonly object _gate = new();
    private bool _hasUnreadableAccounts;
    private string PathOnDisk => path ?? Path.Combine(AppPaths.OpenUsageDir, "api-key-accounts.bin");

    public bool HasUnreadableAccounts
    {
        get { lock (_gate) return _hasUnreadableAccounts; }
    }

    public IReadOnlyList<ApiKeyAccount> List()
    {
        lock (_gate) return ReadableEntries(Load()).Select(entry => entry.Account).ToArray();
    }

    internal string? Key(Guid id)
    {
        lock (_gate) return ReadableEntries(Load()).FirstOrDefault(entry => entry.Account.Id == id)?.Key;
    }

    internal ApiKeyAccount Add(string providerId, string label, string key)
    {
        lock (_gate)
        {
            var entries = Load();
            EnsureUniqueKey(ReadableEntries(entries), providerId, key);
            var account = new ApiKeyAccount(Guid.NewGuid(), providerId, label);
            entries.Add(new StoredRow(new Entry(account, key)));
            Save(entries);
            return account;
        }
    }

    internal void ReplaceKey(ApiKeyAccount account, string key)
    {
        lock (_gate)
        {
            var entries = Load();
            var index = entries.FindIndex(row =>
                row.Readable?.Account is { } saved && saved.Id == account.Id && saved.ProviderId == account.ProviderId);
            if (index < 0 || entries[index].Readable is not { } entry)
                throw new InvalidOperationException("This account is no longer connected.");
            EnsureUniqueKey(ReadableEntries(entries), account.ProviderId, key, account.Id);
            entries[index] = new StoredRow(entry with { Key = key });
            Save(entries);
        }
    }

    private static void EnsureUniqueKey(IEnumerable<Entry> entries, string providerId, string key, Guid? except = null)
    {
        if (entries.Any(entry => entry.Account.Id != except && entry.Account.ProviderId == providerId && entry.Key == key))
            throw new InvalidOperationException("This API key is already connected.");
    }

    private static IEnumerable<Entry> ReadableEntries(IEnumerable<StoredRow> rows) =>
        rows.Select(row => row.Readable).OfType<Entry>();

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.RemoveAll(row => row.Readable?.Account.Id == id) == 0) return false;
            Save(entries);
            return true;
        }
    }

    public bool Rename(Guid id, string label)
    {
        label = NormalizeLabel(label);
        lock (_gate)
        {
            var entries = Load();
            var index = entries.FindIndex(row => row.Readable?.Account.Id == id);
            if (index < 0 || entries[index].Readable is not { } entry) return false;
            entries[index] = new StoredRow(entry with { Account = entry.Account with { Label = label } });
            Save(entries);
            return true;
        }
    }

    internal static string NormalizeLabel(string label)
    {
        label = label.Trim();
        if (label.Length is 0 or > 80 || label.Any(char.IsControl))
            throw new ArgumentException("Enter an account name between 1 and 80 characters.");
        return label;
    }

    private List<StoredRow> Load()
    {
        byte[] protectedBytes;
        try { protectedBytes = File.ReadAllBytes(PathOnDisk); }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _hasUnreadableAccounts = false;
            return [];
        }

        var clear = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        try
        {
            using var document = JsonDocument.Parse(clear);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Could not read saved accounts.");

            var rows = new List<StoredRow>();
            var hasUnreadableRows = false;
            foreach (var value in document.RootElement.EnumerateArray())
            {
                Entry? entry;
                try { entry = value.Deserialize<Entry>(); }
                catch (JsonException) { entry = null; }

                if (entry?.Account is { Id: var id, ProviderId.Length: > 0, Label.Length: > 0 } &&
                    id != Guid.Empty && !string.IsNullOrEmpty(entry.Key))
                {
                    rows.Add(new StoredRow(entry));
                }
                else
                {
                    rows.Add(new StoredRow(null, value.Clone()));
                    hasUnreadableRows = true;
                }
            }
            if (hasUnreadableRows && !_hasUnreadableAccounts)
                AppLog.Warn("accounts", "Some saved API key accounts could not be read and were kept unchanged.");
            _hasUnreadableAccounts = hasUnreadableRows;
            return rows;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private void Save(List<StoredRow> entries)
    {
        var path = PathOnDisk;
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var clear = JsonSerializer.SerializeToUtf8Bytes(entries.Select(row => (object?)row.Readable ?? row.Unreadable));
        try
        {
            File.WriteAllBytes(temp, ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            File.Delete(temp);
        }
    }

    private sealed record Entry(ApiKeyAccount Account, string Key);
    private sealed record StoredRow(Entry? Readable, JsonElement Unreadable = default);
}
