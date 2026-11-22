using OpenUsage.Core.Services;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CliProxyHubReconciliationTests
{
    private static readonly Guid Hub = Guid.NewGuid();

    [Fact]
    public void AddsOnlyCallableSupportedLoginsInListingOrder()
    {
        var plan = CliProxyHubReconciliation.Create(Hub,
            [Login("z", "xai"), Login("no-index") with { AuthIndex = null }, Login("key") with { IsOAuth = false },
             Login("unknown", "other"), Login("disabled") with { IsDisabled = true }, Login("a"),
             Login("z", "devin"), Login("disabled")], []);

        Assert.Collection(plan.Added,
            item => Assert.Equal(new("grok", "z label", new(Hub, "z", "index-z", null)), item),
            item => Assert.Equal(new("codex", "a label", new(Hub, "a", "index-a", null)), item));
        Assert.Empty(plan.Removed);
        Assert.Empty(plan.Relinked);
    }

    [Fact]
    public void RemovesOnlyStaleAccountsForThisHubAndRelinksInPlace()
    {
        var missing = Account("missing");
        var disabled = Account("disabled");
        var changedKind = Account("changed-kind");
        var duplicate = Account("relinked");
        var other = Account("missing") with { Link = missing.Link with { HubId = Guid.NewGuid() } };
        var noIndex = Account("no-index");
        var relinked = Account("relinked");
        var plan = CliProxyHubReconciliation.Create(Hub,
            [Login("disabled") with { IsDisabled = true }, Login("changed-kind", "claude"),
             Login("no-index") with { AuthIndex = null, ChatGptAccountId = "ignore-until-callable" },
             Login("relinked") with { AuthIndex = "new-index", ChatGptAccountId = "fixture-workspace" }],
            [missing, disabled, changedKind, other, noIndex, relinked, duplicate]);

        Assert.Equal(new[] { missing.AccountId, disabled.AccountId, changedKind.AccountId, duplicate.AccountId },
            plan.Removed);
        Assert.Equal(new KeyValuePair<Guid, CliProxyHubLink>(relinked.AccountId,
            relinked.Link with { AuthIndex = "new-index", ChatGptAccountId = "fixture-workspace" }),
            Assert.Single(plan.Relinked));
        Assert.Equal("claude", Assert.Single(plan.Added).ProviderId);
    }

    [Fact]
    public void AlreadyAppliedListingProducesNoChanges()
    {
        var login = Login("existing") with { ChatGptAccountId = "fixture-workspace" };
        var existing = Account(login.Id);
        var plan = CliProxyHubReconciliation.Create(Hub, [login],
            [existing with { Link = existing.Link with { AuthIndex = login.AuthIndex!, ChatGptAccountId = login.ChatGptAccountId } }]);

        Assert.Empty(plan.Added);
        Assert.Empty(plan.Removed);
        Assert.Empty(plan.Relinked);
    }

    [Fact]
    public void ApplyingAListingPreservesKeptIdentityLabelAndOrderAndAppendsNewAccounts()
    {
        var kept = Account("kept");
        var removed = Account("removed");
        var otherHub = Guid.NewGuid();
        var other = Account("kept") with { Link = kept.Link with { HubId = otherHub } };
        var relisted = Login("kept") with { Label = "new label", ChatGptAccountId = "new workspace" };
        CliProxyMirroredAccount[] accounts = [kept, removed, other];
        CliProxyHubLogin[] listing = [Login("z"), relisted, Login("a", "devin")];

        var saved = CliProxyHubReconciliation.Create(Hub, listing, accounts).Apply(accounts);

        Assert.Collection(saved,
            account => Assert.Equal(kept with { Link = new(Hub, "kept", "index-kept", "new workspace") }, account),
            account => Assert.Equal(other, account),
            account => Assert.Equal(("codex", "z label", new CliProxyHubLink(Hub, "z", "index-z", null)),
                (account.ProviderId, account.Label, account.Link)),
            account => Assert.Equal(("devin", "a label", new CliProxyHubLink(Hub, "a", "index-a", null)),
                (account.ProviderId, account.Label, account.Link)));
        Assert.Equal(saved.Count, saved.Select(a => a.AccountId).Distinct().Count());
        Assert.Equal(saved, CliProxyHubReconciliation.Create(Hub, listing, saved).Apply(saved));
    }

    private static CliProxyHubLogin Login(string id, string provider = "codex") =>
        new(id, "index-" + id, provider, id + " label", true, false, null);

    private static CliProxyMirroredAccount Account(string id) =>
        new(Guid.NewGuid(), "codex", "saved " + id, new(Hub, id, "old-index", "old-workspace"));
}
