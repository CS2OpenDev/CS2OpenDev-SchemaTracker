// internal `content-store migrate` dev command.
//
// NOT part of the documented public CLI surface (README.md /): the <binaries-root>
// store — including `_content/<gid>` and the (now-removed) per-platform `game/csgo/`
// co-location — is INTERNAL tooling input, not a public artifact surface.
//
// `content-store migrate` trims each build's co-located content pak IN PLACE (from the
// bytes already on disk — never re-downloads) into the content-addressed trimmed store
// at `<root>/_content/<gid>/game/csgo/`, VALIDATES that the trimmed copy reproduces
// byte-identical content JSON (§7), and only THEN — and only with --reclaim — deletes the
// co-located `game/csgo/pak01_*.vpk` files. It REFUSES to delete any co-located pak whose
// trimmed copy does not reproduce byte-identical content JSON (: fail loud, keep the
// source). Because content is re-acquirable from Steam by the pinned 2347770 manifest_id
// even a worst-case bad trim is recoverable — but a validated trim is provably
// equivalent (it reads the SAME bytes the emitters do).
//
// Default is build+validate only (a dry run that writes/validates `_content/<gid>` and
// reclaims NOTHING). Reclaim is gated behind --reclaim so the full-store reclaim is an
// explicit, separate operator step.

using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Vpk;

namespace Cs2SchemaTracker.Host.Cli;

internal static class ContentStoreCommand
{
    /// <summary>Entry point for `content-store migrate` (the only verb today).</summary>
    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args) || args.Length == 0)
        {
            PrintUsage();
            return args.Length == 0 ? 64 : 0;
        }

        var verb = args[0];
        if (!string.Equals(verb, "migrate", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"content-store: unknown verb '{verb}' (expected 'migrate').");
            PrintUsage();
            return 64;
        }

        var parsed = CliArgs.Parse(args);
        return RunMigrate(parsed);
    }

    private static int RunMigrate(Dictionary<string, string> parsed)
    {
        // Store root: --binaries-root wins, else HostConfig.BinariesRoot (CS2_BINARIES_ROOT / appsettings).
        var root = parsed.TryGetValue("binaries-root", out var r) && !string.IsNullOrEmpty(r)
            ? r
            : HostConfig.BinariesRoot;
        if (string.IsNullOrEmpty(root))
        {
            Console.Error.WriteLine(
                "content-store migrate: no store root — pass --binaries-root <dir> or set CS2_BINARIES_ROOT.");
            return 64;
        }
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"content-store migrate: store root not found: '{root}'.");
            return 66; // EX_NOINPUT
        }

        var buildFilter = parsed.TryGetValue("build", out var b) && !string.IsNullOrEmpty(b) ? b : null;
        var platformFilter = parsed.TryGetValue("platform", out var p) && !string.IsNullOrEmpty(p) ? p : null;
        bool reclaim = parsed.ContainsKey("reclaim");
        bool force = parsed.ContainsKey("force");

        var coLocated = EnumerateCoLocatedPaks(root, buildFilter, platformFilter);
        if (coLocated.Count == 0)
        {
            Console.Error.WriteLine(
                $"content-store migrate: no co-located game/csgo/pak01_dir.vpk under '{root}' " +
                "(nothing to migrate).");
            return 0;
        }

        Console.Error.WriteLine(
            $"content-store migrate: {coLocated.Count} co-located pak(s) under '{root}' " +
            $"(reclaim={(reclaim ? "ON" : "off (build+validate only)")}, force={force}).");

        int built = 0, validated = 0, reclaimed = 0, failed = 0, skipped = 0;

        foreach (var pak in coLocated) // deterministic order
        {
            string tupleDir = pak.TupleDir;
            try
            {
                if (!ContentStore.TryReadContentGid(tupleDir, out var gid))
                {
                    Console.Error.WriteLine(
                        $"content-store migrate: SKIP '{tupleDir}' — no {ContentStore.ContentDepotId} GID in " +
                        "manifest-record.json (cannot content-address this pak).");
                    skipped++;
                    continue;
                }

                var contentRoot = ContentStore.RootForTupleDir(tupleDir)
                    ?? throw new InvalidOperationException(
                        $"cannot derive the _content store root from '{tupleDir}'.");
                var storeDirVpk = ContentStore.ResolveDirVpk(contentRoot, gid);

                // 1) Build/self-heal the trimmed store copy. An existing _content/<gid> is only skipped
                //    when it is a COMPLETE self-contained trim; an incomplete/legacy dir (e.g. the old
                //    python gameevents-only backfill that still references the ORIGINAL external chunks)
                //    is auto-re-trimmed WITHOUT --force. --force re-trims even a complete one.
                var srcArchive = VpkArchive.Open(pak.DirVpkPath);
                var required = ContentPakSelector.EnumerateRequiredEntries(srcArchive);
                if (required.Count == 0)
                {
                    throw new InvalidDataException(
                        $"co-located pak '{pak.DirVpkPath}' has no '.gameevents' entries — refusing to " +
                        "content-address a pak that cannot satisfy.");
                }

                var action = ContentStore.EnsureTrimmedStore(
                    srcArchive, required, contentRoot, gid, force, out var detail);
                if (action == ContentStore.StoreEnsureAction.SkippedComplete)
                {
                    Console.Error.WriteLine(
                        $"content-store migrate: _content/{gid} already a complete trim ({detail}); skipping.");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"content-store migrate: {(action == ContentStore.StoreEnsureAction.Built ? "BUILT" : "RE-TRIMMED")} " +
                        $"_content/{gid} from '{pak.DirVpkPath}' ({required.Count} entries) — {detail}.");
                    built++;
                }

                // 2) VALIDATE: co-located vs trimmed must emit byte-identical content JSON (§7).
                if (!ValidateByteIdentical(pak, storeDirVpk, out var reason))
                {
                    Console.Error.WriteLine(
                        $"content-store migrate: VALIDATION FAILED for '{tupleDir}' — {reason}. " +
                        "Refusing to reclaim the co-located pak.");
                    failed++;
                    continue;
                }
                Console.Error.WriteLine(
                    $"content-store migrate: VALIDATED '{tupleDir}' — trimmed store reproduces byte-identical content JSON.");
                validated++;

                // 3) Guarded reclaim: delete co-located pak01_*.vpk ONLY after a validated trim.
                if (reclaim)
                {
                    int deleted = ReclaimCoLocatedPakFiles(pak);
                    Console.Error.WriteLine(
                        $"content-store migrate: RECLAIMED '{tupleDir}' — deleted {deleted} co-located pak01_*.vpk file(s).");
                    reclaimed++;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
                                          or IOException or FileNotFoundException)
            {
                // Per-build isolation: report and continue, but the run exits non-zero (— a real
                // input problem must not be swallowed). Never reclaim a build that failed to build/validate.
                Console.Error.WriteLine($"content-store migrate: FAILED '{tupleDir}': {ex.Message}");
                failed++;
            }
        }

        Console.Error.WriteLine(
            $"content-store migrate: done — built={built} validated={validated} reclaimed={reclaimed} " +
            $"skipped={skipped} failed={failed} (of {coLocated.Count}).");
        return failed > 0 ? 65 : 0;   // EX_DATAERR on any failure.
    }

    /// <summary>
    /// Emit the 7 content artifacts from BOTH the co-located pak and the trimmed store copy into two
    /// temp dirs and byte-compare every produced file. Byte-identical ⇒ the trim is provably
    /// equivalent for this build. Cleans up the temp dirs.
    /// </summary>
    private static bool ValidateByteIdentical(CoLocatedPak pak, string storeDirVpk, out string reason)
    {
        reason = "";
        string tempA = Path.Combine(Path.GetTempPath(), "cs2-migrate-a-" + Guid.NewGuid().ToString("N"));
        string tempB = Path.Combine(Path.GetTempPath(), "cs2-migrate-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempA);
        Directory.CreateDirectory(tempB);
        try
        {
            ExtractCommand.EmitContentArtifactsFromVpk(pak.DirVpkPath, pak.Build, pak.Platform, tempA);
            ExtractCommand.EmitContentArtifactsFromVpk(storeDirVpk, pak.Build, pak.Platform, tempB);
            return DirsByteIdentical(tempA, tempB, out reason);
        }
        finally
        {
            TryDelete(tempA);
            TryDelete(tempB);
        }
    }

    /// <summary>Byte-compare the file SET and the bytes of every file across two dirs.</summary>
    internal static bool DirsByteIdentical(string dirA, string dirB, out string reason)
    {
        reason = "";
        var filesA = Directory.EnumerateFiles(dirA).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var filesB = Directory.EnumerateFiles(dirB).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (!filesA.SequenceEqual(filesB, StringComparer.Ordinal))
        {
            reason = $"produced file set differs: [{string.Join(",", filesA)}] vs [{string.Join(",", filesB)}]";
            return false;
        }
        foreach (var name in filesA)
        {
            var a = File.ReadAllBytes(Path.Combine(dirA, name!));
            var b = File.ReadAllBytes(Path.Combine(dirB, name!));
            if (!a.AsSpan().SequenceEqual(b))
            {
                reason = $"'{name}' differs ({a.Length} vs {b.Length} bytes)";
                return false;
            }
        }
        return true;
    }

    /// <summary>Delete the co-located <c>game/csgo/pak01_*.vpk</c> files; returns the count removed.</summary>
    private static int ReclaimCoLocatedPakFiles(CoLocatedPak pak)
    {
        var csgoDir = Path.GetDirectoryName(pak.DirVpkPath)!;
        int deleted = 0;
        foreach (var f in Directory.EnumerateFiles(csgoDir, "pak01_*.vpk")
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            File.Delete(f);
            deleted++;
        }
        return deleted;
    }

    private readonly record struct CoLocatedPak(string DirVpkPath, string TupleDir, string Build, string Platform);

    /// <summary>
    /// Every co-located <c>&lt;root&gt;/&lt;build&gt;/&lt;platform&gt;/game/csgo/pak01_dir.vpk</c>
    /// (EXCLUDING the <c>_content</c> store subtree), Ordinal-sorted. Optional build/platform filters.
    /// </summary>
    private static List<CoLocatedPak> EnumerateCoLocatedPaks(
        string root, string? buildFilter, string? platformFilter)
    {
        var result = new List<CoLocatedPak>();
        foreach (var buildDir in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
        {
            var build = Path.GetFileName(buildDir);
            if (build.StartsWith('_'))
                continue;                    // _content and other store-internal dirs.
            if (buildFilter is not null && !string.Equals(build, buildFilter, StringComparison.Ordinal))
                continue;

            foreach (var platDir in Directory.EnumerateDirectories(buildDir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var platform = Path.GetFileName(platDir);
                if (platformFilter is not null && !string.Equals(platform, platformFilter, StringComparison.Ordinal))
                    continue;

                var dirVpk = Path.Combine(platDir, "game", "csgo", "pak01_dir.vpk");
                if (File.Exists(dirVpk))
                {
                    result.Add(new CoLocatedPak(dirVpk, platDir, build, platform));
                }
            }
        }
        return result;
    }

    private static void TryDelete(string dir)
    {
        try
        { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "content-store migrate — trim co-located content paks into the _content/<gid> store, validate, reclaim.");
        Console.Error.WriteLine("  Usage: cs2-schema-tracker content-store migrate [options]");
        Console.Error.WriteLine("  Options:");
        Console.Error.WriteLine("    --binaries-root <dir>  Store root (default: CS2_BINARIES_ROOT / appsettings BinariesRoot).");
        Console.Error.WriteLine("    --build <id>           Limit to one build id.");
        Console.Error.WriteLine("    --platform <p>         Limit to one platform (linux-x86_64 / windows-x86_64).");
        Console.Error.WriteLine("    --force                Re-trim even if _content/<gid> already exists.");
        Console.Error.WriteLine("    --reclaim              Delete co-located pak01_*.vpk AFTER a validated trim (default: off).");
    }
}
