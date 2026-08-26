// emit-pics: write the build-level artifacts/<build>/pics-appinfo.json from an explicit PICS
// capture file (sidecar format), independent of the extract's promote hook.
//
// The scheduled pipeline's commit job uses this to make the committed pics-appinfo.json derive
// from the RIGHT capture at landing time, whichever leg or run produced it:
//   * a set landed but the extract's non-fatal pics emit failed: emit from the leg's uploaded
//     capture sidecar, so the current-only document is never lost with the run's artifacts;
//   * a preserved data/pics-captures/<build>.json is committed: emit from THAT file, so the
//     earliest capture wins even when a queued run raced past the preservation commit.
//
// The capture's embedded head build id (depots.branches.public.buildid) must equal --build:
// PICS is current-only, so a mismatched capture describes some OTHER build and committing it
// under this one would be a mis-association (a stale or mistyped workflow_dispatch id).
//
// captured_utc framing comes from the promoted provenance.json of the build's present platform
// set (windows-x86_64 preferred, matching the historical first-leg-wins order), never wall clock.
//
// Exit codes: 0 emitted · 64 usage error · 65 unreadable capture / embedded-buildid mismatch /
// no promoted platform set to frame captured_utc from.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.PicsAppInfo;
using Cs2SchemaTracker.Host.Steam;

namespace Cs2SchemaTracker.Host.Cli;

internal static class EmitPicsCommand
{
    private const string DefaultArtifactsRoot = "artifacts";

    private static readonly string[] FramingPlatformPreference =
    {
        "windows-x86_64", "linux-x86_64",
    };

    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker emit-pics: write artifacts/<build>/pics-appinfo.json from an
explicit PICS capture file (pics-appinfo-capture.json sidecar format).

Usage:
  cs2-schema-tracker emit-pics --build <id> --capture <path> [--artifacts <root>]

Arguments:
  --build <id>       Build id to commit the capture under (required).
  --capture <path>   The capture file to emit from (required).
  --artifacts <root> Artifacts root (default: artifacts).

Behavior:
  Refuses a capture whose embedded head build id (depots.branches.public.buildid) differs from
  --build (PICS is current-only; a mismatched capture describes another build). captured_utc comes
  from the build's promoted provenance.json (windows-x86_64 preferred), never wall clock. The write
  is canonical + atomic; re-emitting the same capture is byte-identical.

Exit codes: 0 emitted · 64 usage error · 65 unreadable capture / buildid mismatch / no set.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (!parsed.TryGetValue("build", out var build) || string.IsNullOrEmpty(build))
        {
            Console.Error.WriteLine("emit-pics: --build <id> is required.");
            return 64;
        }
        if (!parsed.TryGetValue("capture", out var capturePath) || string.IsNullOrEmpty(capturePath))
        {
            Console.Error.WriteLine("emit-pics: --capture <path> is required.");
            return 64;
        }
        var artifactsRoot = Path.GetFullPath(
            parsed.TryGetValue("artifacts", out var a) && !string.IsNullOrEmpty(a) ? a : DefaultArtifactsRoot);

        PicsAppInfoCapture capture;
        try
        {
            capture = PicsAppInfoCapture.ReadFromFile(capturePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"emit-pics: could not read capture '{capturePath}': {ex.Message}");
            return 65;
        }

        var embedded = PicsAppInfoCapture.TryGetEmbeddedBuildId(capture.AppInfoJson);
        if (!string.Equals(embedded, build, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"emit-pics: capture '{capturePath}' embeds head build id '{embedded ?? "<none>"}' but " +
                $"--build is '{build}'. PICS is current-only; refusing to commit a mis-associated capture.");
            return 65;
        }

        // Framing timestamp from the promoted provenance of a PRESENT platform set (never wall
        // clock). A provenance without a manifest time does not satisfy the framing: keep looking
        // at the other platform rather than committing an empty captured_utc it could have filled.
        string? capturedUtc = null;
        foreach (var platform in FramingPlatformPreference)
        {
            var provPath = Path.Combine(artifactsRoot, build, platform, ArtifactSet.ProvenanceFileName);
            if (!File.Exists(provPath))
                continue;
            try
            {
                var utc = Cache.ProvenanceReader.ReadManifestCreatedUtc(provPath);
                if (!string.IsNullOrEmpty(utc))
                {
                    capturedUtc = utc;
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Console.Error.WriteLine($"emit-pics: could not read provenance '{provPath}': {ex.Message}");
                return 65;
            }
        }
        if (capturedUtc is null)
        {
            Console.Error.WriteLine(
                $"emit-pics: no promoted platform set under '{Path.Combine(artifactsRoot, build)}' " +
                "carries a steam.manifest_created_utc to frame captured_utc from. Emit rides a " +
                "landed set; it does not create one.");
            return 65;
        }

        var outPath = Path.Combine(artifactsRoot, build, PicsAppInfoEmitter.FileName);
        try
        {
            new PicsAppInfoEmitter(SchemaFamily.Version).Emit(build, capturedUtc, capture, outPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"emit-pics: emit failed: {ex.Message}");
            return 65;
        }

        Console.Error.WriteLine($"emit-pics: wrote {outPath} (change {capture.ChangeNumber}).");
        Console.WriteLine(outPath);
        return 0;
    }
}
