// Assembles the cumulative SchemaEvolution message (schema_evolution.json).
//
// Two build paths that MUST produce byte-identical output over the same committed tree:
//   - BuildFull:        streaming-pairwise walk of the whole chain (one-time backfill / --full).
//   - BuildIncremental: the prior cumulative artifact + the (pred, latest) snapshot pair, appending
//                       exactly one transition and folding one snapshot.
//
// MEMORY. BuildFull holds at most TWO parsed snapshots (~12 MB each) plus the accumulator (one small
// record per (class, field)) plus the growing transitions list (deltas only — never a full snapshot).
// It never loads the whole ~376-build history at once: each snapshot is released as the window slides.
//
// Invariants (inherited): deterministic + Ordinal-sorted (in SchemaSnapshotDiff / the accumulator),
// culture-invariant scalars, fail-loud on a missing/incomplete snapshot, atomic canonical write by
// the caller (this class only BUILDS the message; the command writes it).

using System.Text;

using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.Evolution;

/// <summary>
/// Builds the cumulative <see cref="SchemaEvolution"/> for one platform. See file header. Pure of the
/// output write; loads committed entity_schema.json snapshots for the full walk.
/// </summary>
public sealed class SchemaEvolutionEmitter
{
    // Strict: a foreign/malformed snapshot must fail loud, not be silently treated as empty (matches
    // BuildChangelogEmitter). Unknown fields are ignored for forward-compat with newer schema minors.
    private static readonly JsonParser StrictParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private const string EntitySchemaFile = "entity_schema.json";
    private const string ProvenanceFile = "provenance.json";

    private readonly string _schemaVersion;
    private readonly string _platform;

    public SchemaEvolutionEmitter(string schemaVersion, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _platform = platform;
    }

    /// <summary>
    /// Full backfill: diff every consecutive pair of the ascending <paramref name="chain"/>, streaming
    /// pairwise. <paramref name="chain"/> must be non-empty and ascending (see
    /// <see cref="Changelog.ChangelogPredecessor.OrderedChain"/>). Snapshots are read from the
    /// committed set dirs under <paramref name="artifactsRoot"/>.
    /// </summary>
    public SchemaEvolution BuildFull(string artifactsRoot, IReadOnlyList<string> chain)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRoot);
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
        {
            throw new InvalidDataException(
                "SchemaEvolutionEmitter: the committed chain is empty (no builds for the platform).");
        }

        var transitions = new List<Transition>(chain.Count - 1);
        var prev = LoadSnapshot(artifactsRoot, chain[0]);
        var prevManifestUtc = LoadManifestCreatedUtc(artifactsRoot, chain[0]);
        var acc = FieldHistoryAccumulator.Seed(prev, chain[0]);

        for (var i = 1; i < chain.Count; i++)
        {
            var cur = LoadSnapshot(artifactsRoot, chain[i]);
            var curManifestUtc = LoadManifestCreatedUtc(artifactsRoot, chain[i]);
            var transition = SchemaSnapshotDiff.Diff(prev, cur, chain[i - 1], chain[i]);
            StampManifestTimes(transition, prevManifestUtc, curManifestUtc);
            transitions.Add(transition);
            acc.Fold(cur, chain[i]);
            prev = cur; // the older snapshot is now GC-eligible (window slides)
            prevManifestUtc = curManifestUtc;
        }

        return Assemble(chain[0], chain[^1], transitions, acc);
    }

    /// <summary>
    /// Incremental refresh: extend <paramref name="prior"/> (the predecessor build's cumulative
    /// artifact) with exactly one transition <paramref name="predBuild"/> -&gt; <paramref name="latestBuild"/>.
    /// The caller supplies both snapshots (so an inline <c>extract</c> can pass the freshly-STAGED
    /// latest snapshot rather than a committed one). Produces output byte-identical to
    /// <see cref="BuildFull"/> over the resulting tree.
    /// </summary>
    public SchemaEvolution BuildIncremental(
        string artifactsRoot,
        SchemaEvolution prior,
        Schemas.EntitySchema predSnapshot, string predBuild,
        Schemas.EntitySchema latestSnapshot, string latestBuild)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRoot);
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(predSnapshot);
        ArgumentNullException.ThrowIfNull(latestSnapshot);
        ArgumentException.ThrowIfNullOrEmpty(predBuild);
        ArgumentException.ThrowIfNullOrEmpty(latestBuild);

        if (!string.Equals(prior.LatestBuild, predBuild, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SchemaEvolutionEmitter: incremental refresh requires the prior artifact's latest_build " +
                $"('{prior.LatestBuild}') to equal the predecessor build '{predBuild}'.");
        }

        var acc = FieldHistoryAccumulator.Rehydrate(prior.FieldHistory, prior.EnumHistory);
        acc.Fold(latestSnapshot, latestBuild);

        var transitions = new List<Transition>(prior.Transitions.Count + 1);
        transitions.AddRange(prior.Transitions);
        var appended = SchemaSnapshotDiff.Diff(predSnapshot, latestSnapshot, predBuild, latestBuild);
        StampManifestTimes(
            appended,
            LoadManifestCreatedUtc(artifactsRoot, predBuild),
            LoadManifestCreatedUtc(artifactsRoot, latestBuild));
        transitions.Add(appended);

        return Assemble(prior.BaselineBuild, latestBuild, transitions, acc);
    }

    /// <summary>
    /// Stamp the calendar axis on a freshly-diffed transition. The one stated exception to
    /// snapshot-only derivation (see schema_evolution.proto): the values join the two builds'
    /// committed provenance records — input data recorded by Steam, never the wall clock.
    /// </summary>
    private static void StampManifestTimes(Transition transition, string fromUtc, string toUtc)
    {
        transition.FromManifestCreatedUtc = fromUtc;
        transition.ToManifestCreatedUtc = toUtc;
    }

    /// <summary>
    /// Read a committed build's <c>steam.manifest_created_utc</c> from its provenance.json.
    /// Fail-loud on a missing/corrupt file or an empty value — every committed set records it,
    /// so absence means the tree (or the build id) is wrong, never a legitimately dateless build.
    /// </summary>
    public string LoadManifestCreatedUtc(string artifactsRoot, string build)
    {
        var path = Path.Combine(artifactsRoot, build, _platform, ProvenanceFile);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"SchemaEvolutionEmitter: required {ProvenanceFile} not found for build '{build}' " +
                $"platform '{_platform}' at '{path}'.", path);
        }
        Schemas.Provenance provenance;
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            provenance = StrictParser.Parse<Schemas.Provenance>(reader);
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            throw new InvalidDataException(
                $"SchemaEvolutionEmitter: {ProvenanceFile} for build '{build}' does not parse as " +
                $"Provenance: {ex.Message}", ex);
        }
        var utc = provenance.Steam?.ManifestCreatedUtc;
        if (string.IsNullOrEmpty(utc))
        {
            throw new InvalidDataException(
                $"SchemaEvolutionEmitter: {ProvenanceFile} for build '{build}' carries no " +
                "steam.manifest_created_utc — cannot stamp the transition calendar axis.");
        }
        return utc;
    }

    /// <summary>Read + strict-parse a committed build's entity_schema.json. Fail-loud on absence/corruption.</summary>
    public Schemas.EntitySchema LoadSnapshot(string artifactsRoot, string build)
    {
        var path = Path.Combine(artifactsRoot, build, _platform, EntitySchemaFile);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"SchemaEvolutionEmitter: required {EntitySchemaFile} not found for build '{build}' " +
                $"platform '{_platform}' at '{path}'.", path);
        }
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            return StrictParser.Parse<Schemas.EntitySchema>(reader);
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            throw new InvalidDataException(
                $"SchemaEvolutionEmitter: {EntitySchemaFile} for build '{build}' does not parse as " +
                $"EntitySchema: {ex.Message}", ex);
        }
    }

    private SchemaEvolution Assemble(
        string baselineBuild, string latestBuild, List<Transition> transitions, FieldHistoryAccumulator acc)
    {
        var msg = new SchemaEvolution
        {
            SchemaVersion = _schemaVersion,
            Platform = _platform,
            BaselineBuild = baselineBuild,
            LatestBuild = latestBuild,
        };
        msg.Transitions.AddRange(transitions);              // already build-ascending
        msg.FieldHistory.AddRange(acc.ToFieldHistory());    // Ordinal-sorted by (class_name, field)
        msg.EnumHistory.AddRange(acc.ToEnumHistory());      // Ordinal-sorted by enum_name
        return msg;
    }
}
