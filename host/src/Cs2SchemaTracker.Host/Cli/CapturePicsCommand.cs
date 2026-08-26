// capture-pics — fetch the CURRENT PICS appinfo (anonymous, no depot download) and write the
// pics-appinfo-capture.json sidecar into the (build, platform) binaries-cache directory, exactly
// where `extract --commit` looks for it (see ExtractCommand.FindPicsCapture). A later
// `extract --build <id> --platform <p> --commit` then promotes it to the build-level
// artifacts/<id>/pics-appinfo.json.
//
//   capture-pics --build <id> --platform <p> [--app <id=730>]
//
// This is the FORWARD-CAPTURE guarantee for the scheduled pipeline. Unlike the best-effort capture
// folded into `acquire`, it is:
//   * decoupled from the depot download — the PICS appinfo fetch is anonymous + current-only and
//     succeeds even where an anonymous depot download is throttled/denied; it never depends on the
//     acquirer auth mode the extract auto-acquire happens to select, and
//   * FAIL-LOUD — a fetch/write failure is a non-zero exit, not a swallowed "capture skipped".
//
// PICS is current-only: the fetched appinfo is always the live head build. Two guarantees follow:
//   * SEED-FROM-PRESERVED: when the repo already carries data/pics-captures/<build>.json (a prior
//     run where no set landed preserved its capture), the sidecar is written from THAT file and no
//     fetch happens. The earliest capture wins, and a build whose capture is already safe cannot be
//     lost to a transient PICS flake.
//   * HEAD-MATCH: a freshly fetched appinfo whose embedded public-branch buildid differs from
//     --build (a stale or mistyped explicit id) writes NO sidecar. The raw safety-net dump is still
//     taken, keyed by the ACTUAL head id; the exit stays 0 so a legitimate explicit extract can
//     proceed and land honestly without a pics-appinfo.json.

using System;
using System.Globalization;
using System.IO;
using System.Threading;

using Cs2SchemaTracker.Host.Steam;

namespace Cs2SchemaTracker.Host.Cli;

internal static class CapturePicsCommand
{
    public static int Run(string[] args)
    {
        uint appId = SteamAppIdMap.Cs2AppId;
        string? build = null;
        string? platform = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--build" && i + 1 < args.Length)
            {
                build = args[++i];
            }
            else if (args[i] == "--platform" && i + 1 < args.Length)
            {
                platform = args[++i];
            }
            else if (args[i] == "--app" && i + 1 < args.Length)
            {
                appId = uint.Parse(args[++i], CultureInfo.InvariantCulture);
            }
        }
        if (string.IsNullOrEmpty(build) || string.IsNullOrEmpty(platform))
        {
            Console.Error.WriteLine("capture-pics: --build <id> and --platform <p> are required.");
            return 2;
        }

        // Destination = the acquire-immune forward-capture dir (cache/pics/<build>/<platform>),
        // checked FIRST by ExtractCommand.FindPicsCapture. Deliberately NOT the binaries dir: a
        // binary-depot acquire wipes-and-replaces its outDir, so a capture co-located with the
        // binaries would be destroyed when a following `extract` auto-acquires. This sibling
        // location survives that, so capture-pics may run BEFORE the extract.
        var outDir = PicsAppInfoCapture.ForwardCaptureDir(build, platform);

        // SEED-FROM-PRESERVED: a committed preserved capture IS this build's earliest capture; the
        // sidecar comes from it and no fetch happens (see the header note).
        var preservedPath = Path.GetFullPath(Path.Combine("data", "pics-captures", build + ".json"));
        if (File.Exists(preservedPath))
        {
            try
            {
                var preserved = PicsAppInfoCapture.ReadFromFile(preservedPath);
                preserved.WriteToCacheDir(outDir);
                var seededSidecar = Path.Combine(outDir, PicsAppInfoCapture.FileName);
                Console.Error.WriteLine(
                    $"capture-pics: seeded {seededSidecar} from preserved {preservedPath} " +
                    "(earliest capture wins; no PICS fetch).");
                Console.WriteLine(seededSidecar);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"capture-pics: FAILED reading preserved capture '{preservedPath}': " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        try
        {
            var acquirer = new SteamAnonymousAcquirer();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var (change, sha, kv) = acquirer
                .DumpAppInfoAsync(appId, cts.Token)
                .GetAwaiter().GetResult();

            var capture = PicsAppInfoCapture.FromFetch(
                appId, change, sha, PicsAppInfoRenderer.RenderCanonicalBody(kv));

            // RAW SAFETY NET FIRST: unconditionally capture the jsonified PICS response to
            // <binaries-store-root>/_pics/<headBuild>.json before any sidecar decision, keyed by
            // the id the response actually describes, so a mismatched request cannot mis-file it.
            var embedded = PicsAppInfoCapture.TryGetEmbeddedBuildId(capture.AppInfoJson);
            var rawKey = embedded ?? build;
            var rawDumpPath = Path.Combine(
                AcquireCommand.BinariesStoreRoot(), PicsAppInfoCapture.RawDumpDirName, rawKey + ".json");
            capture.WriteRawDump(AcquireCommand.BinariesStoreRoot(), rawKey);
            Console.Error.WriteLine($"capture-pics: captured raw PICS response -> {rawDumpPath}.");

            // HEAD-MATCH: a capture that describes a different head build gets NO sidecar (see the
            // header note); exit 0 so an explicit-build extract can still proceed without pics.
            if (!string.Equals(embedded, build, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"capture-pics: WARNING the live head build is '{embedded ?? "<none>"}', not '{build}'. " +
                    "PICS is current-only; NOT writing a capture sidecar for this build (it would be " +
                    "mis-associated). The extract, if any, lands without pics-appinfo.json.");
                return 0;
            }

            capture.WriteToCacheDir(outDir);

            var path = Path.Combine(outDir, PicsAppInfoCapture.FileName);
            Console.Error.WriteLine(
                $"capture-pics: wrote {path} (app {appId}, change {change}) — promote via extract --commit.");
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"capture-pics: FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
