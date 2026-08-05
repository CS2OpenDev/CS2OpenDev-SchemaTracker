// Content-depot minimal-footprint file selection.
//
// The CS2 shared-content depot (2347770) is ~59 GB, but for gameevents the host only needs the
// `.gameevents` resources inside game/csgo/pak01_*.vpk — a tiny slice. This class decides EXACTLY
// which files of the content depot to fetch, in two phases:
//
//   Phase A (directory only): fetch game/csgo/pak01_dir.vpk. The directory file is small (a few MB)
//   and is the index into the pak01 archive set. Selected by an exact, case-insensitive
//   manifest-path match, so the bulk depot is never pulled.
//
//   Phase B (minimal chunks): parse the fetched pak01_dir.vpk with VpkArchive, find which external
//   archive indices (`_NNN.vpk` chunk numbers) back the `.gameevents` entries GameEventsEmitter
//   consumes, and map those to their manifest file names (game/csgo/pak01_<NNN>.vpk). Only those
//   chunks are fetched.
//
// Determinism: the returned file-name sets are sorted Ordinal and de-duplicated, so the on-disk
// write order and fetch plan are identical for the same pak01_dir.vpk input.
//
// Fail-loud: if the directory file isn't in the content manifest, or the parsed VPK contains zero
// `.gameevents` entries, the caller fails loud rather than producing a useless partial content tree
// — these helpers surface that via an empty result the caller asserts on.

using System.Globalization;

using Cs2SchemaTracker.Host.Vpk;

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>
/// One content pak the tracker reads: its depot-relative base directory (e.g. <c>game/csgo</c> or the
/// engine <c>game/core</c>) plus whether it is REQUIRED. Both paks live in the SAME content depot
/// (2347770) / manifest; a pak is just a different subtree of that depot. The csgo pak carries the
/// full content surface (gameevents + items + modes + localization + ...); the engine core pak carries
/// ONLY <c>resource/core.gameevents</c> (the 79 engine events the csgo pak does not ship). Every
/// depot-relative path, chunk name, and content-store subdir keys off <see cref="BaseRelDir"/>, so
/// adding a pak is a one-record change.
/// </summary>
internal sealed record ContentPak(string BaseRelDir, bool Required)
{
    /// <summary>The pak's directory file, depot-relative, forward-slash (e.g. game/core/pak01_dir.vpk).</summary>
    public string DirectoryFileRelPath => $"{BaseRelDir}/{ContentPakSelector.PakArchiveBaseName}_dir.vpk";

    /// <summary>The depot-relative manifest name for one external chunk (e.g. game/core/pak01_000.vpk).</summary>
    public string ChunkFileRelPath(ushort archiveIndex) =>
        $"{BaseRelDir}/{ContentPakSelector.PakArchiveBaseName}_{archiveIndex.ToString("D3", CultureInfo.InvariantCulture)}.vpk";

    /// <summary>True iff <paramref name="manifestFileName"/> is THIS pak's directory file (slash/case tolerant).</summary>
    public bool IsDirectoryFile(string manifestFileName) =>
        !string.IsNullOrEmpty(manifestFileName)
        && string.Equals(ContentPakSelector.Normalize(manifestFileName), DirectoryFileRelPath,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True iff <paramref name="manifestFileName"/> is any file of THIS pak's archive set (dir or chunk).</summary>
    public bool IsAnyPakFile(string manifestFileName) =>
        ContentPakSelector.IsAnyPakFileUnder(manifestFileName, BaseRelDir);

    /// <summary>The primary content pak: the full csgo content surface. Always required.</summary>
    public static readonly ContentPak Csgo = new("game/csgo", Required: true);

    /// <summary>
    /// The engine core pak — carries <c>resource/core.gameevents</c> (the engine game-event registry).
    /// NOT required: a build/era whose content manifest does not ship it is a graceful omission (the
    /// csgo events still emit), not a failure. UNVERIFIED depot path — see the header note on
    /// <see cref="ContentPakSelector"/>.
    /// </summary>
    public static readonly ContentPak Core = new("game/core", Required: false);

    /// <summary>Every content pak the tracker reads, csgo first (deterministic order).</summary>
    public static readonly IReadOnlyList<ContentPak> All = new[] { Csgo, Core };
}

/// <summary>
/// Pure helpers that decide which content-depot manifest files are needed for gameevents without
/// pulling the whole ~59 GB depot. No Steam, no I/O beyond reading an already-fetched pak01_dir.vpk
/// in <see cref="VpkArchive"/>.
///
/// NOTE (core pak): the engine <c>game/core/pak01_dir.vpk</c> path for <c>resource/core.gameevents</c>
/// is taken from the SchemaExplorer/GameTracking layout and is NOT yet confirmed against a live CS2
/// content-depot (2347770) manifest — the local content store is trimmed to the csgo subtree, and the
/// binary depot's <c>game/core</c> holds shader paks, not pak01. The core-pak wiring is ADDITIVE and
/// absence-tolerant: if the manifest does not ship this path the fetch/store/emit is a graceful no-op
/// and the csgo events emit unchanged. Confirm the path with a content-manifest probe before relying
/// on the 79 engine events; correct <see cref="ContentPak.Core"/>'s BaseRelDir if it differs.
/// </summary>
internal static class ContentPakSelector
{
    /// <summary>The pak01 directory file, depot-relative, forward-slash (CS2 game tree).</summary>
    public const string DirectoryFileRelPath = "game/csgo/pak01_dir.vpk";

    /// <summary>The base name of the pak01 archive set (game/csgo/pak01_*.vpk).</summary>
    public const string PakBaseRelDir = "game/csgo";
    public const string PakArchiveBaseName = "pak01";

    /// <summary>Sentinel archive index meaning "embedded in _dir.vpk" (no external chunk).</summary>
    public const ushort EmbeddedArchiveIndex = 0x7FFF;

    /// <summary>
    /// Phase A file filter: matches ONLY the pak01 directory file in the content
    /// manifest. Manifest file names may use either slash style and any case, so
    /// normalize both sides before comparing.
    /// </summary>
    public static bool IsDirectoryFile(string manifestFileName)
    {
        if (string.IsNullOrEmpty(manifestFileName))
        {
            return false;
        }
        return string.Equals(
            Normalize(manifestFileName), DirectoryFileRelPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Phase A as a predicate over manifest file names (the form
    /// <see cref="SteamAnonymousAcquirer"/> passes its file filter).
    /// </summary>
    public static Func<string, bool> DirectoryFilePredicate => IsDirectoryFile;

    /// <summary>The depot-relative VPK path backing economy item definitions.</summary>
    public const string ItemsGameRelPath = "scripts/items/items_game.txt";

    /// <summary>The depot-relative VPK path backing game modes (a loose top-level file).</summary>
    public const string GameModesRelPath = "gamemodes.txt";

    /// <summary>
    /// The depot-relative VPK path-prefix of the localization token tables. Localization is a
    /// FAMILY of files (resource/csgo_&lt;lang&gt;.txt, ~29 languages) rather than one file; every
    /// entry under this prefix matching csgo_&lt;lang&gt;.txt is selected.
    /// </summary>
    public const string LocalizationRelDir = "resource";
    private static readonly System.Text.RegularExpressions.Regex LocalizationFileRegex =
        new("^resource/csgo_[a-z]+\\.txt$",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The surface-property family (scripts/surfaceproperties_&lt;name&gt;.txt, KV3 text;
    /// _game/_footsteps/_impact_effects/_steamaudio). Several files, may span chunks.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SurfacePropertiesFileRegex =
        new("^scripts/surfaceproperties_[a-z_]+\\.txt$",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The prop-data sources (propdata.txt KV1 + collision_properties.txt KV3).</summary>
    public const string PropDataRelPath = "scripts/propdata.txt";
    public const string CollisionPropertiesRelPath = "scripts/collision_properties.txt";

    /// <summary>
    /// The map-overview family (resource/overviews/&lt;map&gt;.txt, KV1; one per map). Many files,
    /// typically spanning several chunks.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex MapOverviewFileRegex =
        new("^resource/overviews/[^/]+\\.txt$",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The BYTE-RANGE-SELECTIVE content fetch plan: for each external
    /// <c>pak01_&lt;NNN&gt;.vpk</c> that backs a resource the 7 content emitters read, the exact
    /// union of body byte ranges <c>[EntryOffset, EntryOffset+EntryLength)</c> of those resources —
    /// so the acquirer can fetch ONLY the depot-chunks overlapping them (a sparse pak01 file),
    /// shrinking the per-build content fetch from ~1.3 GB to ~tens of MB. This is the SINGLE live
    /// content selector; the resources covered are the SSOT in <see cref="EnumerateRequiredEntries"/>
    /// (every `.gameevents`, items_game.txt, gamemodes.txt, the resource/csgo_&lt;lang&gt;.txt token
    /// tables, the surfaceproperties_*.txt family, propdata.txt + collision_properties.txt, and the
    /// resource/overviews/*.txt family).
    ///
    /// The directory index (<c>pak01_dir.vpk</c>) is a WHOLE-file fetch (it is the VPK index and is
    /// already pulled in Phase A). Embedded resources (archive index 0x7FFF) ride in that index and
    /// add no external range. Zero-length bodies (resource lives entirely in the dir-tree preload)
    /// add no external range either.
    ///
    /// Returns an empty plan (<see cref="ContentFetchPlan.IsEmpty"/>) iff the archive has zero
    /// `.gameevents` entries — the caller fails loud. Per-file range lists are merged +
    /// Ordinal-by-offset sorted.
    ///
    /// TODO(steam-acquisition): the full CRC-deduped content-addressed backfill SCRIPT that fetches
    /// these content resources across many builds is a separate follow-up. This method only builds
    /// the per-build selective fetch plan; it does NOT drive any mass download.
    /// </summary>
    public static ContentFetchPlan SelectContentByteRanges(VpkArchive archive)
        => SelectContentByteRanges(archive, ContentPak.Csgo);

    /// <summary>
    /// <see cref="SelectContentByteRanges(VpkArchive)"/> for an explicit <paramref name="pak"/>: the
    /// directory file and backing chunk names are keyed off <paramref name="pak"/>'s base dir, so the
    /// same byte-range machinery serves the csgo pak (full content surface) and the engine core pak
    /// (core.gameevents only). The resource-matching in <see cref="EnumerateRequiredEntries"/> is
    /// base-dir-agnostic — the core pak simply contains only <c>.gameevents</c>, so it yields exactly
    /// those.
    /// </summary>
    public static ContentFetchPlan SelectContentByteRanges(VpkArchive archive, ContentPak pak)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(pak);

        var required = EnumerateRequiredEntries(archive);
        if (required.Count == 0)
        {
            return new ContentFetchPlan(
                Array.Empty<string>(),
                new Dictionary<string, IReadOnlyList<VpkByteRange>>(StringComparer.Ordinal));
        }

        // Group required body ranges by their backing external chunk file.
        var byFile = new Dictionary<string, List<VpkByteRange>>(StringComparer.Ordinal);
        foreach (var entry in required)
        {
            if (entry.ArchiveIndex == EmbeddedArchiveIndex || entry.EntryLength == 0)
            {
                // Embedded (rides in _dir.vpk) or entirely-preload ⇒ no external body range.
                continue;
            }
            var key = pak.ChunkFileRelPath(entry.ArchiveIndex);
            if (!byFile.TryGetValue(key, out var list))
            {
                list = new List<VpkByteRange>();
                byFile[key] = list;
            }
            list.Add(new VpkByteRange(entry.EntryOffset, entry.EntryLength));
        }

        var ranges = new Dictionary<string, IReadOnlyList<VpkByteRange>>(StringComparer.Ordinal);
        foreach (var kvp in byFile)
        {
            ranges[kvp.Key] = MergeRanges(kvp.Value);
        }

        return new ContentFetchPlan(
            new[] { pak.DirectoryFileRelPath },
            ranges);
    }

    /// <summary>
    /// Merge a set of body byte ranges into a minimal Ordinal-by-offset-sorted list with overlapping
    /// / adjacent ranges coalesced. Two resources in the same chunk file whose bodies abut or overlap
    /// collapse into one range, so the overlapping-chunk math downstream is done once per contiguous
    /// span.
    /// </summary>
    internal static IReadOnlyList<VpkByteRange> MergeRanges(IEnumerable<VpkByteRange> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sorted = input.OrderBy(r => r.Offset).ThenBy(r => r.End).ToList();
        var merged = new List<VpkByteRange>();
        foreach (var r in sorted)
        {
            if (merged.Count == 0)
            {
                merged.Add(r);
                continue;
            }
            var last = merged[^1];
            if (r.Offset <= last.End)
            {
                // Overlapping or adjacent: extend.
                long end = Math.Max(last.End, r.End);
                merged[^1] = new VpkByteRange(last.Offset, end - last.Offset);
            }
            else
            {
                merged.Add(r);
            }
        }
        return merged;
    }

    /// <summary>
    /// Enumerate the VPK directory entries of EXACTLY the resources the 7 content emitters consume:
    /// every `.gameevents`, scripts/items/items_game.txt, the loose gamemodes.txt, every
    /// resource/csgo_&lt;lang&gt;.txt, the scripts/surfaceproperties_*.txt family, scripts/propdata.txt
    /// + scripts/collision_properties.txt, and the resource/overviews/*.txt family. Returns the
    /// entries in the archive's deterministic (Ordinal-by-FullPath) order. Returns EMPTY when there is
    /// no `.gameevents` entry (the wrong VPK) — the gameevents fail-loud gate the byte-range selector
    /// enforces.
    ///
    /// This is the single source of truth for "which resources do we cover" (consumed by
    /// <see cref="SelectContentByteRanges"/> for the fetch plan AND by <see cref="VpkTrimWriter"/>
    /// for the content-store repack), and exactly matches each emitter's own resource-matching
    /// regex / Find call.
    /// </summary>
    internal static IReadOnlyList<VpkDirectoryEntry> EnumerateRequiredEntries(VpkArchive archive)
    {
        bool anyGameEvents = false;
        var result = new List<VpkDirectoryEntry>();
        foreach (var entry in archive.Entries)
        {
            bool isGameEvents = entry.FullPath.EndsWith(".gameevents", StringComparison.Ordinal);
            if (isGameEvents)
            {
                anyGameEvents = true;
            }
            if (isGameEvents
                || string.Equals(entry.FullPath, ItemsGameRelPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.FullPath, GameModesRelPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.FullPath, PropDataRelPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.FullPath, CollisionPropertiesRelPath, StringComparison.OrdinalIgnoreCase)
                || LocalizationFileRegex.IsMatch(entry.FullPath)
                || SurfacePropertiesFileRegex.IsMatch(entry.FullPath)
                || MapOverviewFileRegex.IsMatch(entry.FullPath))
            {
                result.Add(entry);
            }
        }

        // gameevents gate: no `.gameevents` ⇒ wrong VPK; surface the empty set so the caller fails loud.
        return anyGameEvents ? result : Array.Empty<VpkDirectoryEntry>();
    }

    /// <summary>
    /// The depot-relative manifest file name for a given external archive index,
    /// e.g. index 5 -> "game/csgo/pak01_005.vpk". Matches VpkArchive's own
    /// "&lt;base&gt;_&lt;NNN:D3&gt;.vpk" naming so the fetched files satisfy the parser.
    /// </summary>
    public static string ChunkFileRelPath(ushort archiveIndex) =>
        $"{PakBaseRelDir}/{PakArchiveBaseName}_{archiveIndex.ToString("D3", CultureInfo.InvariantCulture)}.vpk";

    /// <summary>
    /// Build the Phase B file-name predicate from the selected set. A manifest file
    /// is fetched iff its normalized name is in <paramref name="selected"/>.
    /// </summary>
    public static Func<string, bool> SelectedPredicate(IReadOnlyCollection<string> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var set = new HashSet<string>(selected.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        return name => set.Contains(Normalize(name));
    }

    /// <summary>
    /// The FALLBACK file filter: every file of the pak01 archive set
    /// (game/csgo/pak01_dir.vpk + game/csgo/pak01_NNN.vpk). Used when the
    /// two-phase minimal selection is not desired / not feasible. Still far
    /// smaller than the full content depot (everything outside pak01 is excluded).
    /// </summary>
    public static bool IsAnyPak01File(string manifestFileName)
        => IsAnyPakFileUnder(manifestFileName, PakBaseRelDir);

    /// <summary>
    /// True iff <paramref name="manifestFileName"/> is any file of the pak01 archive set under
    /// <paramref name="baseRelDir"/> (its <c>pak01_dir.vpk</c> or a <c>pak01_&lt;NNN&gt;.vpk</c>
    /// chunk). Parameterized on the base dir so it serves any <see cref="ContentPak"/> (game/csgo,
    /// game/core, ...).
    /// </summary>
    public static bool IsAnyPakFileUnder(string manifestFileName, string baseRelDir)
    {
        if (string.IsNullOrEmpty(manifestFileName))
        {
            return false;
        }
        var n = Normalize(manifestFileName);
        if (!n.StartsWith(baseRelDir + "/" + PakArchiveBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // <baseRelDir>/pak01_dir.vpk OR <baseRelDir>/pak01_<NNN>.vpk
        return n.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)
            && (n.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase) || HasChunkSuffix(n));
    }

    /// <summary>Predicate form of <see cref="IsAnyPak01File"/>.</summary>
    public static Func<string, bool> AnyPak01FilePredicate => IsAnyPak01File;

    private static bool HasChunkSuffix(string normalized)
    {
        // ..._NNN.vpk where NNN are digits.
        const string vpk = ".vpk";
        if (!normalized.EndsWith(vpk, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int end = normalized.Length - vpk.Length;          // index just past the last digit
        int i = end;
        while (i > 0 && char.IsDigit(normalized[i - 1]))
        {
            i--;
        }
        // require at least one digit and a preceding underscore
        return i < end && i > 0 && normalized[i - 1] == '_';
    }

    /// <summary>
    /// Normalize a manifest file name to forward-slash, no leading slash, for
    /// stable comparison. Does NOT lower-case (the caller's comparisons are
    /// OrdinalIgnoreCase) so the original name is preserved for display.
    /// </summary>
    internal static string Normalize(string name)
        => name.Replace('\\', '/').TrimStart('/');
}
