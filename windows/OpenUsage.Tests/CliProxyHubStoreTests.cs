using System.Security.Cryptography;
using System.Text;
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
