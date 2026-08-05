// SteamAnonymousAcquirer arg-validation (pre-Steam-contact) tests.
//
// The real acquirer's AcquireAsync should reject obviously-bad inputs before
// it opens a Steam connection (: don't make a network call you know
// won't succeed). These tests exercise that early-validation path.

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class SteamAcquirerArgValidationTest
{
    private static readonly uint[] SingleSharedDepot = { SteamAppIdMap.Cs2SharedContentDepotId };

    [Fact]
    public async Task AcquireAsync_throws_on_empty_depot_list()
    {
        var acquirer = new SteamAnonymousAcquirer();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            acquirer.AcquireAsync(
                appId: SteamAppIdMap.Cs2AppId,
                depotIds: Array.Empty<uint>(),
                buildId: 0,
                outDir: Path.GetTempPath(),
                ct: CancellationToken.None));
    }

    [Fact]
    public async Task AcquireAsync_throws_on_null_depot_list()
    {
        var acquirer = new SteamAnonymousAcquirer();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            acquirer.AcquireAsync(
                appId: SteamAppIdMap.Cs2AppId,
                depotIds: null!,
                buildId: 0,
                outDir: Path.GetTempPath(),
                ct: CancellationToken.None));
    }

    [Fact]
    public async Task AcquireAsync_throws_on_empty_outdir()
    {
        var acquirer = new SteamAnonymousAcquirer();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            acquirer.AcquireAsync(
                appId: SteamAppIdMap.Cs2AppId,
                depotIds: SingleSharedDepot,
                buildId: 0,
                outDir: "",
                ct: CancellationToken.None));
    }
}
