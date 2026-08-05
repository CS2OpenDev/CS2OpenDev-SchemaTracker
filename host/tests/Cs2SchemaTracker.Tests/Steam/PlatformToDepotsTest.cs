// PlatformToDepots map sanity tests.
//
// These are unit-only (no Steam contact). They lock in the 2-platform model
// (CS2 is one app 730; the per-OS binary depot ships both client + server):
//   - the two canonical platforms are mapped, and ONLY those two
//   - every plan is app 730 with a single non-zero binary depot
//   - the two platforms differ only in their per-OS binary depot
//   - unknown platforms throw with the known-platforms list in the message

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class PlatformToDepotsTest
{
    // CA1861-safe: static readonly avoids per-call array allocation.
    private static readonly string[] ExpectedKnownPlatforms =
    {
        "linux-x86_64",
        "windows-x86_64",
    };

    public static IEnumerable<object[]> KnownPlatforms =>
        PlatformToDepots.KnownPlatforms.Select(p => new object[] { p });

    [Fact]
    public void KnownPlatforms_contains_exactly_two_v1_platforms()
    {
        Assert.Equal(
            ExpectedKnownPlatforms,
            PlatformToDepots.KnownPlatforms.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [MemberData(nameof(KnownPlatforms))]
    public void Resolve_returns_cs2_app_id_for_known_platform(string platform)
    {
        // CS2 is ONE app — every platform is scoped to 730. There is no
        // dedicated-server app (the fabricated 2347780 was deleted).
        var plan = PlatformToDepots.Resolve(platform);
        Assert.Equal(SteamAppIdMap.Cs2AppId, plan.AppId);
    }

    [Theory]
    [MemberData(nameof(KnownPlatforms))]
    public void Resolve_returns_single_nonzero_binary_depot(string platform)
    {
        var plan = PlatformToDepots.Resolve(platform);
        // Binaries-only: exactly one depot, never the shared content depot.
        Assert.Single(plan.DepotIds);
        Assert.All(plan.DepotIds, id => Assert.NotEqual(0u, id));
        Assert.DoesNotContain(SteamAppIdMap.Cs2SharedContentDepotId, plan.DepotIds);
    }

    [Fact]
    public void Windows_platform_maps_to_windows_binary_depot()
    {
        var plan = PlatformToDepots.Resolve("windows-x86_64");
        Assert.Equal(SteamAppIdMap.Cs2AppId, plan.AppId);
        Assert.Equal(new[] { SteamAppIdMap.Cs2WindowsBinariesDepotId }, plan.DepotIds);
    }

    [Fact]
    public void Linux_platform_maps_to_linux_binary_depot()
    {
        var plan = PlatformToDepots.Resolve("linux-x86_64");
        Assert.Equal(SteamAppIdMap.Cs2AppId, plan.AppId);
        Assert.Equal(new[] { SteamAppIdMap.Cs2LinuxBinariesDepotId }, plan.DepotIds);
    }

    [Fact]
    public void Platforms_differ_only_in_per_os_binary_depot()
    {
        var linux = PlatformToDepots.Resolve("linux-x86_64");
        var windows = PlatformToDepots.Resolve("windows-x86_64");
        Assert.Equal(linux.AppId, windows.AppId);              // same app 730
        Assert.NotEqual(linux.DepotIds, windows.DepotIds);     // different depot
    }

    [Fact]
    public void Resolve_throws_with_known_platform_list_on_bogus_platform()
    {
        // The old tuple names must now be rejected — *.client / *.server are gone.
        var ex = Assert.Throws<ArgumentException>(() => PlatformToDepots.Resolve("windows-x86_64.server"));
        Assert.Contains("Unknown platform", ex.Message);
        foreach (var known in PlatformToDepots.KnownPlatforms)
        {
            Assert.Contains(known, ex.Message);
        }
    }

    [Fact]
    public void IsKnown_true_for_each_platform_and_false_for_old_tuple_names()
    {
        foreach (var platform in PlatformToDepots.KnownPlatforms)
        {
            Assert.True(PlatformToDepots.IsKnown(platform));
        }
        // Old 4-tuple names are no longer valid.
        Assert.False(PlatformToDepots.IsKnown("windows-x86_64.client"));
        Assert.False(PlatformToDepots.IsKnown("linux-x86_64.server"));
        Assert.False(PlatformToDepots.IsKnown("mac-arm64"));
        Assert.False(PlatformToDepots.IsKnown("bogus"));
        Assert.False(PlatformToDepots.IsKnown(""));
        Assert.False(PlatformToDepots.IsKnown(null!));
    }
}
