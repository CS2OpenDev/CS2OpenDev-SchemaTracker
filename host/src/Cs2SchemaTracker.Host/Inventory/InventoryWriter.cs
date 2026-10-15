// Host read-modify-write for data/cs2-assets-inventory.json.
//
// Forward-capture appends a new builds[] entry when a never-before-seen build is committed. This
// writer edits the raw JsonNode tree so _meta / app / eras / depots and every existing build — plus
// any unknown field — survive VERBATIM; only builds[] gains the one new row. It replaces what the
// retired sync-inventory command / the Python ingest did.
//
// Output is canonical + deterministic, matching the on-disk shape the Python ingest wrote
// (json.dumps(obj, indent=2, ensure_ascii=False) + "\n"): 2-space indent, ": " after keys, LF
// endings, UTF-8 no BOM, single trailing newline, top-level order _meta,app,eras,depots,builds, and
// NO \uXXXX / HTML escaping of non-ASCII. The encoder choice (UnsafeRelaxedJsonEscaping) is
// load-bearing — STJ's default HTML-escapes ' < > + and non-ASCII, which would spray a spurious
// diff over every forward-capture write.
//
// Fail-loud: a missing/unparseable inventory, or an attempt to append a build_id that already
// exists, throws before any bytes are written. Appending is NEW-ONLY (never a silent overwrite);
// MergeBuildFacts is the one sanctioned edit of an existing row, and it only ADDS absent keys
// (content / tools / binaries[<platform>] / change_number / title); a present value is never
// rewritten.
//
// _meta.counts is a DERIVED summary of builds[] that every writing path refreshes, so it cannot
// drift from the rows it counts (it had: 376 builds recorded against 387 real ones by 2026-09-01,
// because the Python ingest that used to maintain it was retired and nothing took the job over).
// Only keys ALREADY PRESENT in the block are recomputed — a counts block is never invented, and an
// unknown key is left verbatim — so this stays a lossless RMW like the rest of the writer.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cs2SchemaTracker.Host.Inventory;

/// <summary>
/// One new build row to append to the inventory's <c>builds[]</c>. Key order in the emitted object
/// is fixed: <c>build_id, date_utc, change_number, title, era, content, binaries, tools</c>.
/// Optional fields (<see cref="ChangeNumber"/> / <see cref="Title"/> / <see cref="Content"/> /
/// <see cref="Binaries"/> / <see cref="Tools"/>) are omitted when null so a best-effort
/// forward-capture record stays honest rather than writing placeholder zeros/blanks.
/// </summary>
internal sealed record InventoryBuildRecord(
    long BuildId,
    string DateUtc,
    string Era,
    long? ChangeNumber = null,
    string? Title = null,
    string? Content = null,
    IReadOnlyDictionary<string, string>? Binaries = null,
    string? Tools = null);

/// <summary>
/// One OBSERVED fact: at build <paramref name="BuildId"/>, depot <paramref name="DepotId"/>'s
/// public-branch manifest was <paramref name="ManifestId"/>. This is a recording, never a
/// derivation -- every field comes from a PICS capture, or from an inventory row that itself came
/// from one.
/// </summary>
internal sealed record DepotManifestObservation(
    uint DepotId,
    long BuildId,
    long? ChangeNumber,
    string DateUtc,
    string ManifestId);

/// <summary>What a <see cref="InventoryWriter.MergeDepotHistories"/> call changed.</summary>
internal sealed record DepotHistoryMergeResult(
    int DepotsChanged,
    int EntriesAdded,
    int EntriesResolvedToBuilds)
{
    /// <summary>True when the merge found nothing to do (the inventory is already current).</summary>
    public bool NoChange => DepotsChanged == 0 && EntriesAdded == 0 && EntriesResolvedToBuilds == 0;
}

/// <summary>Lossless append-a-build writer for <c>data/cs2-assets-inventory.json</c>.</summary>
internal static class InventoryWriter
{
    /// <summary>
    /// Append <paramref name="record"/> to the inventory at <paramref name="inventoryPath"/>,
    /// preserving every other field, and rewrite the whole file in canonical form. Inserts the row
    /// keeping <c>builds[]</c> newest-build_id-first. Fail-loud when the file is missing/unparseable
    /// or already carries <paramref name="record"/>'s build_id (append is new-only).
    /// </summary>
    public static void AppendBuild(string inventoryPath, InventoryBuildRecord record)
    {
        ArgumentException.ThrowIfNullOrEmpty(inventoryPath);
        ArgumentNullException.ThrowIfNull(record);

        var root = LoadTree(inventoryPath);
        var builds = (root["builds"] as JsonArray)
            ?? throw new InvalidDataException($"assets inventory '{inventoryPath}' has no 'builds' array.");

        foreach (var n in builds)
        {
            if (n is JsonObject o && BuildIdOf(o) == record.BuildId)
            {
                throw new InvalidDataException(
                    $"assets inventory '{inventoryPath}' already carries build_id {record.BuildId}; " +
                    "AppendBuild is new-only (never a silent overwrite).");
            }
        }

        InsertBuildSorted(builds, NewBuildObject(record));
        EnsurePredecessors(builds);   // derive predecessor for the new row + any neighbour it displaced
        RefreshMetaCounts(root);
        WriteCanonical(inventoryPath, root);
    }

    /// <summary>
    /// Merge MISSING forward-capture facts into an existing <c>builds[]</c> row: <c>content</c> and
    /// <c>tools</c> when the row lacks them, each <paramref name="binaries"/> platform key the row's
    /// <c>binaries</c> map lacks, and <paramref name="changeNumber"/> / <paramref name="title"/> when
    /// absent. Present values are NEVER rewritten (a hand-curated GID — or a real SteamDB title —
    /// always wins over a later forward-capture one). The row is rebuilt in the canonical key order
    /// and the file rewritten canonically iff something was added. Returns true when the file
    /// changed. Fail-loud when the build_id is absent (merge is for existing rows; append new ones).
    /// </summary>
    public static bool MergeBuildFacts(
        string inventoryPath, long buildId, string? content,
        IReadOnlyDictionary<string, string>? binaries, string? tools,
        long? changeNumber = null, string? title = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(inventoryPath);

        var root = LoadTree(inventoryPath);
        var builds = (root["builds"] as JsonArray)
            ?? throw new InvalidDataException($"assets inventory '{inventoryPath}' has no 'builds' array.");

        int idx = -1;
        for (var i = 0; i < builds.Count; i++)
        {
            if (builds[i] is JsonObject o && BuildIdOf(o) == buildId)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
        {
            throw new InvalidDataException(
                $"assets inventory '{inventoryPath}' has no build_id {buildId}; " +
                "MergeBuildFacts only merges into an existing row (use AppendBuild for a new one).");
        }

        var row = (JsonObject)builds[idx]!;
        bool changed = false;

        if (content is not null && !row.ContainsKey("content"))
        {
            row["content"] = content;
            changed = true;
        }
        if (tools is not null && !row.ContainsKey("tools"))
        {
            row["tools"] = tools;
            changed = true;
        }
        if (changeNumber is not null && !row.ContainsKey("change_number"))
        {
            row["change_number"] = changeNumber.Value;
            changed = true;
        }
        if (title is not null && !row.ContainsKey("title"))
        {
            row["title"] = title;
            changed = true;
        }
        if (binaries is { Count: > 0 })
        {
            // Present entries are carried over VERBATIM whatever their node shape; only genuinely
            // absent platform keys are added. Rewriting or dropping an existing value here would
            // break the never-rewrite contract on a hand-curated row.
            var bin = row["binaries"] as JsonObject;
            var merged = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (bin is not null)
            {
                foreach (var kv in bin)
                    merged[kv.Key] = kv.Value?.DeepClone();
            }
            bool binariesChanged = false;
            foreach (var (plat, gid) in binaries)
            {
                if (!merged.ContainsKey(plat))
                {
                    merged[plat] = gid;
                    binariesChanged = true;
                }
            }
            if (binariesChanged)
            {
                var rebuiltBin = new JsonObject();
                foreach (var (plat, node) in merged)
                    rebuiltBin[plat] = node;
                row["binaries"] = rebuiltBin;
                changed = true;
            }
        }

        if (!changed)
        {
            return false;
        }

        builds[idx] = WithCanonicalKeyOrder(row);
        RefreshMetaCounts(root);
        WriteCanonical(inventoryPath, root);
        return true;
    }

    /// <summary>
    /// One-time / idempotent migration: set every <c>builds[]</c> entry's <c>predecessor</c> from its
    /// neighbours (the next-lower committed build_id; <c>null</c> for the in-scope floor) and rewrite
    /// the file in canonical form. Safe to re-run — it recomputes the same values. See
    /// <see cref="EnsurePredecessors"/> for the derivation.
    /// </summary>
    public static void BackfillPredecessors(string inventoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(inventoryPath);
        var root = LoadTree(inventoryPath);
        var builds = (root["builds"] as JsonArray)
            ?? throw new InvalidDataException($"assets inventory '{inventoryPath}' has no 'builds' array.");
        EnsurePredecessors(builds);
        RefreshMetaCounts(root);
        WriteCanonical(inventoryPath, root);
    }

    /// <summary>
    /// Merge observed depot manifests into <c>depots[].history</c>.
    /// <para>
    /// WHY THIS EXISTS: the histories went three months stale (newest entry build 24134959,
    /// 2026-07-09) because nothing in the host ever wrote them -- they were hand-extracted from
    /// SteamDB patch pages, and that manual path stopped. The contract <c>_meta.purpose</c> states
    /// ("any depot's manifest at any build resolves from depots[].history, newest entry whose
    /// change_number &lt;= the build's, or date_utc &lt;= the build date") makes a stale table
    /// quietly WRONG rather than merely incomplete: it answers with a months-old GID instead of
    /// failing.
    /// </para>
    /// <para>
    /// MERGE-ONLY, never replace. An existing entry is never dropped and a present manifest is
    /// never rewritten, so the hand-curated record always survives. The one edit this makes to an
    /// existing entry is to RESOLVE a build-less one (an entry carrying only date_utc +
    /// manifest_id) to the build whose recorded manifest matches it, which adds build_id and
    /// change_number and loses nothing.
    /// </para>
    /// <para>
    /// With <paramref name="rederiveFromBuilds"/>, the depots whose manifest every build already
    /// records in <c>builds[]</c> -- role <c>content</c> (builds[].content), <c>binary</c>
    /// (builds[].binaries[platform], the platform from the depot's own platforms[]) and
    /// <c>tools</c> (builds[].tools) -- are additionally re-derived across EVERY build, so those
    /// four get complete per-build coverage instead of whatever the observations happen to sample.
    /// Those are exactly the depots the acquire paths resolve. Every other depot can only ever be
    /// as dense as the captures passed in.
    /// </para>
    /// Returns what changed; writes nothing when nothing changed, and nothing at all under
    /// <paramref name="dryRun"/>.
    /// </summary>
    public static DepotHistoryMergeResult MergeDepotHistories(
        string inventoryPath,
        IReadOnlyList<DepotManifestObservation> observations,
        bool rederiveFromBuilds,
        bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(inventoryPath);
        ArgumentNullException.ThrowIfNull(observations);

        var root = LoadTree(inventoryPath);
        var result = MergeDepotHistoriesInto(root, observations, rederiveFromBuilds);
        if (!result.NoChange && !dryRun)
        {
            RefreshMetaCounts(root);
            WriteCanonical(inventoryPath, root);
        }
        return result;
    }

    /// <summary>One history entry, decoupled from its JSON node for sorting and collapsing.</summary>
    private sealed record HistoryEntry(long? BuildId, long? ChangeNumber, string DateUtc, string ManifestId);

    private static DepotHistoryMergeResult MergeDepotHistoriesInto(
        JsonObject root, IReadOnlyList<DepotManifestObservation> observations, bool rederiveFromBuilds)
    {
        var depots = (root["depots"] as JsonArray)
            ?? throw new InvalidDataException("assets inventory has no 'depots' array.");

        var byDepot = observations
            .GroupBy(o => o.DepotId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DepotManifestObservation>)g.ToList());

        // The inventory's OWN facts for a build win over whatever the observation carried. A PICS
        // capture's captured_utc is when WE captured it, which trails the build's manifest date by
        // hours; all 4186 existing entries use the BUILD date, and the resolution contract is an
        // ordered scan over those dates, so mixing the two clocks would misorder entries against
        // their neighbours. The observation's own values are the fallback for a build the
        // inventory does not list.
        var buildFacts = BuildFactIndex(root);

        int depotsChanged = 0, added = 0, resolved = 0;
        foreach (var node in depots)
        {
            if (node is not JsonObject depot)
                continue;
            var depotId = DepotIdOf(depot);
            if (depotId == 0)
                continue;

            var candidates = new List<HistoryEntry>();
            if (byDepot.TryGetValue(depotId, out var obs))
            {
                foreach (var o in obs)
                {
                    if (string.IsNullOrEmpty(o.ManifestId))
                        continue;
                    var date = o.DateUtc;
                    var change = o.ChangeNumber;
                    if (buildFacts.TryGetValue(o.BuildId, out var known))
                    {
                        date = known.DateUtc ?? date;
                        change = known.ChangeNumber ?? change;
                    }
                    if (!string.IsNullOrEmpty(date))
                        candidates.Add(new HistoryEntry(o.BuildId, change, date, o.ManifestId));
                }
            }
            if (rederiveFromBuilds)
                candidates.AddRange(DeriveFromBuilds(root, depot));

            if (candidates.Count == 0)
                continue;

            var existing = ReadHistory(depot["history"] as JsonArray);
            var (merged, addedHere, resolvedHere) = MergeOneHistory(existing, candidates);
            if (addedHere == 0 && resolvedHere == 0)
                continue;

            depot["history"] = WriteHistory(merged);
            depotsChanged++;
            added += addedHere;
            resolved += resolvedHere;
        }

        return new DepotHistoryMergeResult(depotsChanged, added, resolved);
    }

    /// <summary>
    /// build_id -> the date_utc / change_number that <c>builds[]</c> records for it. The inventory
    /// is the authority on its own builds; see the normalization note at the call site.
    /// </summary>
    private static Dictionary<long, (string? DateUtc, long? ChangeNumber)> BuildFactIndex(JsonObject root)
    {
        var index = new Dictionary<long, (string? DateUtc, long? ChangeNumber)>();
        foreach (var row in (root["builds"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var id = BuildIdOf(row);
            if (id != 0)
                index[id] = (StringOf(row, "date_utc"), LongOrNull(row, "change_number"));
        }
        return index;
    }

    /// <summary>
    /// The manifest sequence for a depot as every build already records it, one entry per CHANGE.
    /// Empty unless the depot's role maps onto a <c>builds[]</c> field (content / binary / tools).
    /// Walks builds OLDEST-first so the entry kept for a run of identical manifests is the build
    /// where that manifest first appeared, which is what the <c>&lt;=</c> resolution rule needs.
    /// <c>build_id</c> breaks a date_utc tie: several CS2 builds share a timestamp to the second,
    /// and ordering those by date alone is ambiguous (it picks a different representative run to
    /// run, which is how this was first caught).
    /// </summary>
    private static List<HistoryEntry> DeriveFromBuilds(JsonObject root, JsonObject depot)
    {
        var field = BuildsFieldFor(depot);
        if (field is null)
            return [];

        var ordered = ((root["builds"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(r => !string.IsNullOrEmpty(StringOf(r, "date_utc")))
            .OrderBy(r => StringOf(r, "date_utc"), StringComparer.Ordinal)
            .ThenBy(BuildIdOf)
            .ToList();

        var emitted = new List<HistoryEntry>();
        string? previous = null;
        foreach (var row in ordered)
        {
            var gid = field(row);
            if (string.IsNullOrEmpty(gid) || string.Equals(gid, previous, StringComparison.Ordinal))
                continue;
            emitted.Add(new HistoryEntry(
                BuildIdOf(row), LongOrNull(row, "change_number"), StringOf(row, "date_utc")!, gid));
            previous = gid;
        }
        return emitted;
    }

    /// <summary>
    /// The <c>builds[]</c> accessor for a depot, chosen by ROLE -- never a hard-coded depot id, so
    /// a depot rotation flows through from the data (the same rule
    /// <see cref="Steam.AssetsInventory"/> uses to find the acquire depots). A <c>binary</c> depot
    /// must name exactly one platform in its own platforms[]; anything else is ambiguous and gets
    /// no derivation rather than a guess.
    /// </summary>
    private static Func<JsonObject, string?>? BuildsFieldFor(JsonObject depot)
    {
        var role = StringOf(depot, "role");
        if (string.Equals(role, "content", StringComparison.Ordinal))
            return r => StringOf(r, "content");
        if (string.Equals(role, "tools", StringComparison.Ordinal))
            return r => StringOf(r, "tools");
        if (!string.Equals(role, "binary", StringComparison.Ordinal))
            return null;

        var platforms = ((depot["platforms"] as JsonArray) ?? [])
            .Select(p => p is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        if (platforms.Count != 1)
            return null;
        var platform = platforms[0]!;
        return r => r["binaries"] is JsonObject b ? StringOf(b, platform) : null;
    }

    /// <summary>
    /// Merge candidates into an existing history: resolve build-less entries, add what is genuinely
    /// new, order newest-first, then collapse a run of identical manifests to its OLDEST entry (a
    /// manifest that did not change needs no second entry -- the existing one still resolves).
    /// </summary>
    private static (List<HistoryEntry> Merged, int Added, int Resolved) MergeOneHistory(
        List<HistoryEntry> existing, List<HistoryEntry> candidates)
    {
        var firstByManifest = new Dictionary<string, HistoryEntry>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (!firstByManifest.ContainsKey(c.ManifestId))
                firstByManifest[c.ManifestId] = c;
        }

        var merged = new List<HistoryEntry>(existing.Count + candidates.Count);
        int resolved = 0;
        foreach (var e in existing)
        {
            // A build-less entry (hand-entered: date_utc + manifest_id only) that a recorded build
            // also carries takes that build's identity. Same manifest, strictly more provenance.
            if (e.BuildId is null && firstByManifest.TryGetValue(e.ManifestId, out var match))
            {
                merged.Add(match);
                resolved++;
            }
            else
            {
                merged.Add(e);
            }
        }

        var present = new HashSet<(long?, string)>(merged.Select(e => (e.BuildId, e.ManifestId)));
        foreach (var c in candidates)
        {
            if (present.Add((c.BuildId, c.ManifestId)))
                merged.Add(c);
        }

        var ordered = merged
            .OrderByDescending(e => e.DateUtc, StringComparer.Ordinal)
            .ThenByDescending(e => e.BuildId ?? 0)
            .ToList();

        var collapsed = new List<HistoryEntry>(ordered.Count);
        foreach (var e in ordered)
        {
            if (collapsed.Count > 0 &&
                string.Equals(collapsed[^1].ManifestId, e.ManifestId, StringComparison.Ordinal))
            {
                collapsed[^1] = e;   // keep the OLDER representative of the run
                continue;
            }
            collapsed.Add(e);
        }

        // Report the NET effect on the file, not the number of insertions: the collapse can absorb
        // a candidate, and the operator is about to read this number against a diff.
        return (collapsed, collapsed.Count - existing.Count, resolved);
    }

    private static List<HistoryEntry> ReadHistory(JsonArray? history)
    {
        var entries = new List<HistoryEntry>();
        if (history is null)
            return entries;
        foreach (var node in history)
        {
            if (node is not JsonObject o)
                continue;
            var manifest = StringOf(o, "manifest_id");
            var date = StringOf(o, "date_utc");
            if (string.IsNullOrEmpty(manifest) || string.IsNullOrEmpty(date))
                continue;
            var bid = o["build_id"] is null ? (long?)null : BuildIdOf(o);
            entries.Add(new HistoryEntry(
                bid is 0 ? null : bid, LongOrNull(o, "change_number"), date, manifest));
        }
        return entries;
    }

    /// <summary>
    /// Emit history entries in the on-disk shape: key order <c>build_id, change_number, date_utc,
    /// manifest_id</c>, with build_id/change_number OMITTED (not null) when unknown -- the shape
    /// the hand-entered build-less entries already use.
    /// </summary>
    private static JsonArray WriteHistory(List<HistoryEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var o = new JsonObject();
            if (e.BuildId is { } bid)
                o["build_id"] = bid;
            if (e.ChangeNumber is { } cn)
                o["change_number"] = cn;
            o["date_utc"] = e.DateUtc;
            o["manifest_id"] = e.ManifestId;
            arr.Add(o);
        }
        return arr;
    }

    private static string? StringOf(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? LongOrNull(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v)
            return null;
        if (v.TryGetValue<long>(out var l))
            return l;
        if (v.TryGetValue<string>(out var s) &&
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sl))
        {
            return sl;
        }
        return null;
    }

    private static uint DepotIdOf(JsonObject o)
    {
        if (o["depot_id"] is JsonValue v)
        {
            if (v.TryGetValue<uint>(out var u))
                return u;
            if (v.TryGetValue<string>(out var s) &&
                uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var su))
            {
                return su;
            }
        }
        return 0;
    }

    /// <summary>
    /// Recompute the derived tallies in <c>_meta.counts</c> from the tree that is about to be
    /// written. A no-op when the inventory has no <c>_meta.counts</c> block — this never INVENTS
    /// one — and it only rewrites keys that are already there, leaving any key it does not know how
    /// to derive verbatim. Idempotent: re-running on an up-to-date tree changes nothing.
    /// </summary>
    private static void RefreshMetaCounts(JsonObject root)
    {
        if (root["_meta"] is not JsonObject meta || meta["counts"] is not JsonObject counts)
        {
            return;
        }
        var builds = root["builds"] as JsonArray;
        var rows = builds?.OfType<JsonObject>().ToList() ?? [];

        int WithKey(string key) => rows.Count(r => r[key] is not null);

        var derived = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["builds"] = rows.Count,
            ["builds_with_binaries"] = WithKey("binaries"),
            ["builds_with_content"] = WithKey("content"),
            ["builds_with_change_number"] = WithKey("change_number"),
            ["builds_with_era"] = WithKey("era"),
            ["builds_with_title"] = WithKey("title"),
            ["builds_with_tools"] = WithKey("tools"),
            ["depots"] = (root["depots"] as JsonArray)?.Count ?? 0,
            ["eras"] = (root["eras"] as JsonArray)?.Count ?? 0,
        };

        foreach (var key in counts.Select(kv => kv.Key).ToList())
        {
            if (derived.TryGetValue(key, out var value))
            {
                counts[key] = value;
            }
        }
    }

    private static void WriteCanonical(string inventoryPath, JsonNode root)
        => File.WriteAllText(
            inventoryPath, CanonicalSerialize(root),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>Parse the inventory file into a JsonNode tree (fail-loud on missing/invalid).</summary>
    public static JsonObject LoadTree(string inventoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(inventoryPath);
        if (!File.Exists(inventoryPath))
        {
            throw new InvalidDataException($"assets inventory '{inventoryPath}' does not exist.");
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(inventoryPath));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"assets inventory '{inventoryPath}' is not valid JSON: {ex.Message}", ex);
        }
        return (root as JsonObject)
            ?? throw new InvalidDataException($"assets inventory '{inventoryPath}' root must be a JSON object.");
    }

    /// <summary>
    /// Re-serialize the (possibly-mutated) inventory tree to the canonical Python-ingest form:
    /// 2-space indent, LF endings, UTF-8 no BOM, single trailing newline, and Unsafe-relaxed
    /// escaping (matches <c>ensure_ascii=False</c> — escapes only <c>"</c> <c>\</c> and control
    /// chars, leaving <c>' &lt; &gt; +</c> and all non-ASCII verbatim). Keys are NOT re-sorted
    /// (insertion order is meaningful: _meta, app, eras, depots, builds).
    /// </summary>
    public static string CanonicalSerialize(JsonNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var raw = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var lf = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!lf.EndsWith('\n'))
            lf += "\n";
        return lf;
    }

    private static JsonObject NewBuildObject(InventoryBuildRecord r)
    {
        // Fixed key order: build_id, predecessor, date_utc, change_number, title, era, content,
        // binaries, tools. `predecessor` is a placeholder here (null) and is set by
        // EnsurePredecessors once the row's sorted position (hence its neighbours) is known.
        var obj = new JsonObject
        {
            ["build_id"] = r.BuildId,
            ["predecessor"] = null,
            ["date_utc"] = r.DateUtc,
        };
        if (r.ChangeNumber is not null)
            obj["change_number"] = r.ChangeNumber.Value;
        if (r.Title is not null)
            obj["title"] = r.Title;
        obj["era"] = r.Era;
        if (r.Content is not null)
            obj["content"] = r.Content;
        if (r.Binaries is { Count: > 0 })
        {
            var bin = new JsonObject();
            foreach (var (plat, gid) in r.Binaries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                bin[plat] = gid;
            }
            obj["binaries"] = bin;
        }
        // The Workshop Tools (2347779) GID — a single string like "content" (the tools depot is
        // windows-only, so there is no per-platform map). Matches the hand-curated rows' key order.
        if (r.Tools is not null)
            obj["tools"] = r.Tools;
        return obj;
    }

    private static void InsertBuildSorted(JsonArray builds, JsonObject rec)
    {
        long id = BuildIdOf(rec);
        int idx = 0;
        for (; idx < builds.Count; idx++)
        {
            if (builds[idx] is JsonObject o && BuildIdOf(o) < id)
            {
                break;
            }
        }
        builds.Insert(idx, rec);
    }

    /// <summary>
    /// Derive every build's <c>predecessor</c> from its neighbours. <c>builds[]</c> is newest-first,
    /// so a row's predecessor (the next-lower committed build) is the NEXT entry's build_id; the last
    /// entry (the in-scope floor) gets <c>null</c>. Each row is rebuilt so <c>predecessor</c> sits
    /// right after <c>build_id</c> (adding the key to legacy rows that lack it) with all other fields
    /// preserved verbatim in order. Idempotent: re-running yields the same tree.
    /// </summary>
    private static void EnsurePredecessors(JsonArray builds)
    {
        for (var i = 0; i < builds.Count; i++)
        {
            if (builds[i] is not JsonObject build)
                continue;
            JsonNode? predecessor = i + 1 < builds.Count && builds[i + 1] is JsonObject lower
                ? JsonValue.Create(BuildIdOf(lower))
                : null;   // floor row -> explicit JSON null
            builds[i] = WithPredecessor(build, predecessor);
        }
    }

    /// <summary>
    /// Rebuild <paramref name="build"/> with key order <c>build_id, predecessor, &lt;rest…&gt;</c>,
    /// deep-cloning every value so the result is detached from the source tree. Any existing
    /// <c>predecessor</c>/<c>build_id</c> keys are dropped and re-added in the fixed positions.
    /// </summary>
    private static JsonObject WithPredecessor(JsonObject build, JsonNode? predecessor)
    {
        var rebuilt = new JsonObject
        {
            ["build_id"] = build["build_id"]?.DeepClone(),
            ["predecessor"] = predecessor,
        };
        foreach (var kv in build)
        {
            if (kv.Key is "build_id" or "predecessor")
                continue;
            rebuilt[kv.Key] = kv.Value?.DeepClone();
        }
        return rebuilt;
    }

    /// <summary>
    /// Rebuild a builds[] row in the canonical key order (<c>build_id, predecessor, date_utc,
    /// change_number, title, era, content, binaries, tools</c>, then any unknown keys in their
    /// original order), deep-cloning every value. Keys the row lacks stay absent.
    /// </summary>
    private static JsonObject WithCanonicalKeyOrder(JsonObject build)
    {
        string[] known =
        {
            "build_id", "predecessor", "date_utc", "change_number", "title",
            "era", "content", "binaries", "tools",
        };
        var rebuilt = new JsonObject();
        foreach (var k in known)
        {
            if (build.ContainsKey(k))
                rebuilt[k] = build[k]?.DeepClone();
        }
        foreach (var kv in build)
        {
            if (!known.Contains(kv.Key, StringComparer.Ordinal))
                rebuilt[kv.Key] = kv.Value?.DeepClone();
        }
        return rebuilt;
    }

    private static long BuildIdOf(JsonObject o)
    {
        if (o["build_id"] is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l))
                return l;
            if (v.TryGetValue<string>(out var s) &&
                long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sl))
            {
                return sl;
            }
        }
        return 0;
    }
}
