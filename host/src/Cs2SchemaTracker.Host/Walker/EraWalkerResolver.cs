// Host-side era -> walker-binary resolver, keyed on the single-source inventory + era catalog
// (data/cs2-assets-inventory.json: eras[] + builds[].era).
//
// Per-era binary selection, host-selected at runtime:
//
//   1. Determine the build's era:
//        - A build present in inventory.builds -> that build's EXACT era id (authoritative; no
//          provenance.json read).
//        - A fresh/unknown build (not in builds[]) -> the NEWEST compile-pin era (eras[0]); the
//          post-load second gate then validates the layout (never a silent wrong-era walk).
//   2. Resolve the walker binary path from the resolved era:
//        - compile-pin era  -> walker name = the era id itself.
//        - runtime-variant  -> walker name = the compile-pin era whose hl2sdkSha == ridesCompilePin
//          (variants reuse the ridden compile era's binary; fail loud if none matches).
//        Path = <NativesRoot>/<platform>/<walkerName> (+ .exe on windows-x86_64).
//   3. Expected layout signature for the post-load SECOND GATE:
//        - compile-pin      -> era.layoutSignatures[platform] (null when the platform is not
//          validated for this era — an armed-but-null gate that fails loud, never accepts).
//        - runtime-variant  -> the RIDDEN compile era's layoutSignatures[platform] (the walker
//                              emits its compile-time signature; variantSignature is the walker's
//                              internal runtime-allow-list fingerprint, not the emitted one).
//   4. Class-count band (feeds the batch class gate): the resolved era's minClasses/maxClasses.
//
// CS2_WALKER_BIN is an explicit single-binary override that BYPASSES era->binary resolution
// entirely (dev/test escape hatch). When set, this resolver returns that path but still carries the
// resolved era's expected signature so the second gate stays meaningful.
//
// This resolver does NO process launching and NO Steam work; it is a pure path+metadata computation
// over the inventory (loaded once, lazily), so it is unit-testable with fixture inventories.

using Cs2SchemaTracker.Host.Inventory;

using Microsoft.Extensions.Options;

namespace Cs2SchemaTracker.Host.Walker;

/// <summary>The resolved per-(build, platform) era + walker binary + gate metadata.</summary>
/// <param name="ExpectedLayoutSignature">
/// The resolved era's expected layout signature FOR THE RESOLVED PLATFORM, or <c>null</c> when this
/// era has no registered/validated signature for that platform. A <c>null</c> here does NOT disable
/// the gate: the host's post-load second gate treats an armed-but-null expectation as a fail-loud
/// unvalidated layout — never accept a layout not validated for the running host.
/// </param>
internal sealed record EraResolution(
    string Era,
    string Pin,
    string WalkerBinaryPath,
    string? ExpectedLayoutSignature,
    bool FromExplicitOverride,
    bool Kv3ClassDefaults);

/// <summary>
/// The entity_schema class-count band for a (build, platform): the resolved era's own
/// minClasses/maxClasses. Because <c>builds[].era</c> is EXACT (a variant build already names the
/// variant era), there is no separate variant-window override — the band is just the resolved era's.
/// </summary>
internal sealed record EffectiveClassBand(
    string Era,
    int? MinClasses,
    int? MaxClasses);

/// <summary>
/// Selection-only view of a (build, platform)'s resolved era: its id + pin + expected signature +
/// class band, WITHOUT a walker binary path. Used by committed-build discovery (re-walk selection).
/// </summary>
internal sealed record EraSelection(
    string Era,
    string Pin,
    string? ExpectedLayoutSignature,
    int? MinClasses,
    int? MaxClasses);

/// <summary>
/// A build id resolved to its era + pin, for inventory-driven batch selection (--all / --era /
/// --pin). Sourced from the inventory's <c>builds[]</c> — the full known corpus — NOT from the
/// committed <c>artifacts/</c> tree.
/// </summary>
internal sealed record InventoryBuildRef(string Build, string Era, string Pin);

/// <summary>
/// Resolves the per-era walker binary + that era's expected layout signature + class band for a
/// (build, platform), over the single-source inventory/era catalog. The expected signature feeds the
/// host's post-load second gate (ExtractCommand.RunExtract).
/// </summary>
internal sealed class EraWalkerResolver
{
    /// <summary>Env var pointing at the natives root (holds one <c>&lt;platform&gt;/</c> subdir per target).</summary>
    public const string NativesRootEnvVar = "CS2_WALKER_ERAS_ROOT";

    private readonly string _repoRoot;
    private readonly Lazy<InventoryCatalog> _catalog;

    /// <summary>
    /// The resolved repo root this resolver computes everything against (artifacts/, data/).
    /// Callers that share a single resolver instance MUST derive their own repo-root-relative paths
    /// (build selection, --verify base) from this, so the whole command uses ONE repo-root source.
    /// </summary>
    internal string RepoRoot => _repoRoot;

    /// <param name="repoRoot">
    /// Repo root containing the <c>artifacts/</c> and <c>data/</c> dirs. When null it is discovered by
    /// walking up from the host executable (sentinel <c>walker/CMakeLists.txt</c>).
    /// </param>
    /// <param name="catalog">
    /// The inventory/era catalog as <see cref="IOptions{InventoryCatalog}"/>. When null it is loaded
    /// lazily from the resolved inventory path (<see cref="InventoryCatalogProvider.ResolveInventoryPath"/>).
    /// </param>
    public EraWalkerResolver(string? repoRoot = null, IOptions<InventoryCatalog>? catalog = null)
    {
        _repoRoot = repoRoot is not null ? Path.GetFullPath(repoRoot) : DiscoverRepoRoot();
        _catalog = catalog is not null
            ? new Lazy<InventoryCatalog>(() => catalog.Value)
            : new Lazy<InventoryCatalog>(
                () => InventoryCatalog.LoadFromFile(InventoryCatalogProvider.ResolveInventoryPath(_repoRoot)));
    }

    /// <summary>
    /// Resolve the era + walker binary + expected layout signature for (build, platform). Throws on
    /// any fail-loud condition (a build referencing an unknown era, a runtime-variant whose ridden
    /// compile pin is absent, a corrupt/missing inventory). Never guesses.
    /// </summary>
    public EraResolution Resolve(string build, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(build);
        ArgumentException.ThrowIfNullOrEmpty(platform);

        Resolved core = ResolveCore(build, platform);

        // CS2_WALKER_BIN bypass: an explicit single-binary override skips era->binary resolution
        // entirely. The expected signature is still the resolved era's, so the second gate stays
        // meaningful. CS2_WALKER_BIN env (live) wins, else appsettings WalkerBin.
        var explicitOverride = Cs2SchemaTracker.Host.Config.HostConfig.WalkerBin;
        if (!string.IsNullOrWhiteSpace(explicitOverride))
        {
            return new EraResolution(
                Era: core.EraId,
                Pin: core.Pin,
                WalkerBinaryPath: Path.GetFullPath(explicitOverride),
                ExpectedLayoutSignature: core.ExpectedLayoutSignature,
                FromExplicitOverride: true,
                Kv3ClassDefaults: core.Kv3ClassDefaults);
        }

        return new EraResolution(
            Era: core.EraId,
            Pin: core.Pin,
            WalkerBinaryPath: BuildWalkerBinaryPath(core.WalkerName, platform),
            ExpectedLayoutSignature: core.ExpectedLayoutSignature,
            FromExplicitOverride: false,
            Kv3ClassDefaults: core.Kv3ClassDefaults);
    }

    /// <summary>
    /// SELECTION-ONLY era resolution: map (build, platform) to its <see cref="EraSelection"/> (era id,
    /// pin, expected signature, class band) WITHOUT a walker binary path. Used by re-walk
    /// committed-build discovery to bucket/filter builds by era/pin. Fail-loud on a corrupt catalog.
    /// </summary>
    internal EraSelection DetermineEraOnly(string build, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(build);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        Resolved core = ResolveCore(build, platform);
        return new EraSelection(core.EraId, core.Pin, core.ExpectedLayoutSignature, core.MinClasses, core.MaxClasses);
    }

    /// <summary>
    /// Enumerate the inventory's <c>builds[]</c> for <paramref name="platform"/> as
    /// <see cref="InventoryBuildRef"/> (id + resolved era + pin). This is the corpus --all / --era /
    /// --pin select over — every KNOWN build, whether or not it has been walked/committed yet — as
    /// opposed to <see cref="CommittedBuilds"/> (the artifacts/ tree). A build is included unless the
    /// inventory explicitly records OTHER platforms for it but not this one (its <c>binaries</c> map
    /// is present, non-empty, and lacks <paramref name="platform"/>) — that build was released for
    /// other platforms only and cannot be walked here. An empty/absent <c>binaries</c> map is treated
    /// as unknown and included (attempt it). Result is Ordinal-ordered by build id (stable batch).
    /// </summary>
    internal IReadOnlyList<InventoryBuildRef> EnumerateInventoryBuilds(string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(platform);
        var catalog = _catalog.Value;
        var result = new List<InventoryBuildRef>(catalog.Builds.Count);
        foreach (var b in catalog.Builds)
        {
            if (b.Binaries is { Count: > 0 } bins && !bins.ContainsKey(platform))
            {
                continue;   // released for other platforms only — cannot walk it here.
            }
            var buildStr = b.BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Resolved core = ResolveCore(buildStr, platform);
            result.Add(new InventoryBuildRef(buildStr, core.EraId, core.Pin));
        }
        result.Sort((x, y) => string.CompareOrdinal(x.Build, y.Build));
        return result;
    }

    /// <summary>
    /// The entity_schema class-count band for (build, platform): the resolved era's own
    /// minClasses/maxClasses. Since <c>builds[].era</c> names the EXACT era (a variant build already
    /// resolves to its variant era), the band needs no separate variant-window override.
    /// </summary>
    internal EffectiveClassBand DetermineEffectiveClassBand(string build, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(build);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        Resolved core = ResolveCore(build, platform);
        return new EffectiveClassBand(core.EraId, core.MinClasses, core.MaxClasses);
    }

    /// <summary>
    /// The class-count band to gate <paramref name="platform"/> against: the era's per-platform
    /// <c>classBands</c> entry when present, else its platform-agnostic minClasses/maxClasses.
    /// </summary>
    /// <remarks>
    /// Per-platform is authoritative because the Workshop Tools depot ships windows-only: a windows
    /// walk loads ~19 tool modules a linux walk cannot, putting the two platforms in clearly
    /// separated ranges within the SAME era. One band spanning both would be too wide to catch a
    /// real regression, so the flat fields survive only as a fallback for eras not yet calibrated
    /// per platform.
    /// </remarks>
    private static (int? Min, int? Max) ClassBandFor(InventoryEra era, string platform)
    {
        if (era.ClassBands is not null &&
            era.ClassBands.TryGetValue(platform, out var band) &&
            band is not null &&
            (band.Min is not null || band.Max is not null))
        {
            return (band.Min, band.Max);
        }
        return (era.MinClasses, era.MaxClasses);
    }

    /// <summary>The internal fully-resolved era facts every public method projects from.</summary>
    private sealed record Resolved(
        string EraId,
        string Pin,
        string WalkerName,
        string? ExpectedLayoutSignature,
        int? MinClasses,
        int? MaxClasses,
        bool Kv3ClassDefaults);

    /// <summary>
    /// Resolve the era (by exact <c>builds[].era</c>, else the newest compile-pin era for a fresh
    /// build) and project its walker name, pin, expected signature, and class band.
    /// </summary>
    private Resolved ResolveCore(string build, string platform)
    {
        var catalog = _catalog.Value;

        // Step 1 — determine the era.
        InventoryEra era;
        if (long.TryParse(build, System.Globalization.CultureInfo.InvariantCulture, out var buildId) &&
            catalog.FindBuild(buildId) is { } row)
        {
            era = catalog.FindEra(row.Era)
                ?? throw new InvalidDataException(
                    $"inventory build {build} references era '{row.Era}', which is not in eras[]. " +
                    "The inventory is inconsistent (never guess an unknown era).");
        }
        else
        {
            // Fresh / unknown build -> optimistic forward-capture on the newest compile-pin era; the
            // second gate then validates whether it really is that layout.
            era = catalog.NewestCompilePinEra();
        }

        // Step 2/3/4 — walker name + pin + expected signature + class band, by era kind.
        if (era.IsCompilePin)
        {
            if (string.IsNullOrEmpty(era.Hl2SdkSha))
            {
                throw new InvalidDataException(
                    $"inventory compile-pin era '{era.Era}' has no hl2sdkSha (never guess a pin).");
            }
            string? sig = era.LayoutSignatures is not null &&
                          era.LayoutSignatures.TryGetValue(platform, out var s) ? s : null;
            (int? bandMin, int? bandMax) = ClassBandFor(era, platform);
            return new Resolved(era.Era, era.Hl2SdkSha, era.Era, sig, bandMin, bandMax, era.Kv3ClassDefaults);
        }

        if (era.IsRuntimeVariant)
        {
            if (string.IsNullOrEmpty(era.RidesCompilePin))
            {
                throw new InvalidDataException(
                    $"inventory runtime-variant era '{era.Era}' has no ridesCompilePin (never guess).");
            }
            InventoryEra ridden = catalog.FindCompilePinBySha(era.RidesCompilePin)
                ?? throw new InvalidDataException(
                    $"inventory runtime-variant era '{era.Era}' ridesCompilePin '{era.RidesCompilePin}' " +
                    "matches no compile-pin era in eras[] — the variant has no walker binary to ride " +
                    "(never guess).");
            // Variants reuse the ridden compile era's binary (walker name = the ridden era id) and
            // keep the ridden hl2sdk pin. The host SECOND GATE compares the walker's EMITTED
            // SchemaSystemLayoutSignature, which is the ridden binary's COMPILE-TIME signature (the
            // walker detects the runtime variant internally but still emits its compile fingerprint),
            // so the expected gate signature is the RIDDEN compile era's per-platform signature — NOT
            // `variantSignature` (that is the walker's internal runtime-allow-list fingerprint, not
            // the emitted one). The class band stays the variant's OWN (2023 layouts have far fewer
            // classes than the modern ridden era).
            string? riddenSig = ridden.LayoutSignatures is not null &&
                                ridden.LayoutSignatures.TryGetValue(platform, out var rs) ? rs : null;
            // The band stays the VARIANT's own (its layout has far fewer classes than the ridden
            // modern era), resolved for this platform.
            (int? vMin, int? vMax) = ClassBandFor(era, platform);
            // The variant era's OWN kv3ClassDefaults (2023 variants carry no MGetKV3ClassDefaults
            // metadata, so this is a no-op there regardless; default true).
            return new Resolved(
                era.Era, era.RidesCompilePin, ridden.Era, riddenSig, vMin, vMax, era.Kv3ClassDefaults);
        }

        throw new InvalidDataException(
            $"inventory era '{era.Era}' has unknown kind '{era.Kind}'; expected " +
            $"'{InventoryCatalog.CompilePinKind}' or '{InventoryCatalog.RuntimeVariantKind}' (never guess).");
    }

    /// <summary>
    /// Build the per-era walker path: <c>&lt;NativesRoot&gt;/&lt;platform&gt;/&lt;walkerName&gt;</c>,
    /// with a <c>.exe</c> suffix on <c>windows-x86_64</c>. NativesRoot is <c>CS2_WALKER_ERAS_ROOT</c>
    /// (env/appsettings via HostConfig) when set, else the <c>natives/</c> dir next to the host exe.
    /// The resolver does NOT check the file exists — WalkerProcessRunner fails loud at launch time.
    /// </summary>
    private static string BuildWalkerBinaryPath(string walkerName, string platform)
    {
        var configured = Cs2SchemaTracker.Host.Config.HostConfig.NativesRoot;
        // A configured NativesRoot that is RELATIVE resolves against the app dir
        // (AppContext.BaseDirectory) — where a shipped bundle keeps natives/ next to the host —
        // NOT the current working directory, so `dotnet cs2-schema-tracker.dll` works from any cwd.
        // An absolute value (dev/external store) is honored as-is.
        var nativesRoot = !string.IsNullOrEmpty(configured)
            ? (Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured))
            : Path.Combine(AppContext.BaseDirectory, "natives");
        var fileName = walkerName +
            (string.Equals(platform, "windows-x86_64", StringComparison.Ordinal) ? ".exe" : "");
        return Path.GetFullPath(Path.Combine(nativesRoot, platform, fileName));
    }

    /// <summary>
    /// Discover the repo root by walking up from the host executable until the directory holding the
    /// walker build tree is found. The sentinel is <c>walker/CMakeLists.txt</c>, NOT a bare
    /// <c>walker/</c> directory: the host project itself has a <c>Walker/</c> namespace folder that
    /// (case-insensitively on Windows) would otherwise match and stop the walk at the host project
    /// dir. Best-effort: returns the base directory if none is found so error messages stay informative.
    /// </summary>
    internal static string DiscoverRepoRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "walker", "CMakeLists.txt")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return baseDir;
    }
}
