// One-time corpus migration for the build-on-demand localization.json change (internal dev tooling —
// NOT part of the documented CLI surface).
//
// The existing committed corpus was dumped BEFORE localization became build-on-demand: every set's
// localization.json is still on disk (committed, pre-`git rm`), but no provenance.json carries the
// provenance.localization fingerprint and no changelog.json carries the 6th `localization` family.
// After the gate change, verify-artifacts now fails on every content-acquired set lacking the
// fingerprint. This command backfills BOTH, entirely from the on-disk localization.json files — no
// Steam acquire and no re-dump:
//
//   1. provenance.localization  — for every (build, platform) that has localization.json on disk,
//      compute sha256 (hex lowercase) / size_bytes / token_count over the on-disk canonical bytes
//      and write it into that set's provenance.json by ROUND-TRIPPING the Provenance message through
//      the canonical serializer (never hand-editing JSON), so key order + uint64-as-string + canonical
//      form stay exact and the only change is the added localization block.
//   2. the 6th `localization` changelog family — for every set whose immediate committed predecessor
//      ALSO has localization.json on disk, rebuild changelog.json with families[5] = localization,
//      diffing the predecessor's on-disk localization.json against this build's. The five binary
//      families are re-derived from the committed set dirs (byte-identical to what is already there).
//      The floor build (no predecessor) and any build whose predecessor shipped no localization keep
//      their five-family changelog untouched.
//
// Memory / perf: each localization.json (~150 MB) is parsed to a compact token→row map exactly ONCE
// per platform sweep — a build's map is reused as its successor's `from` side (builds are processed
// in ascending numeric order per platform). sha256/size stream the file. token_count comes from the
// parsed map (no extra parse). Determinism: the same on-disk inputs + tool version produce
// byte-identical provenance.json + changelog.json on a re-run (idempotent until the payloads are
// untracked).
//
// This command does NOT git rm the payloads and does NOT commit — those are deliberate human
// checkpoints the operator runs afterward.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Changelog;
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using LocRowMap = System.Collections.Generic.IReadOnlyDictionary<
    string, Cs2SchemaTracker.Host.Changelog.BuildChangelogEmitter.LocRow>;

namespace Cs2SchemaTracker.Host.Cli;

internal static class BackfillLocalizationCommand
{
    private const string DefaultArtifactsRoot = "artifacts";

    private static readonly JsonParser TolerantParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var parsed = CliArgs.Parse(args);

        parsed.TryGetValue("artifacts", out var artifactsArg);
        var artifactsRoot = Path.GetFullPath(
            string.IsNullOrEmpty(artifactsArg) ? DefaultArtifactsRoot : artifactsArg);
        if (!Directory.Exists(artifactsRoot))
        {
            Console.Error.WriteLine($"backfill-localization: artifacts root not found: '{artifactsRoot}'.");
            return 65;
        }

        // Optional build filter (repeatable via CliArgs? CliArgs collapses repeats — accept a single
        // --build or comma-separated list). A limit is used for the smoke checkpoint.
        var buildFilter = new HashSet<string>(StringComparer.Ordinal);
        if (parsed.TryGetValue("build", out var buildArg) && !string.IsNullOrEmpty(buildArg))
        {
            foreach (var b in buildArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                buildFilter.Add(b);
            }
        }

        parsed.TryGetValue("platform", out var platformArg);
        IReadOnlyList<string> platforms = string.IsNullOrEmpty(platformArg)
            ? ArtifactSet.CanonicalPlatforms
            : new[] { platformArg };
        foreach (var p in platforms)
        {
            if (!ArtifactSet.CanonicalPlatforms.Contains(p, StringComparer.Ordinal))
            {
                Console.Error.WriteLine($"backfill-localization: '{p}' is not a canonical platform.");
                return 64;
            }
        }

        int provWritten = 0, changelogWritten = 0, changelogSkipped = 0, failures = 0;

        foreach (var platform in platforms.OrderBy(p => p, StringComparer.Ordinal))
        {
            // Builds (ascending numeric) that have localization.json on disk for this platform.
            var builds = Directory.EnumerateDirectories(artifactsRoot)
                .Select(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .Where(n => !string.IsNullOrEmpty(n) && long.TryParse(n, out _))
                .Where(n => buildFilter.Count == 0 || buildFilter.Contains(n))
                .Where(n => File.Exists(LocalizationPath(artifactsRoot, n!, platform)))
                .OrderBy(n => long.Parse(n!))
                .ToList();

            if (builds.Count == 0)
                continue;
            Console.Error.WriteLine(
                $"backfill-localization: platform '{platform}' — {builds.Count} set(s) with localization.json.");

            LocRowMap? prevRows = null;
            string? prevBuild = null;

            foreach (var build in builds)
            {
                try
                {
                    var locPath = LocalizationPath(artifactsRoot, build!, platform);
                    Dictionary<string, BuildChangelogEmitter.LocRow> rows =
                        BuildChangelogEmitter.LoadLocalizationRows(locPath);   // one parse
                    ulong tokenCount = (ulong)rows.Count;
                    var fingerprint = ExtractCommand.ComputeLocalizationFingerprint(locPath, tokenCount);

                    // 1. provenance.localization — round-trip the message, set the block, rewrite canonical.
                    var provPath = Path.Combine(artifactsRoot, build!, platform, ArtifactSet.ProvenanceFileName);
                    if (!File.Exists(provPath))
                    {
                        throw new FileNotFoundException(
                            $"provenance.json missing for a set that has localization.json: '{provPath}'.");
                    }
                    var provenance = TolerantParser.Parse<Schemas.Provenance>(File.ReadAllText(provPath));
                    provenance.Localization = fingerprint;
                    AtomicWrite.WriteCanonical(provenance, provPath);
                    provWritten++;
                    Console.Error.WriteLine(
                        $"  {build}/{platform}: provenance.localization {{ sha256={fingerprint.Sha256}, " +
                        $"sizeBytes={fingerprint.SizeBytes}, tokenCount={fingerprint.TokenCount} }}");

                    // 2. localization changelog family — only when the immediate committed predecessor
                    //    ALSO has localization.json on disk (else the 5-family changelog stays correct).
                    var predecessor = ChangelogPredecessor.Resolve(artifactsRoot, build!, platform);
                    if (predecessor is not null)
                    {
                        var predLoc = LocalizationPath(artifactsRoot, predecessor, platform);
                        if (File.Exists(predLoc))
                        {
                            LocRowMap fromRows = string.Equals(predecessor, prevBuild, StringComparison.Ordinal)
                                ? prevRows!
                                : BuildChangelogEmitter.LoadLocalizationRows(predLoc);

                            var emitter = new BuildChangelogEmitter(
                                SchemaFamily.Version, platform, predecessor, build!);
                            var doc = emitter.BuildFromRows(
                                fromSetDir: Path.Combine(artifactsRoot, predecessor, platform),
                                toSetDir: Path.Combine(artifactsRoot, build!, platform),
                                fromRows: fromRows,
                                toRows: rows);
                            var changelogPath = Path.Combine(
                                artifactsRoot, build!, platform, ArtifactSet.ChangelogFileName);
                            AtomicWrite.WriteCanonical(doc, changelogPath);
                            changelogWritten++;

                            var locFam = doc.Families.Single(f => f.Family == BuildChangelogEmitter.LocalizationFamily);
                            Console.Error.WriteLine(
                                $"  {build}/{platform}: changelog localization family " +
                                $"(from {predecessor}) added={locFam.Added.Count} removed={locFam.Removed.Count} " +
                                $"changed={locFam.Changed.Count}");
                        }
                        else
                        {
                            changelogSkipped++;
                            Console.Error.WriteLine(
                                $"  {build}/{platform}: predecessor {predecessor} has no localization.json — " +
                                "5-family changelog left untouched.");
                        }
                    }

                    prevRows = rows;
                    prevBuild = build;
                }
                catch (Exception ex)
                {
                    // Migration sweep: record the fail-loud and continue so one bad set does not abort
                    // the whole corpus pass. AtomicWrite means any file already written this set stays
                    // valid + re-runnable. Non-zero exit reflects the failure count.
                    failures++;
                    prevRows = null;   // do not reuse across a failure boundary.
                    prevBuild = null;
                    Console.Error.WriteLine(
                        $"  FAIL {build}/{platform}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Console.Error.WriteLine(
            $"backfill-localization: DONE — provenance rewritten={provWritten}, " +
            $"changelog rewritten (6-family)={changelogWritten}, changelog skipped (no predecessor localization)={changelogSkipped}, " +
            $"failures={failures}.");
        return failures > 0 ? 65 : 0;
    }

    private static string LocalizationPath(string artifactsRoot, string build, string platform)
        => Path.Combine(artifactsRoot, build, platform, ArtifactSet.LocalizationFileName);

    private static void PrintHelp()
    {
        Console.WriteLine(@"cs2-schema-tracker backfill-localization (internal) — corpus migration for build-on-demand localization.

Backfills, entirely from the on-disk committed localization.json files (no Steam acquire, no re-dump):
  1. provenance.localization (sha256/size/token_count) into every set's provenance.json;
  2. the 6th `localization` changelog family into every changelog.json whose predecessor also has
     localization.json on disk.

Usage:
  cs2-schema-tracker backfill-localization [--artifacts <root>] [--build <id[,id...]>] [--platform <P>]

  --artifacts <root>  Artifacts root (default: artifacts).
  --build <id[,id]>   Limit to specific build id(s) (comma-separated). Default: the whole corpus.
  --platform <P>      Limit to one platform. Default: both canonical platforms.

Deterministic + idempotent: re-running produces byte-identical output. Does NOT git rm the payloads
and does NOT commit — those are separate operator steps.");
    }
}
