// Cumulative schema-evolution command (`evolution`).
//
// Walks the whole committed chain for one platform, diffs each consecutive pair, and writes the ONE
// cumulative artifact at a FIXED per-platform path (rewritten in place each run):
//   <artifacts>/schema_evolution/<platform>.json
//
// A fixed path (not under the latest build) means there is nothing to move or delete build-to-build:
// each refresh just overwrites the one file, and git delta-compresses the near-identical successive
// versions in history all the same.
//
// WHO WRITES IT (kept in sync with the schema_evolution.proto header): `extract` NEVER writes this
// file. The routine writers are the commit scripts, scripts/commit-dump.ps1 (operator commits) and
// scripts/commit-forward-capture.ps1 (the scheduled pipeline's commit job); each runs this command
// (incremental mode) before each artifact commit so the refreshed file rides that commit — NON-FATALLY, because a
// rare, retryable refresh failure must not forfeit a time-sensitive forward capture. The stale-file
// backstop is the verify-artifacts evolution gate: ci.yml runs it on operator pushes, and
// scheduled-extract.yml re-runs it post-push itself (GITHUB_TOKEN pushes suppress ci.yml's
// trigger). Operators invoke this command directly for the one-time seed and after any backfill
// (`--full`).
//
// MODE. --full forces a from-scratch backfill (the one-time seed). Otherwise, if the fixed artifact
// already exists and is CONTIGUOUS with the chain (its latest_build == the second-newest committed
// build), an incremental refresh appends just the newest transition; else it safely falls back to a
// full backfill. Both modes produce byte-identical output over the same tree.
//
// Surface (README.md):
//   evolution [--platform <P>] [--artifacts <root>] [--full]
//   --artifacts defaults to "artifacts"; --platform defaults from appsettings (ExtractPlatform).
//
// fail-loud: an empty chain, or a missing/corrupt entity_schema.json for any build in the walk, exits
// non-zero BEFORE any bytes are written (the message is built fully in memory, then atomic-written).
//
// TEST SEAM: Run(args) is the production entry; Run(args, artifactsRootOverride) drives the command
// against a fixture-rooted artifacts/ tree.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Changelog;
using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.Evolution;
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static class EvolutionCommand
{
    private const string DefaultArtifactsRoot = "artifacts";

    private static readonly JsonParser StrictParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static int Run(string[] args) => Run(args, artifactsRootOverride: null);

    internal static int Run(string[] args, string? artifactsRootOverride)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var parsed = CliArgs.Parse(args);

        parsed.TryGetValue("platform", out var platform);
        if (string.IsNullOrEmpty(platform))
            platform = HostConfig.ExtractPlatform;
        if (string.IsNullOrEmpty(platform))
        {
            Console.Error.WriteLine(
                "evolution: --platform <linux-x86_64|windows-x86_64> is required (or set it in appsettings.json).");
            return 64;
        }
        if (!ArtifactSet.CanonicalPlatforms.Contains(platform, StringComparer.Ordinal))
        {
            Console.Error.WriteLine(
                $"evolution: '{platform}' is not a canonical platform " +
                $"(expected one of: {string.Join(", ", ArtifactSet.CanonicalPlatforms)}).");
            return 64;
        }

        parsed.TryGetValue("artifacts", out var artifactsArg);
        var artifactsRoot = Path.GetFullPath(
            artifactsRootOverride ?? (string.IsNullOrEmpty(artifactsArg) ? DefaultArtifactsRoot : artifactsArg));

        var forceFull = parsed.ContainsKey("full");

        var chain = ChangelogPredecessor.OrderedChain(artifactsRoot, platform);
        if (chain.Count == 0)
        {
            Console.Error.WriteLine(
                $"evolution: no committed builds for platform '{platform}' under {artifactsRoot} (: refusing).");
            return 65;
        }

        var outputPath = Path.Combine(artifactsRoot, ArtifactSet.SchemaEvolutionRelativePath(platform));
        var emitter = new SchemaEvolutionEmitter(SchemaFamily.Version, platform);

        SchemaEvolution message;
        string mode;
        try
        {
            if (!forceFull && chain.Count >= 2 && TryLoadContiguousPrior(outputPath, chain[^2], out var prior))
            {
                var pred = chain[^2];
                var latest = chain[^1];
                var predSnapshot = emitter.LoadSnapshot(artifactsRoot, pred);
                var latestSnapshot = emitter.LoadSnapshot(artifactsRoot, latest);
                message = emitter.BuildIncremental(artifactsRoot, prior!, predSnapshot, pred, latestSnapshot, latest);
                mode = $"incremental (appended {pred} -> {latest})";
            }
            else
            {
                message = emitter.BuildFull(artifactsRoot, chain);
                mode = $"full backfill ({chain.Count} builds, {chain.Count - 1} transitions)";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidProtocolBufferException or InvalidJsonException)
        {
            Console.Error.WriteLine($"evolution: {ex.Message}");
            return 65;
        }

        AtomicWrite.WriteCanonical(message, outputPath);

        Console.Error.WriteLine(
            $"evolution: wrote {ArtifactSet.SchemaEvolutionRelativePath(platform)} [{mode}].");
        return 0;
    }

    /// <summary>
    /// Try to load the fixed-path cumulative artifact and confirm it is CONTIGUOUS — its latest_build
    /// equals <paramref name="expectedLatest"/> (the second-newest committed build), so appending the
    /// newest transition is exact — and CURRENT — its schema_version equals
    /// <see cref="SchemaFamily.Version"/>. Returns false (full rebuild) when absent, unparseable,
    /// non-contiguous (e.g. a mid-chain build was backfilled), or written by a different schema
    /// family version. The version check is load-bearing for the incremental == full contract: an
    /// older-shape artifact incrementally extended by newer code would carry the new surfaces only
    /// on the appended transition — a mixed file no full backfill can reproduce.
    /// </summary>
    private static bool TryLoadContiguousPrior(string path, string expectedLatest, out SchemaEvolution? prior)
    {
        prior = null;
        if (!File.Exists(path))
            return false;
        try
        {
            var parsed = StrictParser.Parse<SchemaEvolution>(File.ReadAllText(path));
            if (!string.Equals(parsed.LatestBuild, expectedLatest, StringComparison.Ordinal))
                return false;
            if (!string.Equals(parsed.SchemaVersion, SchemaFamily.Version, StringComparison.Ordinal))
                return false;
            prior = parsed;
            return true;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            return false;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"cs2-schema-tracker evolution — cumulative schema-evolution artifact for a platform.

Walks the whole committed chain, diffs each consecutive pair, and writes ONE cumulative artifact at a
fixed per-platform path:
  <artifacts>/schema_evolution/<platform>.json

Usage:
  cs2-schema-tracker evolution [--platform <P>] [--artifacts <root>] [--full]

Arguments:
  --platform <P>     linux-x86_64 or windows-x86_64 (required unless set in appsettings.json).
  --artifacts <root> Artifacts root (default: artifacts).
  --full             Force a from-scratch backfill (default: incremental when a contiguous prior exists).

Facts-only (no rename/safety inference). Deterministic + byte-identical re-runs; an incremental
refresh is byte-identical to a full backfill. Fail-loud: an empty chain or a missing/corrupt
entity_schema.json exits non-zero before any bytes are written.

Exit codes: 0 ok · 64 usage error · 65 missing/corrupt input.");
    }
}
