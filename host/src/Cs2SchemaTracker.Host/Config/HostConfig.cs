// Host configuration layer (Microsoft.Extensions.Configuration).
//
// Replaces ad-hoc, scattered environment-variable reads with a single config layer that
// binds an `appsettings.json` "Cs2SchemaTracker" section to a strongly-typed HostOptions
// record. The env vars (CS2_BINARIES_ROOT / CS2_WALKER_ERAS_ROOT / CS2_WALKER_BIN)
// STILL WORK and STILL WIN: effective accessors read the env var LIVE on every call and
// only fall back to appsettings, then to the built-in default. This keeps every existing
// test (which sets env vars at runtime) and CI unaffected — config is purely additive.
//
// DISCOVERY mirrors the .env loader (Steam/DotEnv.cs:45-64): look next to the executable
// (AppContext.BaseDirectory, the single-file publish output) AND walk up to the repo root
// so a checked-in dev appsettings.json works. The JSON is loaded ONCE (lazy, cached).
//
// CREDENTIAL/DETERMINISM HYGIENE (ethos): this layer ONLY LOCATES inputs
// and outputs. It NEVER logs a value, and NO config value may reach an emitted artifact
// byte. provenance.json/modules.json relativize their paths independently of this layer.

using Microsoft.Extensions.Configuration;

namespace Cs2SchemaTracker.Host.Config;

/// <summary>
/// Bound shape of the <c>Cs2SchemaTracker</c> section of <c>appsettings.json</c>. All values
/// default to <c>""</c> (empty), which means "no appsettings value" — the effective accessors
/// then fall through to the built-in default. Never bound directly by callers; read the
/// effective values via <see cref="HostConfig"/>.
/// </summary>
internal sealed record HostOptions
{
    /// <summary>External root for acquired CS2 binaries (env: <c>CS2_BINARIES_ROOT</c>).</summary>
    public string BinariesRoot { get; init; } = "";

    /// <summary>
    /// Root of the native per-era walker binaries, holding one <c>&lt;platform&gt;/</c> subdir per
    /// target with the per-era walker exes inside (env: <c>CS2_WALKER_ERAS_ROOT</c>). Empty = the
    /// <c>natives/</c> dir next to the host executable.
    /// </summary>
    public string NativesRoot { get; init; } = "";

    /// <summary>Explicit single walker-binary override (env: <c>CS2_WALKER_BIN</c>).</summary>
    public string WalkerBin { get; init; } = "";

    /// <summary>
    /// Path to the single-source assets inventory + era catalog (no env key). Empty = the repo-root
    /// default <c>data/cs2-assets-inventory.json</c>, resolved against the caller's repo root.
    /// </summary>
    public string InventoryPath { get; init; } = "";

    /// <summary>
    /// Explicit repo root the extract command resolves <c>artifacts/</c> + <c>data/</c> against
    /// (env: <c>CS2_REPO_ROOT</c>). Empty = discover it by walking up from the host executable to the
    /// <c>walker/CMakeLists.txt</c> sentinel. Set it when the host binary lives OUTSIDE the repo tree
    /// — e.g. a container image whose host is baked at a fixed path while the repo is bind-mounted —
    /// so <c>--all</c>/<c>--era</c> enumerate the mounted <c>artifacts/</c> and the inventory loads
    /// from the mounted <c>data/</c>. NOTE: <c>--commit</c> writes the produced set to a
    /// <c>cwd</c>-relative <c>artifacts/</c>, so run from (or set the container WORKDIR to) this root.
    /// </summary>
    public string RepoRoot { get; init; } = "";

    /// <summary>Default off-repo output root for non-committed extract runs (no env key).</summary>
    public string ExtractOutRoot { get; init; } = "";

    /// <summary>Default target platform for the extract command (no env key).</summary>
    public string ExtractPlatform { get; init; } = "";
}

/// <summary>
/// Static accessor for the host's effective configuration. Loads the appsettings.json
/// <c>Cs2SchemaTracker</c> section once (lazy), then resolves each value with precedence
/// <b>env var &gt; appsettings &gt; built-in default</b>, reading the env var LIVE on each call
/// so a runtime-set env var (tests, CI) always wins over the cached file value.
/// </summary>
internal static class HostConfig
{
    /// <summary>The appsettings.json section that holds host configuration.</summary>
    public const string SectionName = "Cs2SchemaTracker";

    /// <summary>The appsettings file name discovered next to the exe / up to the repo root.</summary>
    public const string FileName = "appsettings.json";

    /// <summary>Env var that overrides the repo root (artifacts/ + data/ base) for the extract command.</summary>
    public const string RepoRootEnvVar = "CS2_REPO_ROOT";

    private static readonly Lazy<HostOptions> Options = new(Load);

    /// <summary>
    /// Effective external root for acquired CS2 binaries: <c>CS2_BINARIES_ROOT</c> env var
    /// (live), else the appsettings value (when non-empty), else <c>null</c> (caller falls
    /// back to the in-repo cache convention).
    /// </summary>
    public static string? BinariesRoot
        => Resolve(Cs2SchemaTracker.Host.Cli.ExtractCommand.BinariesRootEnvVar, Options.Value.BinariesRoot);

    /// <summary>
    /// Effective root of the native per-era walker binaries (holds one <c>&lt;platform&gt;/</c> subdir
    /// per target): <c>CS2_WALKER_ERAS_ROOT</c> env var (live), else the appsettings value (when
    /// non-empty), else <c>null</c> (caller falls back to the <c>natives/</c> dir next to the exe).
    /// </summary>
    public static string? NativesRoot
        => Resolve(Walker.EraWalkerResolver.NativesRootEnvVar, Options.Value.NativesRoot);

    /// <summary>
    /// Effective assets-inventory path: <c>CS2_INVENTORY_PATH</c> env var (live), else the appsettings
    /// value (when non-empty), else <c>null</c> (the caller falls back to the repo-root-relative
    /// <c>data/cs2-assets-inventory.json</c>). The env override lets a prebuilt/relocated bundle point
    /// its host at a specific inventory (e.g. the checked-out repo's, so forward-capture updates the
    /// committed file rather than the bundle's shipped copy).
    /// </summary>
    public static string? InventoryPath
        => Resolve(Cs2SchemaTracker.Host.Inventory.InventoryCatalogProvider.InventoryPathEnvVar, Options.Value.InventoryPath);

    /// <summary>
    /// Effective repo root for the extract command's <c>artifacts/</c> + <c>data/</c>:
    /// <c>CS2_REPO_ROOT</c> env var (live), else the appsettings value (when non-empty), else
    /// <c>null</c> (the caller falls back to <see cref="Walker.EraWalkerResolver.DiscoverRepoRoot"/>,
    /// the walk-up-from-the-exe sentinel search — today's behavior). See <see cref="HostOptions.RepoRoot"/>.
    /// </summary>
    public static string? RepoRoot
        => Resolve(RepoRootEnvVar, Options.Value.RepoRoot);

    /// <summary>
    /// Effective explicit walker-binary override: <c>CS2_WALKER_BIN</c> env var (live), else the
    /// appsettings value (when non-empty), else <c>null</c> (caller falls back to era selection /
    /// the <c>walker/build</c> default).
    /// </summary>
    public static string? WalkerBin
        => Resolve(Walker.WalkerProcessRunner.BinaryPathEnvVar, Options.Value.WalkerBin);

    /// <summary>
    /// Effective default off-repo extract output root: appsettings value when non-empty, else
    /// <c>null</c> (the caller then falls back to the built-in <c>extract-out</c>). No env key.
    /// </summary>
    public static string? ExtractOutRoot
        => NullIfEmpty(Options.Value.ExtractOutRoot);

    /// <summary>
    /// Effective default extract platform: appsettings value when non-empty, else <c>null</c>
    /// (the caller then requires an explicit <c>--platform</c>). No env override key.
    /// </summary>
    public static string? ExtractPlatform
        => NullIfEmpty(Options.Value.ExtractPlatform);

    /// <summary>
    /// Resolve effective value: env var (live) wins, else the appsettings value (when non-empty),
    /// else <c>null</c>. The env read is intentionally NOT cached so a runtime-set env var wins.
    /// </summary>
    private static string? Resolve(string envKey, string appsettingsValue)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }
        return NullIfEmpty(appsettingsValue);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Build the configuration once: the <c>Cs2SchemaTracker</c> section of the discovered
    /// appsettings.json (optional) bound to <see cref="HostOptions"/>. Environment variables are
    /// also added as a source for completeness, but the canonical legacy-env precedence is enforced
    /// LIVE in <see cref="Resolve"/> (so runtime-set env vars win regardless of load timing).
    /// </summary>
    private static HostOptions Load()
    {
        var builder = new ConfigurationBuilder();

        var appsettingsPath = FindAppSettings(AppContext.BaseDirectory);
        if (appsettingsPath is not null)
        {
            builder.AddJsonFile(appsettingsPath, optional: true, reloadOnChange: false);
        }

        IConfiguration config = builder.Build();
        var options = new HostOptions();
        config.GetSection(SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Locate <c>appsettings.json</c>: next to the executable first (single-file publish output),
    /// else by walking up from <paramref name="startDir"/> to the repo root (dev runs). Mirrors the
    /// .env discovery in <see cref="Cs2SchemaTracker.Host.Steam.DotEnv.FindEnvFile"/>: stop climbing
    /// once a <c>.git</c> entry is passed without a hit. Null if none.
    /// </summary>
    internal static string? FindAppSettings(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            // Stop climbing once we pass the repo root (.git present but no appsettings.json).
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return null;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
