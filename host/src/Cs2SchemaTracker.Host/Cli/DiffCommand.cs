// build-to-build changelog command (`diff`).
//
// CROSS-BUILD, STANDALONE subcommand. Given a predecessor build (--from) and a newer build
// (--to) for one platform, diffs the two committed (build, platform) artifact sets and writes
// the COMMITTED artifact changelog.json under the NEWER build's per-platform dir:
//   <artifacts>/<to_build>/<platform>/changelog.json
//
// As of the inline wiring, a normal `extract` ALREADY produces changelog.json automatically:
// it diffs the freshly-staged set against the immediate committed predecessor (resolved by the
// shared ChangelogPredecessor rule) and promotes the file atomically with the rest of the set. That
// works for the forward-capture case because builds commit oldest->newest, so the predecessor is
// already on disk when a build is extracted. This standalone `diff` command STAYS for the cases
// inline emission cannot cover: OUT-OF-ORDER regeneration / backfill (a build committed before its
// predecessor existed, or an older set re-diffed after a newer neighbour landed). Both paths write
// the identical file to the identical place; the verify-artifacts predecessor gate enforces that
// the file exists with the correct from_build/to_build wherever a predecessor exists.
//
// Surface (README.md — named args, matching every other host command):
//   diff --from <build> --to <build> [--platform <P>] [--artifacts <root>]
//   --artifacts defaults to "artifacts"; --platform defaults from appsettings (ExtractPlatform),
//   else required.
//
// fail-loud: if either (build,platform) set dir is missing, OR a required family source
// file is absent and NOT accounted-for in that build's omissions.json, exit non-zero BEFORE any
// bytes are written. (Unlike the old python script which silently treated missing as empty.)
//
// LOCALIZATION FAMILY (build-on-demand): localization.json is produced every dump but NOT committed,
// so it cannot be read from either committed set dir. Consistent with the inline extract path, when
// BOTH builds recorded a populated provenance.localization this command REGENERATES each side's
// localization.json on demand from its content and appends the 6th `localization` family; if either
// build's content depot is not resolvable it fails loud with guidance. If either build produced no
// localization, the changelog is the five binary families.
//
// TEST SEAM: Run(args) is the production entry; Run(args, artifactsRootOverride) lets the suite
// drive the whole command against a fixture-rooted artifacts/ tree without touching the real
// checkout or config.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Changelog;
using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.Localization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static class DiffCommand
{
    private const string DefaultArtifactsRoot = "artifacts";

    // The required family source files that must be present in BOTH set dirs (or accounted-for in
    // the build's omissions for that platform). entity_schema.json backs two families (classes,
    // enums); convars/commands/engine_constants back one each.
    private static readonly string[] RequiredSourceFiles =
    {
        "entity_schema.json", "convars.json", "commands.json", "engine_constants.json",
    };

    private static readonly JsonParser TolerantParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>Production entry: resolve --artifacts (default "artifacts") relative to cwd.</summary>
    public static int Run(string[] args) => Run(args, artifactsRootOverride: null);

    /// <summary>
    /// Test seam: when <paramref name="artifactsRootOverride"/> is non-null it is used as the
    /// artifacts root regardless of the parsed --artifacts (so a fixture tree can be targeted).
    /// </summary>
    internal static int Run(string[] args, string? artifactsRootOverride)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var parsed = CliArgs.Parse(args);

        if (!parsed.TryGetValue("from", out var fromBuild) || string.IsNullOrEmpty(fromBuild))
        {
            Console.Error.WriteLine("diff: --from <build> is required (the predecessor / baseline build).");
            return 64;   // EX_USAGE
        }
        if (!parsed.TryGetValue("to", out var toBuild) || string.IsNullOrEmpty(toBuild))
        {
            Console.Error.WriteLine("diff: --to <build> is required (the newer build the changelog is committed under).");
            return 64;
        }
        if (string.Equals(fromBuild, toBuild, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("diff: --from and --to must differ (cannot diff a build against itself).");
            return 64;
        }

        // --platform: explicit wins, else appsettings default (same source as extract), else required.
        parsed.TryGetValue("platform", out var platform);
        if (string.IsNullOrEmpty(platform))
        {
            platform = HostConfig.ExtractPlatform;
        }
        if (string.IsNullOrEmpty(platform))
        {
            Console.Error.WriteLine(
                "diff: --platform <linux-x86_64|windows-x86_64> is required (or set it in appsettings.json).");
            return 64;
        }
        if (!ArtifactSet.CanonicalPlatforms.Contains(platform, StringComparer.Ordinal))
        {
            Console.Error.WriteLine(
                $"diff: '{platform}' is not a canonical platform " +
                $"(expected one of: {string.Join(", ", ArtifactSet.CanonicalPlatforms)}).");
            return 64;
        }

        parsed.TryGetValue("artifacts", out var artifactsArg);
        var artifactsRoot = Path.GetFullPath(
            artifactsRootOverride ?? (string.IsNullOrEmpty(artifactsArg) ? DefaultArtifactsRoot : artifactsArg));

        var fromSetDir = Path.Combine(artifactsRoot, fromBuild, platform);
        var toSetDir = Path.Combine(artifactsRoot, toBuild, platform);

        // FAIL-LOUD: both set dirs must exist, and every required family source file must
        // be present OR accounted-for in that build's omissions.json for this platform — BEFORE any
        // emitter work. A genuine hole (missing source, no omission) exits non-zero, nothing written.
        if (!RequireSet(artifactsRoot, fromBuild, platform, fromSetDir, "from"))
            return 65;   // EX_DATAERR
        if (!RequireSet(artifactsRoot, toBuild, platform, toSetDir, "to"))
            return 65;

        var outputPath = Path.Combine(toSetDir, ArtifactSet.ChangelogFileName);

        // Localization changelog family (build-on-demand). localization.json is NOT committed, so —
        // consistent with the inline extract path — it cannot be read from either committed set dir.
        // Regenerate BOTH sides on demand from their content when BOTH committed sets recorded a
        // populated provenance.localization (i.e. both builds produced localization). If either
        // build's content is not resolvable, fail loud with guidance (a localization diff requires the
        // content depot). If either build did NOT produce localization, the changelog stays the five
        // binary families — matching what extract would emit for that pair.
        string? fromLocTemp = null;
        string? toLocTemp = null;
        try
        {
            bool wantLocalization =
                BuildProducedLocalization(artifactsRoot, fromBuild, platform)
                && BuildProducedLocalization(artifactsRoot, toBuild, platform);
            if (wantLocalization)
            {
                if (!ExtractCommand.TryResolveContentVpk(fromBuild, platform, out var fromVpk, out var fromErr))
                {
                    Console.Error.WriteLine($"diff: {fromErr}");
                    return 65;   // EX_DATAERR — localization diff requires the predecessor's content.
                }
                if (!ExtractCommand.TryResolveContentVpk(toBuild, platform, out var toVpk, out var toErr))
                {
                    Console.Error.WriteLine($"diff: {toErr}");
                    return 65;
                }
                fromLocTemp = Path.Combine(Path.GetTempPath(), $"cs2-loc-from-{Guid.NewGuid():N}.json");
                toLocTemp = Path.Combine(Path.GetTempPath(), $"cs2-loc-to-{Guid.NewGuid():N}.json");
                new LocalizationEmitter(SchemaFamily.Version, fromBuild, platform).EmitFromVpk(fromVpk, fromLocTemp);
                new LocalizationEmitter(SchemaFamily.Version, toBuild, platform).EmitFromVpk(toVpk, toLocTemp);
            }

            var emitter = new BuildChangelogEmitter(SchemaFamily.Version, platform, fromBuild, toBuild);
            emitter.Emit(fromSetDir, toSetDir, outputPath, fromLocTemp, toLocTemp);   // build in memory, then atomic write.

            Console.Error.WriteLine(
                $"diff: wrote {ArtifactSet.ChangelogFileName} for {platform} " +
                $"({fromBuild} -> {toBuild}) at {outputPath}" +
                (toLocTemp is not null ? " (with localization family)." : "."));
            return 0;
        }
        finally
        {
            foreach (var t in new[] { fromLocTemp, toLocTemp })
            {
                if (t is not null && File.Exists(t))
                {
                    try
                    { File.Delete(t); }
                    catch { /* best effort */ }
                }
            }
        }
    }

    /// <summary>
    /// True iff the committed set's provenance.json records a populated <c>localization</c>
    /// fingerprint (non-empty sha256) — i.e. that build produced a build-on-demand localization.json.
    /// A missing/unparseable provenance ⇒ false (no localization family).
    /// </summary>
    private static bool BuildProducedLocalization(string artifactsRoot, string buildId, string platform)
    {
        var prov = Path.Combine(artifactsRoot, buildId, platform, ArtifactSet.ProvenanceFileName);
        if (!File.Exists(prov))
            return false;
        try
        {
            var p = TolerantParser.Parse<Schemas.Provenance>(File.ReadAllText(prov));
            return p.Localization is { } loc && !string.IsNullOrEmpty(loc.Sha256);
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fail-loud presence check for one side of the diff. The set dir must exist, and
    /// every required family source file must be present OR that platform must be recorded in the
    /// build's omissions.json. Returns false (with a stderr message) on any hole; the caller exits
    /// non-zero before any bytes are written.
    /// </summary>
    private static bool RequireSet(
        string artifactsRoot, string buildId, string platform, string setDir, string side)
    {
        if (!Directory.Exists(setDir))
        {
            Console.Error.WriteLine(
                $"diff: --{side} build '{buildId}' has no committed set at " +
                $"{Path.Combine("artifacts", buildId, platform)}/ (: refusing).");
            return false;
        }

        // Is this platform recorded as omitted for the build? If so, missing source files are
        // legitimately accounted-for and the diff cannot run against this side (a hole, but a
        // KNOWN one) — surface it loudly rather than silently treating it as empty.
        bool platformOmitted = IsPlatformOmitted(artifactsRoot, buildId, platform);

        var missing = RequiredSourceFiles
            .Where(f => !File.Exists(Path.Combine(setDir, f)))
            .ToList();

        if (missing.Count == 0)
        {
            return true;
        }

        if (platformOmitted)
        {
            Console.Error.WriteLine(
                $"diff: --{side} build '{buildId}' platform '{platform}' is recorded as OMITTED in " +
                $"omissions.json, so its source artifacts ({string.Join(", ", missing)}) are absent " +
                "by design — cannot produce a changelog for an omitted platform (: refusing).");
            return false;
        }

        Console.Error.WriteLine(
            $"diff: --{side} build '{buildId}' platform '{platform}' is MISSING required source " +
            $"artifact(s) {string.Join(", ", missing)} and is NOT recorded in omissions.json " +
            "(: refusing — not silently treating missing as empty).");
        return false;
    }

    /// <summary>
    /// True iff <paramref name="platform"/> appears in the build's omissions.json. A missing or
    /// malformed omissions.json => false (the missing-source path then reports the genuine hole;
    /// a corrupt omissions.json is its own violation surfaced by verify-artifacts).
    /// </summary>
    private static bool IsPlatformOmitted(string artifactsRoot, string buildId, string platform)
    {
        var omissionsFile = Path.Combine(artifactsRoot, buildId, ArtifactSet.OmissionsFileName);
        if (!File.Exists(omissionsFile))
            return false;
        try
        {
            var omissions = TolerantParser.Parse<Omissions>(File.ReadAllText(omissionsFile));
            return omissions.Omissions_.Any(o =>
                string.Equals(o.Platform, platform, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            return false;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"cs2-schema-tracker diff — build-to-build changelog between two committed builds.

Emits the COMMITTED artifact changelog.json under the NEWER build's per-platform dir:
  <artifacts>/<to_build>/<platform>/changelog.json

Usage:
  cs2-schema-tracker diff --from <build> --to <build> [--platform <P>] [--artifacts <root>]

Arguments:
  --from <build>     Predecessor (baseline) build id — changelog.from_build.
  --to <build>       Newer build id — changelog.to_build; the build the file is committed under.
  --platform <P>     linux-x86_64 or windows-x86_64 (required unless set in appsettings.json).
  --artifacts <root> Artifacts root (default: artifacts).

Diffs five binary-derived families (classes, enums, convars, commands, engine_constants), keyed
by name, into added / removed / changed deltas. When BOTH builds produced the build-on-demand
localization.json (recorded in provenance.localization), a 6th `localization` family is appended:
both sides' localization.json are REGENERATED on demand from their content and diffed by token
(localization.json itself is not committed). Deterministic. Fail-loud: a missing set dir, a required
source artifact absent and not accounted-for in omissions.json, or (when a localization diff is
required) a build's content depot not being resolvable, exits non-zero before any bytes are written.

Exit codes: 0 ok · 64 usage error · 65 missing/unaccounted-for input set (incl. unresolvable content).");
    }
}
