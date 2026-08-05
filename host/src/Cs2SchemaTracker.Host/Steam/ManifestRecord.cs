// Manifest-history persistence (independence-clean, deterministic).
//
// Every acquire (PICS-current OR explicit) writes a small, deterministic record of EXACTLY what it
// resolved: build_id, app_id, and per-depot (manifest_id, manifest_created_utc). This is how we
// accumulate our OWN re-fetchable manifest history over time — the source for later historical
// re-fetches, rather than relying on SteamDB (a third-party source). A
// `manifest-record.json` lands in the tuple output directory next to the binaries it describes.
//
// Format (canonical JSON via CanonicalJson — sorted keys, LF, UTF-8 no BOM):
//
//   {
//     "appId":   730,
//     "buildId": 23669931,
//     "depots": [
//       { "depotId": 2347770, "manifestCreatedUtc": "2026-06-10T...Z", "manifestId": "5146470907583764090" },
//       { "depotId": 2347771, "manifestCreatedUtc": "2026-06-10T...Z", "manifestId": "8287382081622299196" }
//     ]
//   }
//
// The depots array is ALWAYS sorted by depotId before serialization. manifestId is a uint64 carried
// as a JSON string (proto3 canonical-JSON convention; round-trips losslessly into a `--from-manifest`
// spec — the record we write is directly re-loadable to re-fetch).
//
// This record is NOT part of the public surface (it is an internal acquisition-history artifact, not
// a public consumer artifact), so it does not require a schema semver bump. The public
// provenance.json (the host serializer) remains the consumer-facing record of input-binary
// identity.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cs2SchemaTracker.Host.Serialization;

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>
/// A deterministic record of one acquire's resolved manifest identity. Both the
/// per-tuple <c>manifest-record.json</c> and the seedable known-build catalogue
/// use this shape.
/// </summary>
internal sealed record ManifestRecord(
    uint AppId,
    uint BuildId,
    IReadOnlyList<ManifestRecordDepot> Depots)
{
    /// <summary>Default file name written into the tuple output directory.</summary>
    public const string FileName = "manifest-record.json";

    public static ManifestRecord FromAcquireResult(
        uint buildId, IReadOnlyList<AcquiredDepotInfo> depots)
    {
        ArgumentNullException.ThrowIfNull(depots);
        if (depots.Count == 0)
        {
            throw new InvalidDataException(
                "cannot write a manifest record with zero depots.");
        }
        uint appId = depots[0].AppId;
        var recordDepots = depots
            .OrderBy(d => d.DepotId)
            .Select(d => new ManifestRecordDepot(
                DepotId: d.DepotId,
                ManifestId: d.ManifestId,
                ManifestCreatedUtc: d.ManifestCreatedUtc))
            .ToList();
        return new ManifestRecord(appId, buildId, recordDepots);
    }

    /// <summary>
    /// Read and parse a <c>manifest-record.json</c> from <paramref name="path"/>. Reuses the
    /// existing <see cref="ManifestRecordDocument"/> JSON shape (camelCase, uint64-as-string) —
    /// the exact inverse of <see cref="ToDocument"/> / <see cref="ToCanonicalJson"/>; this method
    /// does NOT hand-roll JSON.
    ///
    /// Fail-loud: a present-but-unparseable record (invalid JSON, a non-numeric manifestId, or a
    /// structurally empty document) throws — that IS a real input problem. Callers that treat
    /// ABSENCE as benign must check <see cref="File.Exists(string)"/> first; a missing file here
    /// throws <see cref="FileNotFoundException"/> like any other read.
    ///
    /// The returned record's depots are sorted by depotId, so the result is a pure function of the
    /// file contents regardless of on-disk array order.
    /// </summary>
    public static ManifestRecord ReadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string json = File.ReadAllText(path);

        ManifestRecordDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ManifestRecordDocument>(json);
        }
        catch (JsonException ex)
        {
            // Present-but-corrupt input: fail loud, never paper over.
            throw new InvalidDataException(
                $"manifest-record.json at '{path}' is not valid JSON: {ex.Message}", ex);
        }

        if (doc is null)
        {
            throw new InvalidDataException(
                $"manifest-record.json at '{path}' deserialized to null (empty/invalid document).");
        }
        if (doc.Depots is null || doc.Depots.Count == 0)
        {
            throw new InvalidDataException(
                $"manifest-record.json at '{path}' carries no depots.");
        }

        var depots = new List<ManifestRecordDepot>(doc.Depots.Count);
        foreach (ManifestRecordDepotEntry entry in doc.Depots)
        {
            if (entry is null)
            {
                throw new InvalidDataException(
                    $"manifest-record.json at '{path}' has a null depot entry.");
            }
            if (!ulong.TryParse(entry.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong manifestId))
            {
                throw new InvalidDataException(
                    $"manifest-record.json at '{path}' has a non-numeric manifestId " +
                    $"'{entry.ManifestId}' for depot {entry.DepotId}.");
            }
            depots.Add(new ManifestRecordDepot(
                DepotId: entry.DepotId,
                ManifestId: manifestId,
                ManifestCreatedUtc: entry.ManifestCreatedUtc ?? ""));
        }

        // Sort by depotId so the parsed record is deterministic regardless of on-disk order.
        depots.Sort((x, y) => x.DepotId.CompareTo(y.DepotId));

        return new ManifestRecord(doc.AppId, doc.BuildId, depots);
    }

    /// <summary>Write <c>manifest-record.json</c> into <paramref name="tupleDir"/>.</summary>
    public void WriteToTupleDir(string tupleDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(tupleDir);
        var path = Path.Combine(tupleDir, FileName);
        CanonicalJson.WriteFile(path, ToDocument());
    }

    /// <summary>
    /// Merge this record with another, unioning their depot lists by depotId. Where the
    /// SAME depotId appears in both, <paramref name="incoming"/> wins (it is the freshly-resolved
    /// entry). The result's depots are sorted by depotId, so the merge is a pure function of the
    /// union regardless of input order — merging A then B yields the byte-identical record as merging
    /// B then A when neither side overrides the other's depots. appId/buildId must agree across the
    /// two records (a mismatch is a real input problem and fails loud) — two different builds must
    /// never share one manifest-record.json.
    /// </summary>
    public ManifestRecord MergeWith(ManifestRecord incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (AppId != incoming.AppId)
        {
            throw new InvalidDataException(
                $"refusing to merge manifest records with mismatched appId " +
                $"({AppId} vs {incoming.AppId}).");
        }
        if (BuildId != incoming.BuildId)
        {
            throw new InvalidDataException(
                $"refusing to merge manifest records with mismatched buildId " +
                $"({BuildId} vs {incoming.BuildId}) — two builds must not share one manifest-record.json.");
        }

        // Union by depotId; incoming wins on collision.
        var byDepot = new Dictionary<uint, ManifestRecordDepot>();
        foreach (var d in Depots)
        {
            byDepot[d.DepotId] = d;
        }
        foreach (var d in incoming.Depots)
        {
            byDepot[d.DepotId] = d;
        }

        var merged = byDepot.Values.OrderBy(d => d.DepotId).ToList();

        // A merge that drops an existing depot is a bug (the union must be a superset of each side's
        // depotId set). Guard explicitly.
        foreach (var d in Depots)
        {
            if (!merged.Any(m => m.DepotId == d.DepotId))
            {
                throw new InvalidOperationException(
                    $"BUG: merge dropped pre-existing depot {d.DepotId}.");
            }
        }

        return new ManifestRecord(AppId, BuildId, merged);
    }

    /// <summary>
    /// Persist this record into <paramref name="tupleDir"/>, MERGING with any
    /// <c>manifest-record.json</c> already present there (by depotId; this record's depots
    /// win on collision) rather than clobbering it. This is the order-of-acquire-independent
    /// path: a binary acquire and a content acquire into the SAME dir each call this and
    /// converge to one record carrying BOTH depots.
    ///
    /// Fail-loud: a present-but-corrupt existing record surfaces via <see cref="ReadFromFile"/>
    /// (InvalidDataException) rather than being silently overwritten — that is a real input problem.
    ///
    /// The written record is a pure function of the union of depot entries, sorted by depotId.
    /// </summary>
    public void MergeIntoTupleDir(string tupleDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(tupleDir);
        var path = Path.Combine(tupleDir, FileName);

        ManifestRecord toWrite = this;
        if (File.Exists(path))
        {
            // ReadFromFile fails loud on a corrupt existing record. The existing record is the LEFT
            // side; this record (the fresh acquire) wins on any shared depotId via MergeWith's
            // incoming-wins rule.
            var existing = ReadFromFile(path);
            toWrite = existing.MergeWith(this);
        }

        CanonicalJson.WriteFile(path, toWrite.ToDocument());
    }

    /// <summary>Serialize to canonical JSON (sorted keys, LF, UTF-8 no BOM).</summary>
    public string ToCanonicalJson() => CanonicalJson.Serialize(ToDocument());

    /// <summary>
    /// Build a <see cref="ManifestSpec"/> from this record so a recorded build is
    /// directly re-fetchable via the explicit-manifest path. This closes the loop: acquire → record
    /// → re-acquire from record, all independence-clean.
    /// </summary>
    public ManifestSpec ToManifestSpec() =>
        new(AppId, BuildId,
            Depots.OrderBy(d => d.DepotId)
                  .Select(d => new ManifestSpecDepot(d.DepotId, d.ManifestId))
                  .ToList());

    private ManifestRecordDocument ToDocument() => new()
    {
        AppId = AppId,
        BuildId = BuildId,
        Depots = Depots
            .OrderBy(d => d.DepotId)
            .Select(d => new ManifestRecordDepotEntry
            {
                DepotId = d.DepotId,
                ManifestId = d.ManifestId.ToString(CultureInfo.InvariantCulture),
                ManifestCreatedUtc = d.ManifestCreatedUtc,
            })
            .ToList(),
    };

    // ---- JSON shape (camelCase, proto3-convention uint64-as-string) ----------

    internal sealed class ManifestRecordDocument
    {
        [JsonPropertyName("appId")]
        public uint AppId { get; set; }

        [JsonPropertyName("buildId")]
        public uint BuildId { get; set; }

        [JsonPropertyName("depots")]
        public List<ManifestRecordDepotEntry> Depots { get; set; } = new();
    }

    internal sealed class ManifestRecordDepotEntry
    {
        [JsonPropertyName("depotId")]
        public uint DepotId { get; set; }

        [JsonPropertyName("manifestId")]
        public string ManifestId { get; set; } = "";

        [JsonPropertyName("manifestCreatedUtc")]
        public string ManifestCreatedUtc { get; set; } = "";
    }
}

/// <summary>One depot's recorded manifest identity.</summary>
internal sealed record ManifestRecordDepot(
    uint DepotId,
    ulong ManifestId,
    string ManifestCreatedUtc);
