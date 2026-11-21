using System.Text;
using OpenUsage.Core.Providers.Antigravity;
using Xunit;

namespace OpenUsage.Tests;

public sealed class AntigravityAuthStoreTests : IDisposable
{
    private const string Fixture = """
        {"access_token":"fixture-access","refresh_token":"fixture-refresh","expiry":"2026-11-21T03:00:00Z"}
        """;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;
    private string LegacyPath => Path.Combine(_directory, AntigravityAuthStore.CacheRelative);
    private string ProtectedPath => Path.Combine(_directory, AntigravityAuthStore.ProtectedCacheRelative);

    [Fact]
    public void LocalCredentialProbeReadsLegacyCacheWithoutChangingIt()
    {
        WriteLegacy();
        Assert.Equal("fixture-access", CreateStore().Load()?.Access);
        Assert.Equal(Fixture, File.ReadAllText(LegacyPath));
        Assert.False(File.Exists(ProtectedPath));
    }

    [WindowsFact]
    public void RefreshMigratesLegacyCacheAndRestoresTokensFromProtectedFile()
    {
        WriteLegacy();
        var expected = CreateStore().Load();
        Assert.NotNull(expected);

        Assert.Equal(expected, CreateStore().Load(migrateCache: true));

        Assert.False(File.Exists(LegacyPath));
        Assert.DoesNotContain("fixture-access", Encoding.UTF8.GetString(File.ReadAllBytes(ProtectedPath)));
        Assert.DoesNotContain("fixture-refresh", Encoding.UTF8.GetString(File.ReadAllBytes(ProtectedPath)));
        Assert.Equal(expected, CreateStore().Load());
    }

    [WindowsFact]
    public void SavingRenewalKeepsCredentialManagerPriorityButStoresEncryptedFallback()
    {
        WriteLegacy();
        var store = new AntigravityAuthStore(_directory, _ => "fixture-keyring");
        store.SaveCache("fixture-renewed", "fixture-new-refresh", null);

        Assert.Equal("fixture-keyring", store.Load(migrateCache: true)?.Access);
        Assert.False(File.Exists(LegacyPath));
        Assert.Equal(("fixture-renewed", "fixture-new-refresh", (DateTimeOffset?)null),
            CreateStore().Load()!.Value);
    }

    [Fact]
    public void AFailedProtectedWriteKeepsTheLegacyCacheAndNeverWritesPlaintext()
    {
        WriteLegacy();
        Directory.CreateDirectory(ProtectedPath);

        Assert.Equal("fixture-access", CreateStore().Load(migrateCache: true)?.Access);
        CreateStore().SaveCache("fixture-renewed", "fixture-new-refresh", null);

        Assert.Equal(Fixture, File.ReadAllText(LegacyPath));
        Assert.Equal(LegacyPath, Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(LegacyPath)!)));
    }

    private AntigravityAuthStore CreateStore() => new(_directory, _ => null);

    private void WriteLegacy()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        File.WriteAllText(LegacyPath, Fixture);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI.";
        }
    }
}
