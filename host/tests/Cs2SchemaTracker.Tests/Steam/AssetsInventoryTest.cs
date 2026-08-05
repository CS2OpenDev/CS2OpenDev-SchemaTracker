// AssetsInventory parser tests (the host-side data/cs2-assets-inventory.json reader
//
// These pin the parse: platform -> binary-depot derivation (depots[] role=="binary"), per-build
// per-platform GID extraction (builds[].binaries), and the fail-loud error shapes.

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public sealed class AssetsInventoryTest
{
    private const string Win = "windows-x86_64";
    private const string Lin = "linux-x86_64";

    private const string Good = """
    {
      "app": { "app_id": 730, "name": "Counter-Strike 2" },
      "depots": [
        { "depot_id": 2347771, "role": "binary",  "platforms": ["windows-x86_64"] },
        { "depot_id": 2347773, "role": "binary",  "platforms": ["linux-x86_64"] },
        { "depot_id": 2347770, "role": "content", "platforms": ["windows-x86_64","linux-x86_64"] },
        { "depot_id": 2347779, "role": "tools",   "platforms": ["windows-x86_64"] }
      ],
      "builds": [
        { "build_id": 100, "binaries": { "windows-x86_64": "111", "linux-x86_64": "222" }, "tools": "777" },
        { "build_id": 200, "binaries": { "windows-x86_64": "333" } },
        { "build_id": 300, "content": "999" }
      ]
    }
    """;

    [Fact]
    public void Parses_AppId_And_PlatformBinaryDepots()
    {
        var inv = AssetsInventory.Parse(Good);
        Assert.Equal(730u, inv.AppId);
        Assert.True(inv.HasBinaryDepotFor(Win));
        Assert.True(inv.HasBinaryDepotFor(Lin));
        Assert.Equal(SteamAppIdMap.Cs2WindowsBinariesDepotId, inv.BinaryDepotFor(Win));
        Assert.Equal(SteamAppIdMap.Cs2LinuxBinariesDepotId, inv.BinaryDepotFor(Lin));
    }

    [Fact]
    public void Content_Depot_And_PerBuild_Content_Gid_Are_Parsed()
    {
        var inv = AssetsInventory.Parse(Good);
        Assert.True(inv.HasContentDepot);

        // build 300 records content "999" -> a content target on the shared content depot.
        var t = inv.ContentTargetFor(300u);
        Assert.NotNull(t);
        Assert.Equal(300u, t!.BuildId);
        Assert.Equal(SteamAppIdMap.Cs2SharedContentDepotId, t.ContentDepotId);
        Assert.Equal(999UL, t.ManifestId);
        var spec = t.ToManifestSpec(inv.AppId);
        Assert.Equal(730u, spec.AppId);
        Assert.Contains(spec.Depots, d => d.DepotId == SteamAppIdMap.Cs2SharedContentDepotId);

        // build 100 has binaries but no `content` -> no content target (content omitted, not an error).
        Assert.Null(inv.ContentTargetFor(100u));
        // a build absent from the inventory -> null.
        Assert.Null(inv.ContentTargetFor(424242u));
    }

    [Fact]
    public void Tools_Depot_And_PerBuild_Tools_Gid_Are_Parsed()
    {
        var inv = AssetsInventory.Parse(Good);
        Assert.True(inv.HasToolsDepot);

        // build 100 records tools "777" -> a tools target on the Workshop Tools depot (2347779).
        var t = inv.ToolsTargetFor(100u);
        Assert.NotNull(t);
        Assert.Equal(100u, t!.BuildId);
        Assert.Equal(SteamAppIdMap.Cs2WorkshopToolsDepotId, t.ToolsDepotId);
        Assert.Equal(777UL, t.ManifestId);
        var spec = t.ToManifestSpec(inv.AppId);
        Assert.Equal(730u, spec.AppId);
        Assert.Equal(100u, spec.BuildId);
        Assert.Single(spec.Depots);
        Assert.Equal(SteamAppIdMap.Cs2WorkshopToolsDepotId, spec.Depots[0].DepotId);
        Assert.Equal(777UL, spec.Depots[0].ManifestId);

        // build 200 has binaries but no `tools` -> no tools target (tools omitted, not an error).
        Assert.Null(inv.ToolsTargetFor(200u));
        // a build absent from the inventory -> null.
        Assert.Null(inv.ToolsTargetFor(424242u));
    }

    [Fact]
    public void No_Tools_Depot_Means_No_Tools_Targets()
    {
        // An inventory with no role=="tools" depot yields no tools target even for a build
        // carrying a tools GID (there is no depot to acquire it from).
        var inv = AssetsInventory.Parse("""
        {
          "app": { "app_id": 730 },
          "depots": [ { "depot_id": 2347771, "role": "binary", "platforms": ["windows-x86_64"] } ],
          "builds": [ { "build_id": 100, "binaries": { "windows-x86_64": "111" }, "tools": "777" } ]
        }
        """);
        Assert.False(inv.HasToolsDepot);
        Assert.Null(inv.ToolsTargetFor(100u));
    }

    [Fact]
    public void Two_Tools_Depots_Are_FailLoud()
    {
        var json = """
        {
          "app": { "app_id": 730 },
          "depots": [
            { "depot_id": 2347779, "role": "tools", "platforms": ["windows-x86_64"] },
            { "depot_id": 9999999, "role": "tools", "platforms": ["windows-x86_64"] }
          ],
          "builds": []
        }
        """;
        Assert.Throws<InvalidDataException>(() => AssetsInventory.Parse(json));
    }

    [Fact]
    public void Non_Numeric_Tools_Gid_Is_FailLoud()
    {
        var json = """
        {
          "app": { "app_id": 730 },
          "depots": [ { "depot_id": 2347779, "role": "tools", "platforms": ["windows-x86_64"] } ],
          "builds": [ { "build_id": 100, "tools": "not-a-gid" } ]
        }
        """;
        Assert.Throws<InvalidDataException>(() => AssetsInventory.Parse(json));
    }

    private static readonly uint[] ExpectedWinBuilds = { 100u, 200u };
    private static readonly uint[] ExpectedLinBuilds = { 100u };

    [Fact]
    public void BuildsWithBinaryFor_Filters_By_Platform_Ascending()
    {
        var inv = AssetsInventory.Parse(Good);
        // build 300 has no binaries -> excluded; build 200 is windows-only.
        Assert.Equal(ExpectedWinBuilds, inv.BuildsWithBinaryFor(Win).ToArray());
        Assert.Equal(ExpectedLinBuilds, inv.BuildsWithBinaryFor(Lin).ToArray());
    }

    [Fact]
    public void TargetFor_Returns_The_Recorded_Gid_And_Depot()
    {
        var inv = AssetsInventory.Parse(Good);
        var t = inv.TargetFor(100u, Win);
        Assert.NotNull(t);
        Assert.Equal(100u, t!.BuildId);
        Assert.Equal(SteamAppIdMap.Cs2WindowsBinariesDepotId, t.BinaryDepotId);
        Assert.Equal(111UL, t.ManifestId);
        var spec = t.ToManifestSpec(inv.AppId);
        Assert.Equal(730u, spec.AppId);
        Assert.Equal(100u, spec.BuildId);
        Assert.Single(spec.Depots);
        Assert.Equal(SteamAppIdMap.Cs2WindowsBinariesDepotId, spec.Depots[0].DepotId);
        Assert.Equal(111UL, spec.Depots[0].ManifestId);
    }

    [Fact]
    public void TargetFor_Missing_Platform_Or_Build_Is_Null()
    {
        var inv = AssetsInventory.Parse(Good);
        Assert.Null(inv.TargetFor(200u, Lin));    // build 200 has no linux binary.
        Assert.Null(inv.TargetFor(99999u, Win));  // build not present.
        Assert.Null(inv.TargetFor(300u, Win));    // build 300 has no binaries block.
    }

    [Fact]
    public void ContainsBuild_Reflects_Builds_With_Binaries()
    {
        var inv = AssetsInventory.Parse(Good);
        Assert.True(inv.ContainsBuild(100u));
        Assert.True(inv.ContainsBuild(200u));
        // 300 has no binaries block -> not a selectable inventory build for the batch.
        Assert.False(inv.ContainsBuild(300u));
        Assert.False(inv.ContainsBuild(99999u));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]                                              // root not an object
    [InlineData("{ \"depots\": [], \"builds\": [] }")]            // no app.app_id
    [InlineData("{ \"app\": { \"app_id\": 730 }, \"builds\": [] }")]  // no depots[]
    [InlineData("{ \"app\": { \"app_id\": 730 }, \"depots\": [] }")]  // no builds[]
    public void Malformed_Inventory_Is_FailLoud(string json)
    {
        Assert.Throws<InvalidDataException>(() => AssetsInventory.Parse(json));
    }

    [Fact]
    public void Missing_File_Is_FailLoud()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-inventory-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<InvalidDataException>(() => AssetsInventory.Load(missing));
    }
}
