// HostPlatform mapping + ExtractCommand cross-OS guard tests.
//
// Two layers:
//   1. Host-independent mapping: platform -> required host, known-platform set,
//      unknown-platform rejection, message content (deterministic, no timestamps).
//   2. The ExtractCommand guard's exit code on THIS host. Because the guard's
//      pass/fail depends on the runner's OS+arch, those facts branch on the
//      current host: a matching platform must reach the scaffolding (exit 65),
//      a non-matching platform must hit the guard (exit 70), and an unknown
//      platform always exits 64. This keeps the suite green on Linux, Windows,
//      and macOS.
//
// PLATFORM MODEL (v0.2): two platforms only — "windows-x86_64" / "linux-x86_64".
// client/server is the per-class SchemaClass.module tag, not a platform dimension.
//
// HostPlatform / ExtractCommand are internal — reached via the host project's
// InternalsVisibleTo.

using System.Runtime.InteropServices;

using Cs2SchemaTracker.Host.Cli;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

public class HostPlatformTest
{
    // CA1861: hoist constant array arguments to static readonly fields.
    private static readonly string[] BothPlatforms =
    {
        "linux-x86_64",
        "windows-x86_64",
    };

    private static readonly string[] LinuxPlatformsSorted = { "linux-x86_64" };

    private static readonly string[] WindowsPlatformsSorted = { "windows-x86_64" };

    [Theory]
    [InlineData("linux-x86_64", "Linux x86_64")]
    [InlineData("windows-x86_64", "Windows x86_64")]
    public void Known_platform_maps_to_expected_required_host(string platform, string expectedLabel)
    {
        // Public test signatures can't carry the internal RequiredHost enum, so
        // assert through the label the guidance message uses.
        Assert.True(HostPlatform.TryGetRequiredHost(platform, out var required));
        Assert.Equal(expectedLabel, HostPlatform.RequiredHostLabel(required));
        Assert.True(HostPlatform.IsKnownPlatform(platform));
    }

    [Theory]
    [InlineData("mac-arm64")]
    [InlineData("linux-x86_64.server")]   // the old tuple form is no longer a platform
    [InlineData("windows-x86_64.client")]
    [InlineData("")]
    [InlineData("LINUX-X86_64")]          // case-sensitive: platforms are lowercase
    public void Unknown_platform_is_rejected(string platform)
    {
        Assert.False(HostPlatform.TryGetRequiredHost(platform, out _));
        Assert.False(HostPlatform.IsKnownPlatform(platform));
        Assert.False(HostPlatform.CanExtractPlatform(platform));
    }

    [Fact]
    public void Known_platform_set_is_exactly_the_two_contract_platforms()
    {
        Assert.Equal(BothPlatforms, HostPlatform.KnownPlatforms);
    }

    [Fact]
    public void Unknown_platform_message_lists_both_valid_platforms()
    {
        var msg = HostPlatform.UnknownPlatformMessage("mac-arm64");
        Assert.Contains("mac-arm64", msg);
        foreach (var p in HostPlatform.KnownPlatforms)
        {
            Assert.Contains(p, msg);
        }
    }

    [Theory]
    [InlineData("linux-x86_64", "Linux x86_64")]
    [InlineData("windows-x86_64", "Windows x86_64")]
    public void Cross_os_guidance_names_required_host_and_a_runner_or_vm(string platform, string requiredLabel)
    {
        Assert.True(HostPlatform.TryGetRequiredHost(platform, out var required));
        var msg = HostPlatform.CrossOsGuidanceMessage(platform, required);
        Assert.Contains(platform, msg);
        Assert.Contains(requiredLabel, msg);
        // Documented "use a Linux/Windows runner/VM" guidance.
        Assert.Contains("runner or VM", msg);
    }

    [Fact]
    public void Guidance_message_is_deterministic_across_calls()
    {
        Assert.True(HostPlatform.TryGetRequiredHost("linux-x86_64", out var required));
        var a = HostPlatform.CrossOsGuidanceMessage("linux-x86_64", required);
        var b = HostPlatform.CrossOsGuidanceMessage("linux-x86_64", required);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Can_extract_matches_current_host_os()
    {
        // Exactly the platform whose required host equals this runner's OS+arch.
        var matching = HostPlatform.KnownPlatforms.Where(HostPlatform.CanExtractPlatform).ToList();

        if (RuntimeInformation.OSArchitecture == Architecture.X64 &&
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Equal(LinuxPlatformsSorted, matching.OrderBy(x => x, StringComparer.Ordinal));
        }
        else if (RuntimeInformation.OSArchitecture == Architecture.X64 &&
                 RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(WindowsPlatformsSorted, matching.OrderBy(x => x, StringComparer.Ordinal));
        }
        else
        {
            // macOS / non-x64 host: matches NEITHER platform.
            Assert.Empty(matching);
        }
    }
}

public class ExtractCommandGuardTest
{
    private static readonly string[] ArgsHelp = { "--help" };
    private static readonly string[] ArgsMissingBuild = { "--platform", "linux-x86_64" };
    private static readonly string[] ArgsMissingPlatform = { "--build", "latest" };
    private static readonly string[] ArgsUnknownPlatform = { "--build", "latest", "--platform", "mac-arm64" };

    [Fact]
    public void Help_flag_exits_zero()
    {
        Assert.Equal(0, ExtractCommand.Run(ArgsHelp));
    }

    [Fact]
    public void Missing_build_or_platform_exits_64()
    {
        Assert.Equal(64, ExtractCommand.Run(ArgsMissingBuild));
        Assert.Equal(64, ExtractCommand.Run(ArgsMissingPlatform));
    }

    [Fact]
    public void Unknown_platform_exits_64_before_any_work()
    {
        var code = ExtractCommand.Run(ArgsUnknownPlatform);
        Assert.Equal(64, code);
    }

    [Theory]
    [InlineData("linux-x86_64")]
    [InlineData("windows-x86_64")]
    public void Guard_exit_code_depends_on_whether_this_host_matches_the_platform(string platform)
    {
        // --no-acquire pins this to the opt-out path: with the input binaries absent
        // (build 'latest' is never cached), the matching host fails loud at EX_DATAERR (65)
        // deterministically and WITHOUT touching Steam. Without it, extract now auto-acquires
        // by default, so the exit code would depend on a real acquire attempt — non-
        // deterministic and order-dependent on any leaked cache/env state. This test asserts the
        // guard + fail-loud contract, not acquisition, so it opts out of auto-acquire explicitly.
        var code = ExtractCommand.Run(new[] { "--build", "latest", "--platform", platform, "--no-acquire" });

        if (HostPlatform.CanExtractPlatform(platform))
        {
            // Matching host: proceeds PAST the guard into the orchestration.
            // With no acquired binaries present for build 'latest' and auto-acquire opted out,
            // binary resolution fails loud at EX_DATAERR (65) — never a partial set, never 0.
            // (The cross-OS guard, exit 70, is NOT reached on a matching host.)
            Assert.Equal(65, code);
        }
        else
        {
            // Cross-OS host: guard fires non-zero (EX_SOFTWARE).
            Assert.Equal(70, code);
        }
    }
}
