// merge-omissions: fold ONE platform's content-omission carrier from an external omissions.json
// into the build-level artifacts/<build>/omissions.json in the current tree.
//
// The scheduled pipeline's commit job runs this once per landed leg. Each leg uploads the
// omissions.json its extract reconciled against the run's trigger-SHA tree; taking either file
// whole would drop the OTHER platform's carrier (the exact wedge commit-plan then refuses with
// exit 65). This command extracts only the named platform's carrier from the leg file and merges
// it through BuildLevelOmissions.ReconcilePlatformContentOmissions, so every other platform entry
// in the checkout's committed manifest survives verbatim and the output stays canonical.
//
// An absent --from file means the leg recorded no omissions for that platform: the carrier is
// reconciled to empty (a no-op unless a stale carrier must be cleared).
//
// Exit codes: 0 merged/no-op · 64 usage error · 65 unreadable leg file / corrupt build manifest.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static class MergeOmissionsCommand
{
    private const string DefaultArtifactsRoot = "artifacts";

    // STRICT on purpose: this command rewrites a COMMITTED manifest canonically, so a field this
    // host's schema does not know (a manifest written by a newer host than the release bundle)
    // must refuse rather than be silently dropped by the rewrite.
    private static readonly JsonParser StrictParser = new(JsonParser.Settings.Default);

    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker merge-omissions: fold one platform's content-omission carrier
from an external omissions.json into the build-level artifacts/<build>/omissions.json.

Usage:
  cs2-schema-tracker merge-omissions --build <id> --platform <p> --from <path> [--artifacts <root>]

Arguments:
  --build <id>       Build id whose manifest is merged into (required).
  --platform <p>     The platform whose carrier is taken from --from (required).
  --from <path>      The source omissions.json (a leg upload). Absent = empty carrier.
  --artifacts <root> Artifacts root (default: artifacts).

Behavior:
  Only the named platform's content carrier is read from --from; every other platform entry in the
  target manifest is preserved verbatim (BuildLevelOmissions reconcile). No-op when nothing about
  the platform's carrier changes. Canonical, atomic write.

Exit codes: 0 merged/no-op · 64 usage error · 65 unreadable source / corrupt target manifest.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (!parsed.TryGetValue("build", out var build) || string.IsNullOrEmpty(build))
        {
            Console.Error.WriteLine("merge-omissions: --build <id> is required.");
            return 64;
        }
        if (!parsed.TryGetValue("platform", out var platform) || string.IsNullOrEmpty(platform))
        {
            Console.Error.WriteLine("merge-omissions: --platform <p> is required.");
            return 64;
        }
        if (!ArtifactSet.CanonicalPlatforms.Contains(platform, StringComparer.Ordinal))
        {
            Console.Error.WriteLine(
                $"merge-omissions: '{platform}' is not a canonical platform " +
                $"(expected one of: {string.Join(", ", ArtifactSet.CanonicalPlatforms)}).");
            return 64;
        }
        if (!parsed.TryGetValue("from", out var fromPath) || string.IsNullOrEmpty(fromPath))
        {
            Console.Error.WriteLine("merge-omissions: --from <path> is required.");
            return 64;
        }
        var artifactsRoot = Path.GetFullPath(
            parsed.TryGetValue("artifacts", out var a) && !string.IsNullOrEmpty(a) ? a : DefaultArtifactsRoot);

        // Both files must parse STRICTLY before any write: the reconcile rewrites the target
        // canonically, so an unknown field in either file would otherwise vanish from the commit.
        var targetPath = Path.Combine(artifactsRoot, build, ArtifactSet.OmissionsFileName);
        if (File.Exists(targetPath))
        {
            try
            {
                StrictParser.Parse<Omissions>(File.ReadAllText(targetPath));
            }
            catch (Exception ex) when (ex is IOException or InvalidJsonException or InvalidProtocolBufferException)
            {
                Console.Error.WriteLine(
                    $"merge-omissions: refusing to rewrite '{targetPath}': {ex.Message} " +
                    "(a field this host does not know would be dropped; update the host first).");
                return 65;
            }
        }

        var carrier = new List<ContentArtifactOmission>();
        if (File.Exists(fromPath))
        {
            Omissions source;
            try
            {
                source = StrictParser.Parse<Omissions>(File.ReadAllText(fromPath));
            }
            catch (Exception ex) when (ex is IOException or InvalidJsonException or InvalidProtocolBufferException)
            {
                Console.Error.WriteLine($"merge-omissions: could not read '{fromPath}': {ex.Message}");
                return 65;
            }
            var entry = source.Omissions_.FirstOrDefault(o =>
                string.Equals(o.Platform, platform, StringComparison.Ordinal)
                && o.Reason == PlatformOmission.Types.Reason.Unspecified);
            if (entry is not null)
            {
                carrier.AddRange(entry.ContentOmissions);
            }
        }

        try
        {
            BuildLevelOmissions.ReconcilePlatformContentOmissions(
                Path.Combine(artifactsRoot, build), build, platform, carrier);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"merge-omissions: reconcile failed: {ex.Message}");
            return 65;
        }

        Console.WriteLine(
            $"merge-omissions: {carrier.Count} content omission(s) for {platform} reconciled into " +
            $"build {build}'s manifest.");
        return 0;
    }
}
