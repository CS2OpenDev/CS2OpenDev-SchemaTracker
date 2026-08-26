// reconcile-changelog: make one (build, platform) set's changelog.json agree with the tree it is
// about to be committed into.
//
// The scheduled legs emit changelog.json inline against the run's trigger-SHA tree, but the commit
// job lands the set on main's TRUE tip, whose immediate committed predecessor can be newer (a
// partial completion or a second build landed in between). Committing the leg's from_build anyway
// would wedge the verify-artifacts predecessor gate on every later push. This command re-resolves
// the predecessor with the SAME shared rule the gate uses (ChangelogPredecessor) and regenerates
// the five binary families when the on-disk changelog disagrees (or is missing). An in-sync
// changelog is left byte-untouched.
//
// The regenerated file carries the binary families only, exactly like the legs' own
// --no-localization-changelog emit: a predecessor's localization cannot be re-derived here.
//
// Exit codes: 0 in-sync / regenerated / floor build · 64 usage error · 65 inconsistent tree or a
// regeneration failure.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Changelog;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static class ReconcileChangelogCommand
{
    private const string DefaultArtifactsRoot = "artifacts";

    private static readonly JsonParser TolerantParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker reconcile-changelog: regenerate one set's changelog.json when its
from_build disagrees with the immediate committed predecessor in the current tree.

Usage:
  cs2-schema-tracker reconcile-changelog --build <id> --platform <p> [--artifacts <root>]

Arguments:
  --build <id>       Build id whose set is on disk (required).
  --platform <p>     linux-x86_64 or windows-x86_64 (required).
  --artifacts <root> Artifacts root (default: artifacts).

Behavior:
  Resolves the predecessor with the shared ChangelogPredecessor rule (the one verify-artifacts
  gates on). In-sync changelog: no write. Stale or missing: regenerated from the two on-disk sets,
  five binary families (a predecessor's localization cannot be re-derived). Floor build: nothing
  required; a changelog PRESENT on the floor build is an inconsistency and fails loud.

Exit codes: 0 in-sync/regenerated/floor · 64 usage error · 65 inconsistent tree / emit failure.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (!parsed.TryGetValue("build", out var build) || string.IsNullOrEmpty(build))
        {
            Console.Error.WriteLine("reconcile-changelog: --build <id> is required.");
            return 64;
        }
        if (!parsed.TryGetValue("platform", out var platform) || string.IsNullOrEmpty(platform))
        {
            Console.Error.WriteLine("reconcile-changelog: --platform <linux-x86_64|windows-x86_64> is required.");
            return 64;
        }
        var artifactsRoot = Path.GetFullPath(
            parsed.TryGetValue("artifacts", out var a) && !string.IsNullOrEmpty(a) ? a : DefaultArtifactsRoot);

        var setDir = Path.Combine(artifactsRoot, build, platform);
        if (!Directory.Exists(setDir))
        {
            Console.Error.WriteLine($"reconcile-changelog: no set dir at '{setDir}'.");
            return 65;
        }
        var changelogPath = Path.Combine(setDir, ArtifactSet.ChangelogFileName);

        var predecessor = ChangelogPredecessor.Resolve(artifactsRoot, build, platform);
        if (predecessor is null)
        {
            if (File.Exists(changelogPath))
            {
                Console.Error.WriteLine(
                    $"reconcile-changelog: build {build} ({platform}) is the earliest committed build " +
                    $"for the platform yet carries {ArtifactSet.ChangelogFileName}. A floor build must " +
                    "not have one; the tree is inconsistent.");
                return 65;
            }
            Console.WriteLine(
                $"reconcile-changelog: build {build} ({platform}) is the platform floor. No changelog required.");
            return 0;
        }

        if (File.Exists(changelogPath))
        {
            try
            {
                var current = TolerantParser.Parse<Schemas.BuildChangelog>(File.ReadAllText(changelogPath));
                if (string.Equals(current.FromBuild, predecessor, StringComparison.Ordinal)
                    && string.Equals(current.ToBuild, build, StringComparison.Ordinal))
                {
                    Console.WriteLine(
                        $"reconcile-changelog: build {build} ({platform}) changelog is in sync " +
                        $"(from_build={predecessor}). No write.");
                    return 0;
                }
                Console.Error.WriteLine(
                    $"reconcile-changelog: build {build} ({platform}) changelog has " +
                    $"from_build='{current.FromBuild}' but the immediate committed predecessor is " +
                    $"'{predecessor}'. Regenerating.");
            }
            catch (Exception ex) when (ex is IOException or InvalidJsonException or InvalidProtocolBufferException)
            {
                Console.Error.WriteLine(
                    $"reconcile-changelog: existing changelog unreadable ({ex.Message}). Regenerating.");
            }
        }
        else
        {
            Console.Error.WriteLine(
                $"reconcile-changelog: build {build} ({platform}) has no changelog but predecessor " +
                $"'{predecessor}' is committed. Generating.");
        }

        try
        {
            var fromSetDir = Path.Combine(artifactsRoot, predecessor, platform);
            new BuildChangelogEmitter(SchemaFamily.Version, platform, predecessor, build)
                .Emit(fromSetDir, setDir, changelogPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"reconcile-changelog: regeneration failed: {ex.GetType().Name}: {ex.Message}");
            return 65;
        }

        Console.WriteLine(
            $"reconcile-changelog: regenerated build {build} ({platform}) changelog " +
            $"(from_build={predecessor}).");
        return 0;
    }
}
