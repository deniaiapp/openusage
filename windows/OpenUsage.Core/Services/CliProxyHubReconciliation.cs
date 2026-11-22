namespace OpenUsage.Core.Services;

internal sealed record CliProxyHubLink(Guid HubId, string LoginId, string AuthIndex, string? ChatGptAccountId);

internal sealed record CliProxyMirroredAccount(Guid AccountId, string ProviderId, CliProxyHubLink Link);

internal sealed record CliProxyHubAddition(string ProviderId, string Label, CliProxyHubLink Link);

internal sealed record CliProxyHubReconciliation(
    IReadOnlyList<CliProxyHubAddition> Added,
    IReadOnlyList<Guid> Removed,
    IReadOnlyDictionary<Guid, CliProxyHubLink> Relinked)
{
    public static CliProxyHubReconciliation Create(
        Guid hubId, IReadOnlyList<CliProxyHubLogin> logins, IReadOnlyList<CliProxyMirroredAccount> accounts)
    {
        var listed = new Dictionary<string, CliProxyHubLogin>(StringComparer.Ordinal);
        var ordered = new List<CliProxyHubLogin>();
        foreach (var login in logins)
        {
            if (listed.TryAdd(login.Id, login)) ordered.Add(login);
        }

        var mirrored = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<Guid>();
        var relinked = new Dictionary<Guid, CliProxyHubLink>();
        foreach (var account in accounts)
        {
            var link = account.Link;
            if (link.HubId != hubId) continue;
            if (!listed.TryGetValue(link.LoginId, out var login) || login.IsDisabled ||
                login.ProviderId != account.ProviderId || !mirrored.Add(login.Id))
            {
                removed.Add(account.AccountId);
                continue;
            }

            if (login.AuthIndex is { } index)
            {
                var current = link with { AuthIndex = index, ChatGptAccountId = login.ChatGptAccountId };
                if (current != link) relinked.Add(account.AccountId, current);
            }
        }

        var added = new List<CliProxyHubAddition>();
        foreach (var login in ordered)
        {
            if (!login.IsDisabled && login.ProviderId is { } provider && login.AuthIndex is { } index &&
                !mirrored.Contains(login.Id))
                added.Add(new(provider, login.Label, new(hubId, login.Id, index, login.ChatGptAccountId)));
        }
        return new(added, removed, relinked);
    }
}
