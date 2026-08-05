// The canonical shape of a committed (build, platform) artifact set.
//
// The host's single in-code source of truth for the artifact surface the README "What it produces"
// section describes in prose: the per-platform REQUIRED files, the required (non-empty) protos/
// directory, the content-depot-gated files (required iff the content depot was acquired), the
// content depot id, and the two canonical platform names.
//
// The completeness validator (ArtifactSetValidator) reads these constants; the bash gate is a thin
// git-diff driver that shells `verify-artifacts`.
//
// Determinism: every list is a fixed, stable-ordered readonly array. No I/O here.

namespace Cs2SchemaTracker.Host.Artifacts;

/// <summary>
/// The canonical contract shape of a committed <c>(build, platform)</c> artifact set
/// (README "What it produces"). Single in-code source of truth for the completeness validator.
/// </summary>
public static class ArtifactSet
{
    /// <summary>
    /// Per-platform UNCONDITIONAL required files (README "What it produces"). Every committed
    /// <c>(build, platform)</c> set carries all of these; they are derivable from the per-OS
    /// binary depot alone. The content-depot-gated files (<see cref="ContentDepotGatedFiles"/>)
    /// are deliberately NOT in this list.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredFiles = new[]
    {
        "entity_schema.json",
        "convars.json",
        "commands.json",
        "network_messages.json",
        "demo_messages.json",
        "engine_constants.json",
        "string_pools.json",
        "registry_audit.json",
        "modules.json",
        "provenance.json",
        "protos.descriptorset",
    };

    /// <summary>
    /// Per-platform required DIRECTORIES that must exist and be non-empty (README "What it produces").
    /// The per-descriptor <c>protos/&lt;file&gt;.proto</c> count is data-dependent, so we require
    /// the directory exists and is non-empty rather than enumerating its files.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredNonEmptyDirs = new[]
    {
        "protos",
    };

    /// <summary>
    /// The CS2 cross-platform content depot. If it appears in a platform's
    /// <c>provenance.json</c> <c>steam.depots[].depotId</c>, the content extraction had its
    /// input (the <c>pak01_*.vpk</c>), so every content-depot-gated artifact MUST be present.
    /// </summary>
    public const uint ContentDepotId = 2347770;

    /// <summary>
    /// Content-depot-gated required files (README "What it produces"). Each is required in a
    /// <c>(build, platform)</c> set IFF that platform's <c>provenance.json</c> lists content
    /// depot <see cref="ContentDepotId"/> under <c>steam.depots[].depotId</c>. On a binary-only
    /// build (no content depot) each is legitimately absent — present-or-absent is a non-issue.
    ///
    /// <see cref="LocalizationFileName"/> is deliberately NOT in this list: localization.json is a
    /// BUILD-ON-DEMAND artifact (produced every dump but not committed — at ~199 MB/set it is 96% of
    /// the working tree). Its presence in a committed set is neither required nor expected; instead a
    /// content-depot set MUST carry a populated <c>provenance.localization</c> fingerprint
    /// (sha256/size/token_count) so an on-demand rebuild via <c>emit-localization</c> is
    /// byte-verifiable. The completeness gate (ArtifactSetValidator) enforces THAT, not the file.
    /// </summary>
    public static readonly IReadOnlyList<string> ContentDepotGatedFiles = new[]
    {
        "gameevents.json",
        "item_definitions.json",
        "game_modes.json",
        "surface_properties.json",
        "prop_data.json",
        "map_overviews.json",
    };

    /// <summary>
    /// The build-on-demand localization artifact filename. Produced on every dump (so extraction +
    /// determinism stay exercised) but NOT committed to the tree — it is <c>.gitignore</c>d and
    /// regenerated on demand by <c>emit-localization</c>. Deliberately absent from
    /// <see cref="ContentDepotGatedFiles"/>; the committed set carries only its
    /// <c>provenance.localization</c> fingerprint. It IS still a legitimate content-omission artifact
    /// name (an era that never shipped localization tables records it in omissions.json), so it is
    /// listed in <see cref="OmittableContentArtifacts"/> even though it is not a required committed
    /// file.
    /// </summary>
    public const string LocalizationFileName = "localization.json";

    /// <summary>
    /// The content artifacts a per-platform omissions entry may legitimately name: every
    /// content-depot-gated file PLUS the build-on-demand <see cref="LocalizationFileName"/> (an era
    /// that genuinely never shipped localization tables records a <c>localization.json</c> omission,
    /// even though the file itself is never committed). Used by the omission-name validation in
    /// ArtifactSetValidator.
    /// </summary>
    public static readonly IReadOnlyList<string> OmittableContentArtifacts =
        ContentDepotGatedFiles.Append(LocalizationFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The canonical platform names (README "What it produces"). A present platform directory must be
    /// one of these; an omissions row must name one of these.
    /// </summary>
    public static readonly IReadOnlyList<string> CanonicalPlatforms = new[]
    {
        "linux-x86_64",
        "windows-x86_64",
    };

    /// <summary>
    /// The build-level omissions manifest filename. Present ONLY for builds that have a
    /// recorded omission (a wholesale not-dumped platform, or a per-artifact content omission for an
    /// era that never shipped the source). A clean build carries none; a missing file is treated as
    /// <c>omissions: []</c> (absent = clean). Omissions are still ALWAYS recorded WHEN they exist —
    /// only the empty-file-for-clean-builds ceremony is dropped.
    /// </summary>
    public const string OmissionsFileName = "omissions.json";

    /// <summary>The per-platform provenance filename used to evaluate content-depot gating.</summary>
    public const string ProvenanceFileName = "provenance.json";

    /// <summary>
    /// The build-to-build changelog filename. It is a COMMITTED per-(build,platform) artifact
    /// committed under the NEWER build's platform dir. It is produced INLINE by <c>extract</c> when a
    /// committed predecessor already exists on disk (the forward-capture case, builds committed
    /// oldest->newest), and also by the standalone <c>diff</c> subcommand for out-of-order
    /// regeneration / backfill. It is NOT an unconditional EmitFullSet file: its presence/absence is
    /// governed by the PREDECESSOR rule (shared <see cref="Changelog.ChangelogPredecessor"/>):
    /// required iff this (build,platform) has an
    /// immediate committed predecessor for the platform; forbidden (absent) when it is the earliest
    /// committed build for the platform (the floor build). Therefore it is deliberately absent from
    /// <see cref="RequiredFiles"/> and <see cref="ContentDepotGatedFiles"/> — the predecessor gate
    /// in ArtifactSetValidator owns it.
    /// </summary>
    public const string ChangelogFileName = "changelog.json";

    /// <summary>
    /// The directory (directly under the artifacts root, NOT under a build dir) holding the cumulative
    /// schema-evolution artifact — exactly one file per platform, at a FIXED path rewritten in place
    /// each build: <c>artifacts/schema_evolution/&lt;platform&gt;.json</c>. It carries the entire
    /// history (all transitions + field/enum history), is produced by the <c>evolution</c> subcommand
    /// and refreshed INLINE by <c>extract</c>. A fixed path (rather than under the latest build) keeps
    /// the inline write + commit trivial (overwrite-in-place; no cross-build move/deletion) and is
    /// deliberately outside the per-build sets — the repo-level evolution check in verify-artifacts
    /// owns it, so it is absent from <see cref="RequiredFiles"/> / <see cref="ContentDepotGatedFiles"/>.
    /// </summary>
    public const string SchemaEvolutionDirName = "schema_evolution";

    /// <summary>
    /// The repo-relative (forward-slash) path of the cumulative schema-evolution artifact for
    /// <paramref name="platform"/>, relative to the artifacts root:
    /// <c>schema_evolution/&lt;platform&gt;.json</c>.
    /// </summary>
    public static string SchemaEvolutionRelativePath(string platform) =>
        $"{SchemaEvolutionDirName}/{platform}.json";
}
