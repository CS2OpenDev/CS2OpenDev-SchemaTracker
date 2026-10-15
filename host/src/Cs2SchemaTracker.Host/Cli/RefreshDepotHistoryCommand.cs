// Maintenance command: refresh depots[].history in data/cs2-assets-inventory.json from facts the
// repo already committed.
//
// Idempotent and merge-only. Two sources, neither of which contacts Steam:
//   builds[]  — for the depots whose manifest every build already records (role content / binary /
//               tools). Complete per-build coverage, so the depots the acquire paths actually
//               resolve get an exact history rather than a sampled one.
//   artifacts/<build>/pics-appinfo.json — the committed PICS captures, which carry a public GID for
//               EVERY depot the app touches, including the ones builds[] says nothing about
//               (csgo-legacy, cs2-other). As dense as the captures are and no denser.
//
// WHY: the histories were last written 2026-07-09 (newest entry build 24134959) and nothing in the
// host maintained them — they came from SteamDB patch pages by hand (_meta.provenance), and that
// stopped. _meta.purpose makes them load-bearing: "any depot's manifest at any build resolves from
// depots[].history (newest entry whose change_number <= the build's, or date_utc <= the build
// date)". A stale table does not fail, it answers with a months-old GID — which is how
// `acquire --content --build <historical>` was quietly resolving wrong.
//
// Surface: refresh-depot-history [--inventory <path>] [--artifacts <dir>] [--dry-run]
//   --inventory defaults to CS2_INVENTORY_PATH / appsettings, else repo-root data/cs2-assets-inventory.json.
//   --artifacts defaults to <repo root>/artifacts.
//   --dry-run reports what would change and writes nothing.

using System.Globalization;
using System.Text.Json;

using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.Inventory;
using Cs2SchemaTracker.Host.Walker;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static class RefreshDepotHistoryCommand
{
    private static readonly JsonParser PicsParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker refresh-depot-history — refresh depots[].history in the inventory.

Usage:
  cs2-schema-tracker refresh-depot-history [--inventory <path>] [--artifacts <dir>] [--dry-run]

Merge-only and idempotent: an existing entry is never dropped and a recorded manifest is never
rewritten. Rebuilds the content/binary/tools depot histories from builds[] (complete per-build
coverage) and merges every committed artifacts/<build>/pics-appinfo.json capture for the remaining
depots. Contacts Steam for nothing. Exit codes: 0 ok · 65 missing/invalid inventory or artifacts.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        parsed.TryGetValue("inventory", out var inventoryArg);
        parsed.TryGetValue("artifacts", out var artifactsArg);
        bool dryRun = parsed.ContainsKey("dry-run");

        string repoRoot;
        try
        {
            repoRoot = EraWalkerResolver.DiscoverRepoRoot();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"refresh-depot-history: {ex.Message}");
            return 65;
        }

        var inventoryPath = !string.IsNullOrEmpty(inventoryArg)
            ? inventoryArg
            : HostConfig.InventoryPath
              ?? Path.Combine(repoRoot, "data", "cs2-assets-inventory.json");

        var artifactsRoot = !string.IsNullOrEmpty(artifactsArg)
            ? artifactsArg
            : Path.Combine(repoRoot, "artifacts");

        try
        {
            var (observations, captures, skipped) = ReadCaptures(artifactsRoot);

            Console.Error.WriteLine(
                $"refresh-depot-history: read {captures} PICS capture(s) under '{artifactsRoot}' " +
                $"-> {observations.Count} depot observation(s)" +
                (skipped > 0 ? $"; {skipped} capture(s) carried no parseable depot manifests." : "."));

            var result = InventoryWriter.MergeDepotHistories(
                inventoryPath, observations, rederiveFromBuilds: true, dryRun: dryRun);

            if (result.NoChange)
            {
                Console.Error.WriteLine(
                    "refresh-depot-history: depots[].history is already current — nothing to do.");
                return 0;
            }

            var verb = dryRun ? "WOULD add" : "added";
            Console.Error.WriteLine(
                $"refresh-depot-history: {verb} {result.EntriesAdded} entr(ies) across " +
                $"{result.DepotsChanged} depot(s)" +
                (result.EntriesResolvedToBuilds > 0
                    ? $"; resolved {result.EntriesResolvedToBuilds} build-less entr(ies) to their build."
                    : "."));
            Console.Error.WriteLine(dryRun
                ? "refresh-depot-history: --dry-run, nothing written."
                : $"refresh-depot-history: rewrote {inventoryPath}.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"refresh-depot-history: {ex.Message}");
            return 65;
        }
    }

    /// <summary>
    /// Every (depot, build) public manifest the committed captures record. Fail-loud on a capture
    /// that EXISTS but does not parse as PicsAppInfo (that is corruption, not absence) — but the
    /// appinfo body itself is declared OPAQUE by schemas/pics_appinfo.proto ("do not assume any key
    /// is present"), so every lookup inside it is defensive and a capture that simply carries no
    /// usable depot block is counted and skipped rather than failing the run.
    /// </summary>
    private static (List<DepotManifestObservation> Observations, int Captures, int Skipped) ReadCaptures(
        string artifactsRoot)
    {
        if (!Directory.Exists(artifactsRoot))
        {
            throw new InvalidDataException($"artifacts root '{artifactsRoot}' does not exist.");
        }

        var observations = new List<DepotManifestObservation>();
        int captures = 0, skipped = 0;

        var fileName = PicsAppInfo.PicsAppInfoEmitter.FileName;
        foreach (var dir in Directory.EnumerateDirectories(artifactsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var path = Path.Combine(dir, fileName);
            if (!File.Exists(path))
                continue;

            captures++;
            Schemas.PicsAppInfo pics;
            try
            {
                pics = PicsParser.Parse<Schemas.PicsAppInfo>(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
            {
                throw new InvalidDataException(
                    $"pics-appinfo.json at '{path}' does not parse as PicsAppInfo: {ex.Message}", ex);
            }

            if (!long.TryParse(pics.BuildId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var buildId)
                || buildId <= 0)
            {
                skipped++;
                continue;
            }

            long? changeNumber = long.TryParse(
                pics.ChangeNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cn) && cn > 0
                ? cn
                : null;

            var before = observations.Count;
            foreach (var (depotId, gid) in PublicManifests(pics.AppinfoJson, path))
            {
                observations.Add(new DepotManifestObservation(
                    depotId, buildId, changeNumber, pics.CapturedUtc ?? "", gid));
            }
            if (observations.Count == before)
                skipped++;
        }

        return (observations, captures, skipped);
    }

    /// <summary>
    /// depot id -> public-branch manifest GID out of the opaque appinfo body
    /// (<c>depots.&lt;numeric&gt;.manifests.public.gid</c>). Non-numeric depot keys (branch
    /// metadata such as <c>branches</c> / <c>baselanguages</c>) and any depot without a public
    /// manifest are skipped. A body that is not JSON at all is corruption and throws.
    /// </summary>
    private static IEnumerable<(uint DepotId, string Gid)> PublicManifests(string? appinfoJson, string path)
    {
        if (string.IsNullOrWhiteSpace(appinfoJson))
            yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(appinfoJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"the appinfo body in '{path}' is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("depots", out var depots)
                || depots.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var depot in depots.EnumerateObject())
            {
                if (!uint.TryParse(depot.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depotId))
                    continue;
                if (depot.Value.ValueKind != JsonValueKind.Object
                    || !depot.Value.TryGetProperty("manifests", out var manifests)
                    || manifests.ValueKind != JsonValueKind.Object
                    || !manifests.TryGetProperty("public", out var pub)
                    || pub.ValueKind != JsonValueKind.Object
                    || !pub.TryGetProperty("gid", out var gid)
                    || gid.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var value = gid.GetString();
                if (!string.IsNullOrEmpty(value))
                    yield return (depotId, value);
            }
        }
    }
}
