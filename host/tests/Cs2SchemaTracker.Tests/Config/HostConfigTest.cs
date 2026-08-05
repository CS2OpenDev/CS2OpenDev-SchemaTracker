// Host configuration layer (Microsoft.Extensions.Configuration) tests.
//
// Covers the additive config layer (HostConfig / HostOptions / appsettings.json):
//   - the appsettings discovery (FindAppSettings: next to the exe, then up to the repo root,
//     stopping at .git);
//   - the binding of the Cs2SchemaTracker section into HostOptions (the same builder+bind
//     HostConfig.Load uses), proving BinariesRoot / NativesRoot / WalkerBin resolve from file;
//   - the LIVE env-var override precedence (env > appsettings) through the public HostConfig
//     accessors;
//   - that the env keys are referenced through the production `public const` fields, not
//     duplicated string literals (a literal drift would break the contract silently).
//
// The class mutates process-global env vars (CS2_BINARIES_ROOT / CS2_WALKER_ERAS_ROOT /
// CS2_WALKER_BIN), so it joins the serialized "env-mutating" collection and snapshots +
// restores every var it touches in a finally. Deterministic: throwaway temp dirs, no
// wall-clock, no real walker / CS2 binaries / Steam.

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.Walker;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace Cs2SchemaTracker.Tests.Config;

[Collection("env-mutating")]
public sealed class HostConfigTest
{
    // The three env keys the config layer resolves, taken from the SAME production consts the
    // accessors use (NOT literals — so a const rename is caught here, not silently diverged).
    private static string BinariesEnv => ExtractCommand.BinariesRootEnvVar;
    private static string NativesEnv => EraWalkerResolver.NativesRootEnvVar;
    private static string WalkerBinEnv => WalkerProcessRunner.BinaryPathEnvVar;

    // Snapshot + restore the three env vars around a body; clear them first so the body starts
    // from a known (unset) baseline.
    private static void WithCleanEnv(Action body)
    {
        var oldBins = Environment.GetEnvironmentVariable(BinariesEnv);
        var oldNatives = Environment.GetEnvironmentVariable(NativesEnv);
        var oldBin = Environment.GetEnvironmentVariable(WalkerBinEnv);
        Environment.SetEnvironmentVariable(BinariesEnv, null);
        Environment.SetEnvironmentVariable(NativesEnv, null);
        Environment.SetEnvironmentVariable(WalkerBinEnv, null);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(BinariesEnv, oldBins);
            Environment.SetEnvironmentVariable(NativesEnv, oldNatives);
            Environment.SetEnvironmentVariable(WalkerBinEnv, oldBin);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hostcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Bind a written appsettings.json's Cs2SchemaTracker section into HostOptions exactly as
    // HostConfig.Load does (ConfigurationBuilder + AddJsonFile + GetSection(SectionName).Bind),
    // so this asserts the production binding wiring over a fixture file.
    private static HostOptions BindFrom(string appsettingsPath)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath, optional: false, reloadOnChange: false)
            .Build();
        var options = new HostOptions();
        config.GetSection(HostConfig.SectionName).Bind(options);
        return options;
    }

    // ---- appsettings binding ---------------------------------------------------------------

    [Fact]
    public void AppSettings_Binds_BinariesRoot_NativesRoot_WalkerBin_From_File()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, HostConfig.FileName);
            File.WriteAllText(path, """
            {
              "Cs2SchemaTracker": {
                "BinariesRoot": "/srv/cs2-binaries",
                "NativesRoot": "/srv/natives",
                "WalkerBin": "/srv/walker/cs2_schema_walker"
              }
            }
            """);

            var options = BindFrom(path);

            Assert.Equal("/srv/cs2-binaries", options.BinariesRoot);
            Assert.Equal("/srv/natives", options.NativesRoot);
            Assert.Equal("/srv/walker/cs2_schema_walker", options.WalkerBin);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AppSettings_Absent_Values_Default_To_Empty()
    {
        var dir = NewTempDir();
        try
        {
            // An appsettings with an EMPTY section: every value stays "" (the "no value" sentinel
            // the effective accessors translate to null -> built-in default).
            var path = Path.Combine(dir, HostConfig.FileName);
            File.WriteAllText(path, """{ "Cs2SchemaTracker": { } }""");

            var options = BindFrom(path);

            Assert.Equal("", options.BinariesRoot);
            Assert.Equal("", options.NativesRoot);
            Assert.Equal("", options.WalkerBin);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- FindAppSettings discovery ---------------------------------------------------------

    [Fact]
    public void FindAppSettings_Finds_File_Next_To_StartDir()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, HostConfig.FileName);
            File.WriteAllText(path, """{ "Cs2SchemaTracker": { } }""");

            var found = HostConfig.FindAppSettings(dir);
            Assert.Equal(path, found);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindAppSettings_Walks_Up_To_An_Ancestor()
    {
        var root = NewTempDir();
        try
        {
            // appsettings.json at the root; start two directories deeper.
            var path = Path.Combine(root, HostConfig.FileName);
            File.WriteAllText(path, """{ "Cs2SchemaTracker": { } }""");
            var deep = Path.Combine(root, "a", "b");
            Directory.CreateDirectory(deep);

            var found = HostConfig.FindAppSettings(deep);
            Assert.Equal(path, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindAppSettings_Stops_Climbing_At_RepoRoot_Sentinel()
    {
        var root = NewTempDir();
        try
        {
            // A .git marker at the root with NO appsettings.json: discovery must stop there and
            // return null rather than climbing into the real ancestors (which may carry one).
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: somewhere");
            var deep = Path.Combine(root, "child");
            Directory.CreateDirectory(deep);

            var found = HostConfig.FindAppSettings(deep);
            Assert.Null(found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- env-override precedence (live: env > appsettings) ----------------------------------

    [Fact]
    public void BinariesRoot_Env_Overrides_AppSettings_Value()
    {
        WithCleanEnv(() =>
        {
            // The cached appsettings (test bin appsettings.json) carries an EMPTY BinariesRoot, so
            // with no env var the accessor is null; setting the env var makes it win LIVE.
            Assert.Null(HostConfig.BinariesRoot);

            Environment.SetEnvironmentVariable(BinariesEnv, "/override/binaries");
            Assert.Equal("/override/binaries", HostConfig.BinariesRoot);
        });
    }

    [Fact]
    public void NativesRoot_Env_Overrides_AppSettings_Value()
    {
        WithCleanEnv(() =>
        {
            Assert.Null(HostConfig.NativesRoot);

            Environment.SetEnvironmentVariable(NativesEnv, "/override/natives");
            Assert.Equal("/override/natives", HostConfig.NativesRoot);
        });
    }

    [Fact]
    public void WalkerBin_Env_Overrides_AppSettings_Value()
    {
        WithCleanEnv(() =>
        {
            Assert.Null(HostConfig.WalkerBin);

            Environment.SetEnvironmentVariable(WalkerBinEnv, "/override/walker.exe");
            Assert.Equal("/override/walker.exe", HostConfig.WalkerBin);
        });
    }

    [Fact]
    public void Env_Read_Is_Live_So_Clearing_It_Falls_Back()
    {
        WithCleanEnv(() =>
        {
            Environment.SetEnvironmentVariable(BinariesEnv, "/first");
            Assert.Equal("/first", HostConfig.BinariesRoot);

            // The env var is read LIVE on each call (not cached) — clearing it falls back to the
            // (empty) appsettings -> null.
            Environment.SetEnvironmentVariable(BinariesEnv, null);
            Assert.Null(HostConfig.BinariesRoot);
        });
    }

    // ---- env keys come from the production consts -------------------------------------------

    [Fact]
    public void Env_Keys_Are_The_Documented_Production_Consts_Not_Literals()
    {
        // The config layer's accessors must resolve via these EXACT env-key consts. Pinning the
        // literal names here makes a const rename (which would silently break operator scripts +
        // CI) a compile/test failure, and proves the test above used the production keys.
        Assert.Equal("CS2_BINARIES_ROOT", ExtractCommand.BinariesRootEnvVar);
        Assert.Equal("CS2_WALKER_ERAS_ROOT", EraWalkerResolver.NativesRootEnvVar);
        Assert.Equal("CS2_WALKER_BIN", WalkerProcessRunner.BinaryPathEnvVar);

        // And the section/file-name constants are the ones discovery + binding key on.
        Assert.Equal("Cs2SchemaTracker", HostConfig.SectionName);
        Assert.Equal("appsettings.json", HostConfig.FileName);
    }
}
