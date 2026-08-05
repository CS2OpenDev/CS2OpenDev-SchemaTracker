// dump-appinfo — diagnostic. Fetch an app's CURRENT PICS product-info
// (appinfo) and write it to a file. JSON is the default rendering (preferred over
// the on-wire binary VDF); --format vdf emits the raw VDF text instead. Anonymous
// (no credentials) — works for the current public build of app 730. PICS is
// current-only, so this is the live head build, not a historical one.
//
//   dump-appinfo [--app <id=730>] [--format json|vdf] [--out <path>]
//
// Prints the output path on stdout (so callers can capture it).

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

using Cs2SchemaTracker.Host.Steam;

using SteamKit2;

namespace Cs2SchemaTracker.Host.Cli;

internal static class DumpAppInfoCommand
{
    public static int Run(string[] args)
    {
        uint appId = SteamAppIdMap.Cs2AppId;
        string format = "json";
        string? outPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--app" && i + 1 < args.Length)
            {
                appId = uint.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--format" && i + 1 < args.Length)
            {
                format = args[++i].ToLowerInvariant();
            }
            else if (args[i] == "--out" && i + 1 < args.Length)
            {
                outPath = args[++i];
            }
        }
        if (format != "json" && format != "vdf")
        {
            Console.Error.WriteLine($"dump-appinfo: --format must be 'json' or 'vdf' (got '{format}').");
            return 2;
        }
        outPath ??= Path.Combine(Path.GetTempPath(), $"cs2-appinfo-{appId}.{format}");

        try
        {
            var acquirer = new SteamAnonymousAcquirer();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var (change, sha, kv) = acquirer
                .DumpAppInfoAsync(appId, cts.Token)
                .GetAwaiter().GetResult();

            string text = format == "json"
                ? RenderJson(appId, change, sha, kv)
                : RenderVdf(appId, change, sha, kv);
            File.WriteAllText(outPath, text);

            Console.Error.WriteLine(
                $"dump-appinfo: wrote {outPath} (app {appId}, change {change}, format {format})");
            Console.WriteLine(outPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dump-appinfo: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static string RenderJson(uint appId, uint change, string sha, KeyValue kv)
    {
        var top = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["app_id"] = appId,
                ["fetched_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["pics_change_number"] = change,
                ["appinfo_sha1"] = sha,
                ["source"] = "anonymous PICS, current public build (PICS is current-only)",
            },
            ["appinfo"] = PicsAppInfoRenderer.KvToJson(kv),
        };
        return top.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string RenderVdf(uint appId, uint change, string sha, KeyValue kv)
    {
        using var ms = new MemoryStream();
        kv.SaveToStream(ms, asBinary: false);
        var vdf = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        return
            $"// CS2 PICS appinfo (raw VDF) — app {appId}{Environment.NewLine}" +
            $"// fetched {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} via anonymous PICS (current public build){Environment.NewLine}" +
            $"// PICS change_number = {change}{Environment.NewLine}" +
            $"// appinfo SHA-1 = {sha}{Environment.NewLine}{Environment.NewLine}" + vdf;
    }
}
