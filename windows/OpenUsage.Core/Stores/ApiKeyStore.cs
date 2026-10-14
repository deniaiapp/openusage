using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Stores;

public sealed class ApiKeyStore
{
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ApiKeyStore() => Load();

    public string? Get(string providerId)
    {
        lock (_gate) return _keys.TryGetValue(providerId, out var value) ? value : null;
    }

    public void Set(string providerId, string? key)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(key)) _keys.Remove(providerId);
            else _keys[providerId] = key.Trim();
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.KeysFile)) return;
            var protectedBytes = File.ReadAllBytes(AppPaths.KeysFile);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null) return;
            foreach (var (k, v) in parsed)
                if (!string.IsNullOrWhiteSpace(v)) _keys[k] = v;
        }
        catch (Exception ex)
        {
            AppLog.Error("keys", "could not load API keys", ex);
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_keys);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.KeysFile, protectedBytes);
    }
}
