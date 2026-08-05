// PICS appinfo canonical rendering — shared between the diagnostic (dump-appinfo)
// and the committed pics-appinfo.json capture/emit path.
//
// The PICS appinfo body is Valve's ARBITRARY nested KeyValues (VDF) tree with no fixed
// schema. We render it to ONE canonical-JSON string: a node with children -> a JSON
// object (child name -> value); a leaf -> its string value. Keeping every leaf as a
// string mirrors VDF (all values are strings) and preserves uint64 manifest GIDs without
// floating-point precision loss; CanonicalJson then sorts keys, forces LF, strips BOM, so
// re-capturing the SAME PICS response yields a BYTE-IDENTICAL body. This is the
// single chokepoint both DumpAppInfoCommand and PicsAppInfoCapture render through.

using System.Text.Json.Nodes;

using Cs2SchemaTracker.Host.Serialization;

using SteamKit2;

namespace Cs2SchemaTracker.Host.Steam;

internal static class PicsAppInfoRenderer
{
    /// <summary>
    /// Convert a SteamKit2 <see cref="KeyValue"/> tree to a JSON node: a node with children
    /// becomes a JSON object (child name -> value); a leaf becomes its string value. Within
    /// appinfo, child keys are unique per node; a duplicate would be last-wins.
    /// </summary>
    public static JsonNode? KvToJson(KeyValue kv)
    {
        ArgumentNullException.ThrowIfNull(kv);
        if (kv.Children is { Count: > 0 })
        {
            var obj = new JsonObject();
            foreach (var c in kv.Children)
            {
                obj[c.Name ?? ""] = KvToJson(c);
            }
            return obj;
        }
        return JsonValue.Create(kv.Value);
    }

    /// <summary>
    /// Render the appinfo KeyValues tree to the VERBATIM canonical-JSON body that lands in
    /// <c>pics-appinfo.json</c>'s <c>appinfo_json</c> (sorted keys, LF, UTF-8 no BOM). This is
    /// ONLY the appinfo body — no framing/_meta — because the committed artifact carries the
    /// framing fields (build_id, change_number, captured_utc, ...) in the surrounding proto
    /// message, not inside the opaque body.
    /// </summary>
    public static string RenderCanonicalBody(KeyValue appinfo)
    {
        ArgumentNullException.ThrowIfNull(appinfo);
        JsonNode? node = KvToJson(appinfo);
        // CanonicalJson.SerializeRawJson sorts keys recursively + normalizes whitespace/encoding.
        return CanonicalJson.SerializeRawJson(node?.ToJsonString() ?? "null");
    }
}
