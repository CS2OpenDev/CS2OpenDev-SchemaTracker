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
// PICS is current-only: the fetched appinfo is always the live head build. --build/--platform name
// only the destination directory, so run this against the build that IS current (the one the
// scheduled extract is about to walk).

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
            // <binaries-store-root>/_pics/<build>.json before the curated (build, platform) sidecar
            // write below — see AcquireCommand.TryCapturePicsAppInfoAsync for the same convention.
            var rawDumpPath = Path.Combine(
                AcquireCommand.BinariesStoreRoot(), PicsAppInfoCapture.RawDumpDirName, build + ".json");
            capture.WriteRawDump(AcquireCommand.BinariesStoreRoot(), build);
            Console.Error.WriteLine($"capture-pics: captured raw PICS response -> {rawDumpPath}.");

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
