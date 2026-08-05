// internal `reconcile-content-gids` dev command.
//
// NOT part of the documented public CLI surface (README.md /): the <binaries-root>
// store and its per-tuple `manifest-record.json` files are INTERNAL tooling input.
//
// === Why this exists ===
// The SteamDB ingest CORRECTED the content-depot (2347770) GIDs in
// data/cs2-assets-inventory.json (the "80 content-GID corrections"), but the per-build
// on-disk `<build>/<platform>/manifest-record.json` files still carry the OLD
// (date-map-estimate) GIDs. Extract now resolves content via the manifest-record's 2347770
// GID, so a stale record points `_content/<gid>` resolution at the WRONG copy. For ~80
// builds the two platforms' manifest-records ALSO disagree (win vs lin captured a different
// GID), which breaks cross-platform dedup and indicates some content came from a NEIGHBOR
// manifest.
//
// This command compares each on-disk manifest-record's 2347770 GID to the AUTHORITATIVE GID
// for that build in the inventory (builds[].content):
//   --check  report-only: builds whose on-disk GID != inventory GID, and builds whose
//            platforms disagree. Exit 1 on drift, 0 when clean.
//   --apply  rewrite the manifest-record's 2347770 GID to the authoritative inventory value,
//            byte-for-byte preserving the rest of the record. Then LOUDLY warn that each
//            reconciled build's content must be RE-ACQUIRED by the authoritative GID (the
//            existing _content/<old-gid> is stale/possibly-neighbor content). This command
//            NEVER re-acquires — it only fixes the METADATA.
//
// Fail-loud: an unreadable manifest-record, or a store build absent from the
// inventory, is a real input problem — reported and the run exits non-zero (65). Determinism
// tuple dirs are processed Ordinal-sorted; --apply splices ONLY the GID token.

using System.Globalization;
using System.Text;

using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Walker;

namespace Cs2SchemaTracker.Host.Cli;

internal static class ReconcileContentGidsCommand
{
    public static int Run(string[] args) => Run(args, Console.Error);

    /// <summary>Testable entry point (log sink injected).</summary>
    internal static int Run(string[] args, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(log);

        if (CliArgs.HasHelpFlag(args))
        {
            PrintUsage(log);
            return 0;
        }

        var parsed = CliArgs.Parse(args);

        // Store root: --binaries-root wins, else HostConfig.BinariesRoot.
        var root = parsed.TryGetValue("binaries-root", out var r) && !string.IsNullOrEmpty(r)
            ? r
            : HostConfig.BinariesRoot;
        if (string.IsNullOrEmpty(root))
        {
            log.WriteLine(
                "reconcile-content-gids: no store root — pass --binaries-root <dir> or set CS2_BINARIES_ROOT.");
            return 64;
        }
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            log.WriteLine($"reconcile-content-gids: store root not found: '{root}'.");
            return 66; // EX_NOINPUT
        }

        // Inventory path: --inventory wins, else repo-root/data/cs2-assets-inventory.json.
        var inventoryPath = parsed.TryGetValue("inventory", out var inv) && !string.IsNullOrEmpty(inv)
            ? Path.GetFullPath(inv)
            : Path.Combine(EraWalkerResolver.DiscoverRepoRoot(), AssetsInventory.DefaultRelativePath);

        bool apply = parsed.ContainsKey("apply");
        bool check = parsed.ContainsKey("check");
        if (apply && check)
        {
            log.WriteLine("reconcile-content-gids: pass exactly one of --check or --apply (not both).");
            return 64;
        }
        // Default is the safe, read-only --check.
        if (!apply)
        {
            check = true;
        }

        var buildFilter = parsed.TryGetValue("build", out var b) && !string.IsNullOrEmpty(b) ? b : null;
        var platformFilter = parsed.TryGetValue("platform", out var p) && !string.IsNullOrEmpty(p) ? p : null;

        return Reconcile(root, inventoryPath, apply, buildFilter, platformFilter, log);
    }

    /// <summary>
    /// The reconcile core: compare every on-disk <c>manifest-record.json</c>'s 2347770 GID against the
    /// inventory's authoritative <c>builds[].content</c> value; report drift + platform disagreement,
    /// and (when <paramref name="apply"/>) rewrite the stale GID. Returns 65 on any fail-loud error
    /// (unreadable record / build absent from inventory), 1 on --check drift, 0 when clean/applied.
    /// </summary>
    internal static int Reconcile(
        string storeRoot,
        string inventoryPath,
        bool apply,
        string? buildFilter,
        string? platformFilter,
        TextWriter log)
    {
        // Fail-loud: a missing / malformed inventory throws InvalidDataException.
        var inventory = AssetsInventory.Load(inventoryPath);

        var records = EnumerateManifestRecords(storeRoot, buildFilter, platformFilter);
        if (records.Count == 0)
        {
            log.WriteLine(
                $"reconcile-content-gids: no <build>/<platform>/{ManifestRecord.FileName} under '{storeRoot}' (nothing to reconcile).");
            return 0;
        }

        log.WriteLine(
            $"reconcile-content-gids: {records.Count} manifest-record(s) under '{storeRoot}' " +
            $"vs inventory '{inventoryPath}' (mode={(apply ? "APPLY" : "check")}).");

        int errors = 0, mismatches = 0, applied = 0, ok = 0, noContentDepot = 0;

        // build_id -> (platform -> on-disk content GID), to detect cross-platform disagreement.
        var perBuildPlatformGid = new SortedDictionary<uint, SortedDictionary<string, ulong>>();
        // build_id -> authoritative GID, for the RE-ACQUIRE warning list (dedup by build).
        var reconciledBuilds = new SortedDictionary<uint, (ulong OldGid, ulong AuthGid)>();

        foreach (var rec in records) // Ordinal-sorted
        {
            if (!uint.TryParse(rec.Build, NumberStyles.None, CultureInfo.InvariantCulture, out var buildId))
            {
                // A non-numeric build dir under the store root is not a real build tuple; skip quietly.
                continue;
            }

            ManifestRecord record;
            try
            {
                record = ManifestRecord.ReadFromFile(rec.RecordPath);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException
                                          or FileNotFoundException or UnauthorizedAccessException)
            {
                log.WriteLine($"reconcile-content-gids: ERROR unreadable record '{rec.RecordPath}': {ex.Message}");
                errors++;
                continue;
            }

            var content = record.Depots.FirstOrDefault(d => d.DepotId == ContentStore.ContentDepotId);
            if (content is null)
            {
                // Binary-only record (no content depot acquired) — nothing to reconcile.
                noContentDepot++;
                continue;
            }
            ulong onDiskGid = content.ManifestId;

            if (!perBuildPlatformGid.TryGetValue(buildId, out var byPlatform))
            {
                byPlatform = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
                perBuildPlatformGid[buildId] = byPlatform;
            }
            byPlatform[rec.Platform] = onDiskGid;

            if (!inventory.HasBuild(buildId))
            {
                log.WriteLine(
                    $"reconcile-content-gids: ERROR build {buildId} ({rec.Platform}) is absent from the inventory " +
                    $"'{inventoryPath}' — cannot resolve an authoritative content GID.");
                errors++;
                continue;
            }

            var authGidNullable = inventory.ContentGidFor(buildId);
            if (authGidNullable is not { } authGid)
            {
                log.WriteLine(
                    $"reconcile-content-gids: ERROR build {buildId} ({rec.Platform}) is in the inventory but has NO " +
                    "authoritative content GID (builds[].content) — cannot reconcile.");
                errors++;
                continue;
            }

            if (onDiskGid == authGid)
            {
                ok++;
                continue;
            }

            mismatches++;
            reconciledBuilds[buildId] = (onDiskGid, authGid);
            log.WriteLine(
                $"reconcile-content-gids: {(apply ? "FIX" : "DRIFT")} build {buildId} ({rec.Platform}) " +
                $"on-disk 2347770 GID {onDiskGid} != inventory {authGid}.");

            if (apply)
            {
                try
                {
                    RewriteContentGid(rec.RecordPath, onDiskGid, authGid);
                    applied++;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    log.WriteLine($"reconcile-content-gids: ERROR rewriting '{rec.RecordPath}': {ex.Message}");
                    errors++;
                }
            }
        }

        // Cross-platform disagreement: a build whose platforms recorded DIFFERENT on-disk content GIDs.
        int disagreements = 0;
        foreach (var (buildId, byPlatform) in perBuildPlatformGid)
        {
            var distinct = byPlatform.Values.Distinct().ToList();
            if (distinct.Count > 1)
            {
                disagreements++;
                var detail = string.Join(", ", byPlatform.Select(kv => $"{kv.Key}={kv.Value}"));
                log.WriteLine(
                    $"reconcile-content-gids: DISAGREE build {buildId} platforms recorded different content GIDs [{detail}] " +
                    "(cross-platform dedup broken; likely neighbor-manifest content).");
            }
        }

        log.WriteLine(
            $"reconcile-content-gids: done — records={records.Count} ok={ok} " +
            $"{(apply ? $"fixed={applied}" : $"drift={mismatches}")} disagreements={disagreements} " +
            $"noContentDepot={noContentDepot} errors={errors}.");

        if (apply && reconciledBuilds.Count > 0)
        {
            log.WriteLine(
                "reconcile-content-gids: WARNING — the following build(s) had their manifest-record content GID " +
                "reconciled. Their existing _content/<old-gid> is STALE (possibly neighbor-manifest content) and " +
                "MUST be RE-ACQUIRED by the authoritative GID (this command fixed ONLY the metadata, not the content):");
            foreach (var (buildId, gids) in reconciledBuilds)
            {
                log.WriteLine(
                    $"reconcile-content-gids:   build {buildId}: re-acquire content GID {gids.AuthGid} " +
                    $"(was {gids.OldGid}).");
            }
        }

        if (errors > 0)
        {
            return 65; // EX_DATAERR — a real input problem.
        }
        if (!apply && (mismatches > 0 || disagreements > 0))
        {
            return 1;  // --check drift signal.
        }
        return 0;
    }

    private readonly record struct RecordRef(string Build, string Platform, string TupleDir, string RecordPath);

    /// <summary>
    /// Every <c>&lt;root&gt;/&lt;build&gt;/&lt;platform&gt;/manifest-record.json</c> (EXCLUDING the
    /// <c>_content</c> store subtree), Ordinal-sorted. Optional build/platform filters.
    /// </summary>
    private static List<RecordRef> EnumerateManifestRecords(
        string root, string? buildFilter, string? platformFilter)
    {
        var result = new List<RecordRef>();
        foreach (var buildDir in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
        {
            var build = Path.GetFileName(buildDir);
            if (build.StartsWith('_'))
                continue;   // _content and other store-internal dirs.
            if (buildFilter is not null && !string.Equals(build, buildFilter, StringComparison.Ordinal))
                continue;

            foreach (var platDir in Directory.EnumerateDirectories(buildDir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var platform = Path.GetFileName(platDir);
                if (platformFilter is not null && !string.Equals(platform, platformFilter, StringComparison.Ordinal))
                    continue;

                var recordPath = Path.Combine(platDir, ManifestRecord.FileName);
                if (File.Exists(recordPath))
                {
                    result.Add(new RecordRef(build, platform, platDir, recordPath));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Rewrite the single 2347770 content-GID token in a manifest-record.json from
    /// <paramref name="oldGid"/> to <paramref name="newGid"/>, preserving every other byte of the file.
    /// The GID is a quoted decimal uint64 (canonical form), so the token <c>"&lt;gid&gt;"</c> is spliced
    /// verbatim. Fail-loud: if the token does not appear EXACTLY once (ambiguous / already
    /// rewritten), throw rather than guess. Deterministic: a pure byte splice.
    /// </summary>
    internal static void RewriteContentGid(string recordPath, ulong oldGid, ulong newGid)
    {
        byte[] content = File.ReadAllBytes(recordPath);
        byte[] find = Encoding.UTF8.GetBytes("\"" + oldGid.ToString(CultureInfo.InvariantCulture) + "\"");
        byte[] repl = Encoding.UTF8.GetBytes("\"" + newGid.ToString(CultureInfo.InvariantCulture) + "\"");

        int count = CountOccurrences(content, find);
        if (count != 1)
        {
            throw new InvalidDataException(
                $"reconcile-content-gids: expected exactly one occurrence of the content GID token " +
                $"\"{oldGid}\" in '{recordPath}' but found {count}; refusing to rewrite ambiguously.");
        }

        int at = IndexOf(content, find, 0);
        var result = new byte[content.Length - find.Length + repl.Length];
        Array.Copy(content, 0, result, 0, at);
        Array.Copy(repl, 0, result, at, repl.Length);
        Array.Copy(content, at + find.Length, result, at + repl.Length, content.Length - at - find.Length);

        var tmp = recordPath + ".reconcile.tmp";
        File.WriteAllBytes(tmp, result);
        File.Move(tmp, recordPath, overwrite: true);
    }

    private static int CountOccurrences(byte[] haystack, byte[] needle)
    {
        int count = 0, from = 0, at;
        while ((at = IndexOf(haystack, needle, from)) >= 0)
        {
            count++;
            from = at + needle.Length;
        }
        return count;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
                j++;
            if (j == needle.Length)
                return i;
        }
        return -1;
    }

    private static void PrintUsage(TextWriter log)
    {
        log.WriteLine(
            "reconcile-content-gids — fix stale on-disk manifest-record content (2347770) GIDs against the inventory.");
        log.WriteLine("  Usage: cs2-schema-tracker reconcile-content-gids [--check | --apply] [options]");
        log.WriteLine("  Options:");
        log.WriteLine("    --binaries-root <dir>  Store root (default: CS2_BINARIES_ROOT / appsettings BinariesRoot).");
        log.WriteLine("    --inventory <path>     Inventory path (default: data/cs2-assets-inventory.json).");
        log.WriteLine("    --check                Report-only: drift + cross-platform disagreement (exit 1 on drift). DEFAULT.");
        log.WriteLine("    --apply                Rewrite each stale record's 2347770 GID to the authoritative inventory value.");
        log.WriteLine("    --build <id>           Limit to one build id.");
        log.WriteLine("    --platform <p>         Limit to one platform (linux-x86_64 / windows-x86_64).");
    }
}
