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
    private string PathOnDisk => path ?? Path.Combine(AppPaths.OpenUsageDir, "api-key-accounts.bin");

    public IReadOnlyList<ApiKeyAccount> List()
    {
        lock (_gate) return Load().Select(entry => entry.Account).ToArray();
    }

    internal string? Key(Guid id)
    {
        lock (_gate) return Load().Find(entry => entry.Account.Id == id)?.Key;
    }

    internal ApiKeyAccount Add(string providerId, string label, string key)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.Any(entry => entry.Account.ProviderId == providerId && entry.Key == key))
                throw new InvalidOperationException("This API key is already connected.");
            var account = new ApiKeyAccount(Guid.NewGuid(), providerId, label);
            entries.Add(new Entry(account, key));
            Save(entries);
            return account;
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.RemoveAll(entry => entry.Account.Id == id) == 0) return false;
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
            var index = entries.FindIndex(entry => entry.Account.Id == id);
            if (index < 0) return false;
            var entry = entries[index];
            entries[index] = entry with { Account = entry.Account with { Label = label } };
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

    private List<Entry> Load()
    {
        byte[] protectedBytes;
        try { protectedBytes = File.ReadAllBytes(PathOnDisk); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }

        var clear = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(clear)
                ?? throw new InvalidDataException("Could not read saved accounts.");
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private void Save(List<Entry> entries)
    {
        var path = PathOnDisk;
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var clear = JsonSerializer.SerializeToUtf8Bytes(entries);
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
}
