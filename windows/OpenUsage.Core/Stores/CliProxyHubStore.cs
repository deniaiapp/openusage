using System.Security.Cryptography;
using System.Text.Json;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Stores;

public sealed record CliProxyHub(Guid Id, Uri Address);

internal sealed class CliProxyHubStore(string? path = null)
{
    private readonly object _gate = new();
    private string PathOnDisk => path ?? Path.Combine(AppPaths.OpenUsageDir, "cliproxyapi-hubs.bin");

    internal IReadOnlyList<CliProxyHub> List()
    {
        lock (_gate) return Load().Select(entry => entry.Hub).ToArray();
    }

    internal string? KeyFor(CliProxyHub hub)
    {
        lock (_gate) return Load().Find(entry => Matches(entry, hub))?.Key;
    }

    // The connection service must prove the key against the normalized address before saving it.
    internal CliProxyHub Add(Uri address, string validatedKey)
    {
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https") ||
            address.Host.Length == 0 || address.UserInfo.Length != 0 ||
            address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("Enter a valid CLIProxyAPI hub address.", nameof(address));
        var key = CheckKey(validatedKey);
        lock (_gate)
        {
            var entries = Load();
            if (entries.Any(entry => SameAddress(entry.Hub.Address, address)))
                throw new InvalidOperationException("This CLIProxyAPI hub is already connected.");
            var hub = new CliProxyHub(Guid.NewGuid(), address);
            entries.Add(new Entry(hub, key));
            Save(entries);
            return hub;
        }
    }

    internal bool ReplaceKey(CliProxyHub hub, string validatedKey)
    {
        var key = CheckKey(validatedKey);
        lock (_gate)
        {
            var entries = Load();
            var index = entries.FindIndex(entry => Matches(entry, hub));
            if (index < 0) return false;
            entries[index] = entries[index] with { Key = key };
            Save(entries);
            return true;
        }
    }

    internal bool Remove(Guid id)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.RemoveAll(entry => entry.Hub.Id == id) == 0) return false;
            Save(entries);
            return true;
        }
    }

    private static bool Matches(Entry entry, CliProxyHub hub) =>
        entry.Hub.Id == hub.Id && SameAddress(entry.Hub.Address, hub.Address);

    private static bool SameAddress(Uri saved, Uri candidate) =>
        candidate.IsAbsoluteUri && saved.AbsoluteUri == candidate.AbsoluteUri;

    private static string CheckKey(string value)
    {
        var key = value.Trim();
        if (key.Length == 0 || key.Any(char.IsControl))
            throw new ArgumentException("Enter a valid CLIProxyAPI management key.");
        return key;
    }

    private List<Entry> Load()
    {
        byte[] encrypted;
        try { encrypted = File.ReadAllBytes(PathOnDisk); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }

        var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(clear)
                ?? throw new InvalidDataException("Could not read saved CLIProxyAPI hubs.");
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private void Save(List<Entry> entries)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(entries);
        try
        {
            AtomicFile.WriteAllBytes(PathOnDisk, ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private sealed record Entry(CliProxyHub Hub, string Key);
}
