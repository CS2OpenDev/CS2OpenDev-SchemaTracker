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
// (content / tools / binaries[<platform>]); a present value is never rewritten.

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
        WriteCanonical(inventoryPath, root);
    }

    /// <summary>
    /// Merge MISSING forward-capture facts into an existing <c>builds[]</c> row: <c>content</c> and
    /// <c>tools</c> when the row lacks them, and each <paramref name="binaries"/> platform key the
    /// row's <c>binaries</c> map lacks. Present values are NEVER rewritten (a hand-curated GID always
    /// wins over a later forward-capture one). The row is rebuilt in the canonical key order and the
    /// file rewritten canonically iff something was added. Returns true when the file changed.
    /// Fail-loud when the build_id is absent (merge is for existing rows; append new ones).
    /// </summary>
    public static bool MergeBuildFacts(
        string inventoryPath, long buildId, string? content,
        IReadOnlyDictionary<string, string>? binaries, string? tools)
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
        if (binaries is { Count: > 0 })
        {
            var bin = row["binaries"] as JsonObject;
            var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (bin is not null)
            {
                foreach (var kv in bin)
                {
                    if (kv.Value is JsonValue v && v.TryGetValue<string>(out var gid))
                        merged[kv.Key] = gid;
                }
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
                foreach (var (plat, gid) in merged)
                    rebuiltBin[plat] = gid;
                row["binaries"] = rebuiltBin;
                changed = true;
            }
        }

        if (!changed)
        {
            return false;
        }

        builds[idx] = WithCanonicalKeyOrder(row);
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
        WriteCanonical(inventoryPath, root);
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
