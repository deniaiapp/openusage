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

    private static CliProxyHubLogin Login(string id, string provider = "codex") =>
        new(id, "index-" + id, provider, id + " label", true, false, null);

    private static CliProxyMirroredAccount Account(string id) =>
        new(Guid.NewGuid(), "codex", new(Hub, id, "old-index", "old-workspace"));
}
