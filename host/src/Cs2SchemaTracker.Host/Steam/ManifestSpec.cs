// groundwork — explicit-manifest acquisition spec.
//
// Anonymous PICS only ever exposes the public branch's CURRENT manifest per
// depot. To re-fetch a SPECIFIC PRIOR build, the caller must supply the
// per-depot manifest GIDs out-of-band — from OUR OWN recorded history (see
// ManifestRecord), which is -clean, since the GIDs come from a build we
// previously acquired and recorded ourselves.
//
// The spec is a small JSON file (see `--from-manifest <file>` on AcquireCommand):
//
//   {
//     "buildId":   23669931,
//     "appId":     730,
//     "depots": [
//       { "depotId": 2347770, "manifestId": "5146470907583764090" },
//       { "depotId": 2347771, "manifestId": "8287382081622299196" }
//     ]
//   }
//
// manifestId is a uint64 and is carried as a JSON STRING (proto3 canonical-JSON
// convention for 64-bit ints; also avoids any 2^53 double-rounding hazard).
// depotId / buildId / appId are 32-bit and carried as JSON numbers.
//
// Parsing is fail-loud: any malformed / missing / out-of-range field,
// a duplicate depot, or an empty depot list throws before any Steam contact.

using System.Globalization;
using System.Text.Json;

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>One explicit (depotId -> manifestId) pairing from a manifest spec.</summary>
internal sealed record ManifestSpecDepot(uint DepotId, ulong ManifestId);

/// <summary>
/// A parsed explicit-manifest acquisition request: which app + build, and the
/// exact per-depot manifest GIDs to fetch (bypassing PICS-current resolution).
/// </summary>
internal sealed record ManifestSpec(
    uint AppId,
    uint BuildId,
    IReadOnlyList<ManifestSpecDepot> Depots)
{
    /// <summary>Depots in stable depot-ID order — the acquire iteration order.</summary>
    public IReadOnlyList<ManifestSpecDepot> OrderedDepots =>
        Depots.OrderBy(d => d.DepotId).ToList();

    /// <summary>Depot IDs in stable order.</summary>
    public IReadOnlyList<uint> OrderedDepotIds =>
        Depots.Select(d => d.DepotId).OrderBy(d => d).ToList();

    /// <summary>
    /// Parse a manifest-spec JSON file. Fail-loud on any structural or
    /// value error — throws <see cref="InvalidDataException"/> with a specific
    /// message and writes nothing. Does NOT contact Steam.
    /// </summary>
    public static ManifestSpec ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new InvalidDataException(
                $"--from-manifest spec file '{path}' does not exist.");
        }
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"--from-manifest spec file '{path}' could not be read: {ex.Message}", ex);
        }
        return Parse(json, path);
    }

    /// <summary>
    /// Parse a manifest-spec from a JSON string. <paramref name="source"/> is
    /// only used to make error messages point back at the file path.
    /// </summary>
    public static ManifestSpec Parse(string json, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"manifest spec '{source}' is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"manifest spec '{source}' root must be a JSON object.");
            }

            var appId = ReadUInt32(root, "appId", source);
            var buildId = ReadUInt32(root, "buildId", source);

            if (!root.TryGetProperty("depots", out var depotsEl) ||
                depotsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"manifest spec '{source}' must have a 'depots' array.");
            }

            var seen = new HashSet<uint>();
            var depots = new List<ManifestSpecDepot>();
            int index = 0;
            foreach (var depotEl in depotsEl.EnumerateArray())
            {
                if (depotEl.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"manifest spec '{source}' depots[{index}] must be an object.");
                }
                var depotId = ReadUInt32(depotEl, "depotId", $"{source} depots[{index}]");
                var manifestId = ReadUInt64String(depotEl, "manifestId", $"{source} depots[{index}]");
                if (!seen.Add(depotId))
                {
                    throw new InvalidDataException(
                        $"manifest spec '{source}' lists depot {depotId} more than once.");
                }
                depots.Add(new ManifestSpecDepot(depotId, manifestId));
                index++;
            }

            if (depots.Count == 0)
            {
                throw new InvalidDataException(
                    $"manifest spec '{source}' has an empty 'depots' array.");
            }

            return new ManifestSpec(appId, buildId, depots);
        }
    }

    private static uint ReadUInt32(JsonElement obj, string name, string source)
    {
        if (!obj.TryGetProperty(name, out var el))
        {
            throw new InvalidDataException(
                $"manifest spec '{source}' is missing required field '{name}'.");
        }
        // Accept a JSON number or a numeric string for robustness.
        if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out var n))
        {
            return n;
        }
        if (el.ValueKind == JsonValueKind.String &&
            uint.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sn))
        {
            return sn;
        }
        throw new InvalidDataException(
            $"manifest spec '{source}' field '{name}' must be a uint32 (got {el.ValueKind}).");
    }

    private static ulong ReadUInt64String(JsonElement obj, string name, string source)
    {
        if (!obj.TryGetProperty(name, out var el))
        {
            throw new InvalidDataException(
                $"manifest spec '{source}' is missing required field '{name}'.");
        }
        // manifestId is canonically a JSON string (proto3 uint64 convention), but
        // accept a number too so hand-written specs aren't rejected over a quirk.
        if (el.ValueKind == JsonValueKind.String &&
            ulong.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv))
        {
            return sv;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out var nv))
        {
            return nv;
        }
        throw new InvalidDataException(
            $"manifest spec '{source}' field '{name}' must be a uint64 manifest GID " +
            $"(string preferred; got {el.ValueKind}).");
    }
}
