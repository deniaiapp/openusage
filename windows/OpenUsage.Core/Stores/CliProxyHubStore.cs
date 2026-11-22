using System.Security.Cryptography;
using System.Text.Json;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Stores;

internal sealed record CliProxyHub(Guid Id, Uri Address);

internal sealed class CliProxyHubStore(string? path = null)
{
    private readonly object _gate = new();
    private string PathOnDisk => path ?? Path.Combine(AppPaths.OpenUsageDir, "cliproxyapi-hubs.bin");

    internal IReadOnlyList<CliProxyHub> List()
    {
        lock (_gate) return Load().Select(entry => entry.Hub).ToArray();
    }

    internal string? KeyFor(CliProxyHub hub) => ReadKey(hub)?.Key;

    internal (string Key, Guid? Revision)? ReadKey(CliProxyHub hub)
    {
        lock (_gate)
        {
            var entry = Load().Find(entry => Matches(entry, hub));
            return entry is null ? null : (entry.Key, entry.KeyRevision);
        }
    }

    internal IReadOnlyList<CliProxyMirroredAccount> ListAccounts()
    {
        lock (_gate) return Load().SelectMany(entry => entry.Accounts ?? []).ToArray();
    }

    // The connection service must prove the key against the normalized address before saving it.
    internal CliProxyHub Add(Uri address, string validatedKey, IReadOnlyList<CliProxyHubLogin>? logins = null)
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
            entries.Add(new Entry(hub, key, logins is null ? null : Reconcile(hub, logins, []), Guid.NewGuid()));
            Save(entries);
            return hub;
        }
    }

    internal bool ReplaceKey(CliProxyHub hub, string validatedKey, IReadOnlyList<CliProxyHubLogin>? logins = null)
    {
        var key = CheckKey(validatedKey);
        lock (_gate)
        {
            var entries = Load();
            var index = entries.FindIndex(entry => Matches(entry, hub));
            if (index < 0) return false;
            var entry = entries[index];
            entries[index] = entry with
            {
                Key = key,
                KeyRevision = Guid.NewGuid(),
                Accounts = logins is null ? entry.Accounts : Reconcile(hub, logins, entry.Accounts ?? [])
            };
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

    internal bool UpdateAccounts(CliProxyHub hub, Guid? listedWithRevision, IReadOnlyList<CliProxyHubLogin> logins)
    {
        lock (_gate)
        {
            var entries = Load();
            var index = entries.FindIndex(entry => Matches(entry, hub) && entry.KeyRevision == listedWithRevision);
            if (index < 0) return false;
            var entry = entries[index];
            var accounts = Reconcile(hub, logins, entry.Accounts ?? []);
            if (!accounts.SequenceEqual(entry.Accounts ?? []))
            {
                entries[index] = entry with { Accounts = accounts };
                Save(entries);
            }
            return true;
        }
    }

    private static bool Matches(Entry entry, CliProxyHub hub) =>
        entry.Hub.Id == hub.Id && SameAddress(entry.Hub.Address, hub.Address);

    private static IReadOnlyList<CliProxyMirroredAccount> Reconcile(
        CliProxyHub hub, IReadOnlyList<CliProxyHubLogin> logins, IReadOnlyList<CliProxyMirroredAccount> accounts) =>
        CliProxyHubReconciliation.Create(hub.Id, logins, accounts).Apply(accounts);

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

    private sealed record Entry(CliProxyHub Hub, string Key, IReadOnlyList<CliProxyMirroredAccount>? Accounts, Guid? KeyRevision);
}
