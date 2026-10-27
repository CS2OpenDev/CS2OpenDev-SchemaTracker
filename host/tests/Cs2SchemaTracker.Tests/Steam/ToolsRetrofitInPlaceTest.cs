// The tools acquire retrofits IN PLACE (reusing the on-disk DLLs) only when the tuple already
// records the exact tools manifest being fetched. Any other record keeps the staged path, so DLLs
// from a different manifest are never mistaken for this one's.

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public sealed class ToolsRetrofitInPlaceTest : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs2-tools-retrofit-" + Guid.NewGuid().ToString("N"));

    public ToolsRetrofitInPlaceTest() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private void WriteRecord(string depotsJson)
        => File.WriteAllText(Path.Combine(_dir, ManifestRecord.FileName), $$"""{"appId":730,"buildId":1,"depots":{{depotsJson}}}""");

    [Fact]
    public void Same_tools_manifest_recorded_retrofits_in_place()
    {
        WriteRecord("""[{"depotId":2347771,"manifestId":"111"},{"depotId":2347779,"manifestId":"777"}]""");
        Assert.True(SteamAnonymousAcquirer.ToolsManifestRecorded(_dir, 2347779, 777));
    }

    [Fact]
    public void A_different_tools_manifest_keeps_the_staged_path()
    {
        WriteRecord("""[{"depotId":2347771,"manifestId":"111"},{"depotId":2347779,"manifestId":"778"}]""");
        Assert.False(SteamAnonymousAcquirer.ToolsManifestRecorded(_dir, 2347779, 777));
    }

    [Fact]
    public void No_tools_depot_or_no_record_keeps_the_staged_path()
    {
        Assert.False(SteamAnonymousAcquirer.ToolsManifestRecorded(_dir, 2347779, 777));
        WriteRecord("""[{"depotId":2347771,"manifestId":"777"}]""");
        Assert.False(SteamAnonymousAcquirer.ToolsManifestRecorded(_dir, 2347779, 777));
    }
}
