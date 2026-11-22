using System.Security.Cryptography;
using System.Text;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CliProxyHubStoreTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("openusage-hub-tests-");
    private string StorePath => Path.Combine(_directory.FullName, "hubs.bin");

    [WindowsFact]
    public void RestartKeepsIdentityAndBindsTheEncryptedKeyToTheExactAddress()
    {
        const string key = "fixture-private-hub-key";
        var store = new CliProxyHubStore(StorePath);
        var hub = store.Add(new Uri("https://example.test/Hubs/one"), key);
        var sibling = store.Add(new Uri("https://example.test/hubs/one"), key);

        Assert.DoesNotContain(key, Encoding.UTF8.GetString(File.ReadAllBytes(StorePath)));
        var reopened = new CliProxyHubStore(StorePath);
        Assert.Equal(new[] { hub, sibling }, reopened.List());
        Assert.Equal(key, reopened.KeyFor(hub));
        Assert.Null(reopened.KeyFor(hub with { Address = sibling.Address }));
        Assert.Null(reopened.KeyFor(hub with { Address = new Uri("/Hubs/one", UriKind.Relative) }));
        Assert.Null(reopened.KeyFor(hub with { Address = new Uri("https://example.test/Hubs/one#fragment") }));
        Assert.Throws<InvalidOperationException>(() => reopened.Add(new Uri("https://EXAMPLE.TEST:443/Hubs/one"), key));
        Assert.Equal(key, reopened.KeyFor(sibling));
    }

    [WindowsFact]
    public void ReplacementKeepsIdentityAndRemovalDropsOnlyThatHub()
    {
        var store = new CliProxyHubStore(StorePath);
        var first = store.Add(new Uri("https://one.example.test"), "first-fixture");
        var second = store.Add(new Uri("https://two.example.test"), "second-fixture");
        Assert.False(store.ReplaceKey(first with { Address = second.Address }, "unrelated-fixture"));
        Assert.Equal("first-fixture", store.KeyFor(first));

        Assert.True(store.ReplaceKey(first, "new-fixture"));
        var reopened = new CliProxyHubStore(StorePath);
        Assert.Equal(new[] { first, second }, reopened.List());
        Assert.Equal("new-fixture", reopened.KeyFor(first));
        Assert.True(reopened.Remove(first.Id));

        var afterRemoval = new CliProxyHubStore(StorePath);
        Assert.False(afterRemoval.Remove(first.Id));
        Assert.False(afterRemoval.ReplaceKey(first, "stale-fixture"));
        Assert.Null(afterRemoval.KeyFor(first));
        Assert.Equal(second, Assert.Single(afterRemoval.List()));
        Assert.Equal("second-fixture", afterRemoval.KeyFor(second));
    }

    [WindowsFact]
    public void LegacyStoreReopensAndSavesMirrorsOnlyOnTheMatchingHub()
    {
        var firstId = Guid.NewGuid();
        var legacy = $$"""
            [{"Hub":{"Id":"{{firstId}}","Address":"https://one.example.test/"},"Key":"legacy-fixture"}]
            """;
        File.WriteAllBytes(StorePath, ProtectedData.Protect(
            Encoding.UTF8.GetBytes(legacy), null, DataProtectionScope.CurrentUser));
        var store = new CliProxyHubStore(StorePath);
        var first = Assert.Single(store.List());
        Assert.Equal(firstId, first.Id);
        Assert.Equal("legacy-fixture", store.KeyFor(first));
        Assert.Empty(store.ListAccounts());

        CliProxyHubLogin[] logins =
        [
            new("fixture-login", "fixture-index", "codex", "fixture-label", true, false, "fixture-workspace")
        ];
        var second = store.Add(new Uri("https://two.example.test/"), "other-fixture", logins);
        var otherAccount = Assert.Single(store.ListAccounts());
        Assert.Equal(second.Id, otherAccount.Link.HubId);
        Assert.True(store.ReplaceKey(first, "new-fixture", logins));

        var reopened = new CliProxyHubStore(StorePath);
        Assert.Collection(reopened.ListAccounts(),
            account =>
            {
                Assert.Equal(firstId, account.Link.HubId);
                Assert.Equal("fixture-label", account.Label);
                Assert.Equal("fixture-workspace", account.Link.ChatGptAccountId);
                Assert.NotEqual(otherAccount.AccountId, account.AccountId);
            },
            account => Assert.Equal(otherAccount, account));
        var encrypted = Encoding.UTF8.GetString(File.ReadAllBytes(StorePath));
        Assert.DoesNotContain("fixture-workspace", encrypted);
        Assert.DoesNotContain("new-fixture", encrypted);

        Assert.True(reopened.ReplaceKey(first, "new-fixture", []));
        Assert.Equal(otherAccount, Assert.Single(reopened.ListAccounts()));
        Assert.True(reopened.Remove(second.Id));
        Assert.Empty(new CliProxyHubStore(StorePath).ListAccounts());
    }

    [WindowsFact]
    public void RuntimeKeyFollowsReplacementButNotRelinkedOrDeletedLogins()
    {
        var store = new CliProxyHubStore(StorePath);
        var login = new CliProxyHubLogin("fixture-login", "before", "codex", "Fixture", true, false, "workspace");
        var hub = store.Add(new Uri("https://hub.example/"), "fixture-key", [login]);
        var account = Assert.Single(store.ListAccounts());
        Assert.Equal("fixture-key", store.KeyFor(hub, account));
        Assert.Null(store.KeyFor(hub, account with { AccountId = Guid.NewGuid() }));
        Assert.Null(store.KeyFor(hub with { Address = new Uri("https://other.example/") }, account));

        Assert.True(store.ReplaceKey(hub, "fixture-replacement-key", [login]));
        Assert.Equal("fixture-replacement-key", store.KeyFor(hub, account));
        Assert.True(store.ReplaceKey(hub, "fixture-replacement-key", [login with { AuthIndex = "after" }]));
        var relinked = Assert.Single(store.ListAccounts());
        Assert.Equal(account.AccountId, relinked.AccountId);
        Assert.Null(store.KeyFor(hub, account));
        Assert.Equal("fixture-replacement-key", store.KeyFor(hub, relinked));
        Assert.True(store.Remove(hub.Id));
        Assert.Null(store.KeyFor(hub, relinked));
    }

    [WindowsFact]
    public void UnreadableStoreIsNotOverwritten()
    {
        byte[] broken = [0x01, 0x02, 0x03];
        File.WriteAllBytes(StorePath, broken);
        var store = new CliProxyHubStore(StorePath);
        Assert.Throws<CryptographicException>(() => store.Add(new Uri("https://example.test"), "fixture"));
        Assert.Equal(broken, File.ReadAllBytes(StorePath));
    }

    [Theory]
    [InlineData("/relative", "fixture")]
    [InlineData("ftp://example.test", "fixture")]
    [InlineData("https://user:password@example.test", "fixture")]
    [InlineData("https://example.test?token=fixture", "fixture")]
    [InlineData("https://example.test", " ")]
    [InlineData("https://example.test", "key\nwith-control")]
    public void RejectsUnusableAddressOrKeyWithoutCreatingStore(string address, string key)
    {
        var store = new CliProxyHubStore(StorePath);
        Assert.Throws<ArgumentException>(() => store.Add(new Uri(address, UriKind.RelativeOrAbsolute), key));
        Assert.False(File.Exists(StorePath));
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI.";
        }
    }
}
