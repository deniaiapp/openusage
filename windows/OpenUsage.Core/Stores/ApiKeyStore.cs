using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Stores;

public sealed class ApiKeyStore
{
    private Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _path;

    public ApiKeyStore() : this(AppPaths.KeysFile) { }

    internal ApiKeyStore(string path)
    {
        _path = path;
        Load();
    }

    public string? Get(string providerId)
    {
        lock (_gate) return _keys.TryGetValue(providerId, out var value) ? value : null;
    }

    public void Set(string providerId, string? key)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, string>(_keys, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(key)) next.Remove(providerId);
            else next[providerId] = key.Trim();
            Save(next);
            _keys = next;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var protectedBytes = File.ReadAllBytes(_path);
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

    private void Save(Dictionary<string, string> keys)
    {
        var json = JsonSerializer.Serialize(keys);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        AtomicFile.WriteAllBytes(_path, protectedBytes);
    }
}
