// Single-source inventory + era catalog model — the host's read view of
// data/cs2-assets-inventory.json.
//
// The era catalog was consolidated INTO the assets inventory: a top-level eras[] lists every
// schema-system layout era (compile-pin or runtime-variant), and every builds[] entry carries an
// EXACT era id. This model binds the two arrays the host needs for per-(build, platform) era
// resolution (EraWalkerResolver) and the class-count gate. It is exposed via IOptions<T>
// (InventoryCatalogProvider) so call sites depend on the options abstraction, not on file IO.
//
// The LOSSLESS read-modify-write path (forward-capture appends a new builds[] entry) does NOT use
// this model — it edits the raw JsonNode tree so _meta/app/depots and every unknown field survive
// verbatim (Inventory/InventoryWriter). This model deliberately parses only what resolution reads.
//
// Parsing is fail-loud: a missing/unreadable file, malformed JSON, or a structurally-wrong document
// throws InvalidDataException before any resolution work. Never returns a partially-valid catalog.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cs2SchemaTracker.Host.Inventory;

/// <summary>One era row from the inventory's <c>eras[]</c> (compile-pin OR runtime-variant).</summary>
internal sealed class InventoryEra
{
    /// <summary>Exact era id (e.g. <c>cs2-2026-07-09</c>); the compile-pin walker binary's dir name.</summary>
    [JsonPropertyName("era")]
    public string Era { get; init; } = "";

    /// <summary><c>compile-pin</c> or <c>runtime-variant</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>Full hl2sdk SHA — compile-pin eras only (null on runtime-variant rows).</summary>
    [JsonPropertyName("hl2sdkSha")]
    public string? Hl2SdkSha { get; init; }

    /// <summary>The compile-pin hl2sdk SHA this runtime-variant rides — variant rows only.</summary>
    [JsonPropertyName("ridesCompilePin")]
    public string? RidesCompilePin { get; init; }

    /// <summary>Per-platform expected layout signature — compile-pin eras only.</summary>
    [JsonPropertyName("layoutSignatures")]
    public Dictionary<string, string>? LayoutSignatures { get; init; }

    /// <summary>The single expected layout signature for a runtime-variant (platform-agnostic key).</summary>
    [JsonPropertyName("variantSignature")]
    public string? VariantSignature { get; init; }

    /// <summary>Inclusive lower bound for this era's expected entity_schema class count.</summary>
    /// <remarks>
    /// Platform-agnostic FALLBACK, used only when <see cref="ClassBands"/> has no entry for the
    /// platform being extracted. Prefer the per-platform band: since the Workshop Tools depot is
    /// windows-only, a windows walk loads ~19 tool modules a linux walk cannot, so the two
    /// platforms sit in clearly separated count ranges (e.g. cs2-2026-07-09: win 4967, linux 3326).
    /// A single band wide enough to span both is too loose to catch a real regression.
    /// </remarks>
    [JsonPropertyName("minClasses")]
    public int? MinClasses { get; init; }

    /// <summary>Inclusive upper bound for this era's expected entity_schema class count.</summary>
    /// <remarks>Platform-agnostic fallback — see <see cref="MinClasses"/>.</remarks>
    [JsonPropertyName("maxClasses")]
    public int? MaxClasses { get; init; }

    /// <summary>
    /// Per-platform expected entity_schema class-count band, keyed exactly like
    /// <see cref="LayoutSignatures"/> (<c>windows-x86_64</c> / <c>linux-x86_64</c>). Authoritative
    /// when present; <see cref="MinClasses"/>/<see cref="MaxClasses"/> are the fallback.
    /// </summary>
    [JsonPropertyName("classBands")]
    public Dictionary<string, InventoryClassBand>? ClassBands { get; init; }

    /// <summary>Human label (documentation only).</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>
    /// Whether the MGetKV3ClassDefaults live-recovery accessor ABI is VALIDATED for this era.
    /// </summary>
    /// <remarks>
    /// The KV3 class-defaults recovery (walker: DecodeKv3Defaults) CALLS a generated per-class
    /// accessor thunk and serializes the result via tier0 SaveKV3AsJSON. That call is only ABI-valid
    /// where the SchemaMetadataEntryData_t / accessor layout matches the walker's assumption. It was
    /// validated for eras cs2-2025-07-31 and newer (Windows recovers 2400–3006 values/build; linux
    /// proven byte-identical). For the OLDER KV3-bearing eras (cs2-2024-02-07 … cs2-2025-03-20) the
    /// ABI does NOT hold: the accessor yields NOTHING on windows (0 populated in the committed sweep)
    /// and CRASHES the process on linux (calling it invokes garbage — an uncatchable stack overflow).
    /// So for those eras the host passes CS2_WALKER_NO_KV3_DEFAULTS to the walker (emit empty —
    /// deferred-with-reason, IDENTICAL to the committed windows state; no data is lost because there
    /// is none to recover there). The cs2-2023-* eras carry no MGetKV3ClassDefaults metadata at all,
    /// so the setting is a no-op for them.
    ///
    /// DEFAULT true (absent ⇒ attempt): a NEW era gets KV3 recovery automatically. If a future era
    /// breaks the ABI it fails LOUD (the walker crashes / the determinism gate trips) rather than
    /// silently dropping KV3 — that signals the era needs an explicit <c>"kv3ClassDefaults": false</c>
    /// here (and, ideally, a walker-side ABI update) rather than a silent gap.
    /// </remarks>
    [JsonPropertyName("kv3ClassDefaults")]
    public bool Kv3ClassDefaults { get; init; } = true;

    /// <summary>True iff this is a compile-pin era (carries its own hl2sdk pin + per-platform sigs).</summary>
    public bool IsCompilePin => string.Equals(Kind, InventoryCatalog.CompilePinKind, StringComparison.Ordinal);

    /// <summary>True iff this is a runtime-variant era (rides a compile pin; one variant signature).</summary>
    public bool IsRuntimeVariant => string.Equals(Kind, InventoryCatalog.RuntimeVariantKind, StringComparison.Ordinal);
}

/// <summary>One build row from the inventory's <c>builds[]</c> (only the fields resolution reads).</summary>
internal sealed class InventoryBuildEntry
{
    [JsonPropertyName("build_id")]
    public long BuildId { get; init; }

    /// <summary>Exact era id this build's schema-system layout belongs to (compile OR variant).</summary>
    [JsonPropertyName("era")]
    public string Era { get; init; } = "";

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("binaries")]
    public Dictionary<string, string>? Binaries { get; init; }
}

/// <summary>
/// One platform's inclusive entity_schema class-count band, as carried by an era's
/// <c>classBands</c> map.
/// </summary>
internal sealed class InventoryClassBand
{
    /// <summary>Inclusive lower bound.</summary>
    [JsonPropertyName("min")]
    public int? Min { get; init; }

    /// <summary>Inclusive upper bound.</summary>
    [JsonPropertyName("max")]
    public int? Max { get; init; }
}

/// <summary>
/// The host's read view of <c>data/cs2-assets-inventory.json</c>: <c>eras[]</c> + <c>builds[]</c>,
/// the single source of truth for per-(build, platform) era resolution. Bound via
/// <see cref="InventoryCatalogProvider"/> (IOptions).
/// </summary>
internal sealed class InventoryCatalog
{
    /// <summary>The <c>kind</c> value for a compile-pin era.</summary>
    public const string CompilePinKind = "compile-pin";

    /// <summary>The <c>kind</c> value for a runtime-variant era.</summary>
    public const string RuntimeVariantKind = "runtime-variant";

    /// <summary>Default repo-relative path to the inventory file.</summary>
    public const string DefaultRelativePath = "data/cs2-assets-inventory.json";

    [JsonPropertyName("eras")]
    public List<InventoryEra> Eras { get; init; } = new();

    [JsonPropertyName("builds")]
    public List<InventoryBuildEntry> Builds { get; init; } = new();

    /// <summary>The build row for <paramref name="buildId"/>, or null when not in the inventory.</summary>
    public InventoryBuildEntry? FindBuild(long buildId)
    {
        foreach (var b in Builds)
        {
            if (b.BuildId == buildId)
            {
                return b;
            }
        }
        return null;
    }

    /// <summary>The era row keyed by exact <paramref name="eraId"/>, or null.</summary>
    public InventoryEra? FindEra(string eraId)
    {
        ArgumentException.ThrowIfNullOrEmpty(eraId);
        foreach (var e in Eras)
        {
            if (string.Equals(e.Era, eraId, StringComparison.Ordinal))
            {
                return e;
            }
        }
        return null;
    }

    /// <summary>
    /// The newest compile-pin era. The inventory orders <c>eras[]</c> newest-first with the
    /// compile-pin eras leading, so this is the first compile-pin row — the optimistic
    /// forward-capture target for a fresh/unknown build. Throws when no compile-pin era exists
    /// (a structurally-broken catalog; never guess).
    /// </summary>
    public InventoryEra NewestCompilePinEra()
    {
        foreach (var e in Eras)
        {
            if (e.IsCompilePin)
            {
                return e;
            }
        }
        throw new InvalidDataException(
            "inventory eras[] carries no compile-pin era; cannot pick a newest era for a fresh " +
            "build's forward-capture (never guess).");
    }

    /// <summary>
    /// The compile-pin era whose <c>hl2sdkSha</c> equals <paramref name="sha"/> (the ridden pin of a
    /// runtime-variant), or null when none. Fails loud if more than one compile-pin era carries the
    /// same SHA (a corrupt catalog — never guess between two).
    /// </summary>
    public InventoryEra? FindCompilePinBySha(string sha)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha);
        InventoryEra? match = null;
        foreach (var e in Eras)
        {
            if (e.IsCompilePin && string.Equals(e.Hl2SdkSha, sha, StringComparison.Ordinal))
            {
                if (match is not null)
                {
                    throw new InvalidDataException(
                        $"inventory eras[] has two compile-pin eras with hl2sdkSha '{sha}' " +
                        $"('{match.Era}' and '{e.Era}') — ambiguous; never guess.");
                }
                match = e;
            }
        }
        return match;
    }

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Load + validate the catalog at <paramref name="path"/>. Throws
    /// <see cref="InvalidDataException"/> if absent / unreadable / malformed / structurally empty.
    /// </summary>
    public static InventoryCatalog LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new InvalidDataException(
                $"assets inventory '{path}' does not exist. It is the single source of truth for the " +
                "era catalog (eras[]) and each build's era (builds[].era); the host cannot resolve a " +
                "per-era walker without it.");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"assets inventory '{path}' could not be read: {ex.Message}", ex);
        }

        return Parse(json, path);
    }

    /// <summary>Parse the catalog from a JSON string (used by <see cref="LoadFromFile"/> and tests).</summary>
    public static InventoryCatalog Parse(string json, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(json);
        InventoryCatalog? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<InventoryCatalog>(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"assets inventory '{source}' is not valid JSON: {ex.Message}", ex);
        }

        if (catalog is null)
        {
            throw new InvalidDataException($"assets inventory '{source}' parsed to null.");
        }
        if (catalog.Eras.Count == 0)
        {
            throw new InvalidDataException(
                $"assets inventory '{source}' has no eras[]; the era catalog is the single source of truth.");
        }
        return catalog;
    }
}
