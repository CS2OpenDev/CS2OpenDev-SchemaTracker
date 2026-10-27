// The tools-content artifacts: cs_script_api.json and entity_io.json.
//
// Both come from LOOSE files in the Workshop Tools depot (2347779) — Valve's cs_script declaration
// file and the Hammer FGDs — not from the content pak, so they get their own resolution and their
// own absence rules, separate from EmitContentArtifacts:
//
//   linux-x86_64       The tools depot does not exist for this platform. Both artifacts are
//                      recorded as content omissions with reason TOOLS_DEPOT_NOT_ON_PLATFORM.
//   windows, no tools  The tools leg is best-effort (DLC-gated, needs auth). A tuple whose
//                      manifest-record.json does not list 2347779 is a documented SKIP: no file and
//                      NO omission. Recording an omission there would assert the build never shipped
//                      files it may well ship.
//   windows, stale     The record lists 2347779 but tools-content.json is missing or stale: a cache
//                      from before the slice kept these files. That FAILS LOUD. Skipping would land a
//                      set with no tools artifacts and no omission, which verify-artifacts cannot
//                      flag, and a rewalk over an old store would do that to every windows build.
//                      The fix is a tools retrofit (acquire --tools) for the build.
//   windows, tools     Each artifact is emitted when its source file is present; a genuinely absent
//                      source (an era before cs_script) is a CONTENT_NOT_SHIPPED_THIS_ERA omission.
//                      A present-but-malformed source fails loud inside the emitter.
//
// Every tools-content file an emitter read is returned so provenance.inputs can hash it beside the
// binaries (depot-relative path, same as every other input).
//
// --tools-content-root <dir> (development only) reads the files from another tree with the depot
// layout — a local CS2 install with the Workshop Tools — instead of the tuple dir. It bypasses the
// marker check, so it is refused under --commit and for more than one build: the install's files
// belong to whatever build the install is on, not necessarily the one being extracted.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.CsScript;
using Cs2SchemaTracker.Host.EntityIo;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.ToolsContent;
using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.Cli;

internal static partial class ExtractCommand
{
    /// <summary>The artifacts built from the Workshop Tools depot's loose files, in emit order.</summary>
    internal static readonly IReadOnlyList<string> ToolsContentArtifactNames = ArtifactSet.ToolsDepotGatedFiles;

    /// <summary>The only platform the Workshop Tools depot exists for.</summary>
    internal const string ToolsPlatform = ArtifactSet.ToolsPlatform;

    internal sealed record ToolsEmitResult(
        IReadOnlyList<ContentArtifactOmission> Omissions,
        IReadOnlyList<string> InputsRead,
        ToolsContentTree? Tree);

    private readonly record struct ToolsArtifactSpec(
        string FileName,
        Func<ToolsContentTree, bool> HasSource,
        Func<ToolsContentTree, string, IReadOnlyList<string>> Emit,
        string SourceDescription);

    /// <summary>
    /// Resolve where (build, platform)'s tools-content files live. Returns null — with the reason in
    /// <paramref name="note"/> — when the tools depot was not acquired (the file header's "windows,
    /// no tools" case), and throws for a stale slice ("windows, stale"). An explicit
    /// <paramref name="overrideRoot"/> must exist.
    /// </summary>
    internal static ToolsContentTree? ResolveToolsContent(string binariesDir, string? overrideRoot, out string note)
    {
        if (!string.IsNullOrEmpty(overrideRoot))
        {
            if (!Directory.Exists(overrideRoot))
            {
                throw new DirectoryNotFoundException(
                    $"--tools-content-root '{overrideRoot}' does not exist.");
            }
            note = $"tools content from --tools-content-root '{Path.GetFullPath(overrideRoot)}' (development override)";
            return new ToolsContentTree(overrideRoot);
        }

        var recordPath = Path.Combine(binariesDir, ManifestRecord.FileName);
        bool recordListsTools = File.Exists(recordPath)
            && ManifestRecord.ReadFromFile(recordPath).Depots.Any(d => d.DepotId == SteamAppIdMap.Cs2WorkshopToolsDepotId);
        if (!recordListsTools)
        {
            note = $"the Workshop Tools depot {SteamAppIdMap.Cs2WorkshopToolsDepotId} was not acquired for this tuple";
            return null;
        }
        if (!ToolsContentMarker.IsCurrent(binariesDir))
        {
            throw new InvalidDataException(
                $"the tools slice in '{binariesDir}' predates tools-content generation "
                + $"{ToolsBinSelector.ToolsContentGeneration} ({ToolsBinSelector.ToolsContentMarkerFileName} missing or stale), "
                + "so cs_script_api.json and entity_io.json cannot be built and must not be silently left out. "
                + "Retrofit the tools leg first: acquire --tools --build <id> --platform windows-x86_64 --auth.");
        }
        note = $"tools content from '{binariesDir}'";
        return new ToolsContentTree(binariesDir);
    }

    /// <summary>
    /// Emit cs_script_api.json and entity_io.json into <paramref name="stagingDir"/> per the rules
    /// in the file header. Never throws for absence; throws for malformed sources.
    /// </summary>
    internal static ToolsEmitResult EmitToolsContentArtifacts(
        string build, string platform, string binariesDir, string stagingDir, string? toolsContentRoot)
    {
        var omissions = new List<ContentArtifactOmission>();
        if (!string.Equals(platform, ToolsPlatform, StringComparison.Ordinal))
        {
            foreach (var name in ToolsContentArtifactNames)
            {
                omissions.Add(new ContentArtifactOmission
                {
                    Artifact = name,
                    Reason = PlatformOmission.Types.Reason.ToolsDepotNotOnPlatform,
                    Notes = $"built from the Workshop Tools depot {SteamAppIdMap.Cs2WorkshopToolsDepotId}, "
                        + $"which ships for {ToolsPlatform} only",
                });
                Console.Error.WriteLine(
                    $"extract: OMIT {name} — the Workshop Tools depot does not exist for {platform} "
                    + "(recorded as TOOLS_DEPOT_NOT_ON_PLATFORM).");
            }
            return new ToolsEmitResult(omissions, Array.Empty<string>(), null);
        }

        var tree = ResolveToolsContent(binariesDir, toolsContentRoot, out var note);
        if (tree is null)
        {
            foreach (var name in ToolsContentArtifactNames)
            {
                Console.Error.WriteLine(
                    $"extract: SKIP {name} — {note}. This is a documented omission, not a failure.");
            }
            return new ToolsEmitResult(omissions, Array.Empty<string>(), null);
        }
        Console.Error.WriteLine($"extract: {note}.");

        var specs = new[]
        {
            new ToolsArtifactSpec("cs_script_api.json", CsScriptApiEmitter.HasSource,
                (t, o) => new CsScriptApiEmitter(SchemaFamily.Version, build, platform).Emit(t, o),
                $"{CsScriptApiEmitter.DeclarationPath} absent from this build's Workshop Tools depot"),
            new ToolsArtifactSpec("entity_io.json", EntityIoEmitter.HasSource,
                (t, o) => new EntityIoEmitter(SchemaFamily.Version, build, platform).Emit(t, o),
                $"{EntityIoEmitter.RootPath} absent from this build's Workshop Tools depot"),
        };

        var inputs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            if (spec.HasSource(tree))
            {
                foreach (var read in spec.Emit(tree, Path.Combine(stagingDir, spec.FileName)))
                    inputs.Add(read);
                Console.Error.WriteLine($"extract: wrote {spec.FileName} (from {tree.Root})");
            }
            else
            {
                omissions.Add(new ContentArtifactOmission
                {
                    Artifact = spec.FileName,
                    Reason = PlatformOmission.Types.Reason.ContentNotShippedThisEra,
                    Notes = spec.SourceDescription,
                });
                Console.Error.WriteLine(
                    $"extract: OMIT {spec.FileName} — its source is genuinely absent from this build's "
                    + "Workshop Tools depot. Graceful omission, not a failure.");
            }
        }
        return new ToolsEmitResult(omissions, inputs.ToList(), tree);
    }
}
