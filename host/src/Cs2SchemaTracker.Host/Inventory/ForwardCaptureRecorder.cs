// Forward-capture inventory seam.
//
// When `extract --commit` promotes a NEVER-BEFORE-SEEN build into artifacts/, this records the build
// in data/cs2-assets-inventory.json so the inventory stays the single source of truth as new builds
// land. It derives the appendable facts from the freshly-promoted provenance.json (date_utc + the
// content/binary/tools depot GIDs) and the resolved era, then appends via InventoryWriter (lossless RMW).
//
// A build absent from builds[] is appended with the facts provenance carries; change_number and
// title are not in provenance, so they are left absent (a best-effort forward-capture row, not a
// fabricated one). A build already in the inventory has any MISSING facts merged in (the binaries
// GID of a platform that landed later, tools/content when absent); a present value is never
// rewritten, so a hand-curated row always wins. Non-fatal by contract when called from
// PromoteBuildLevel (surfaced loudly, never reverts the promote).

using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Walker;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Inventory;

/// <summary>Records a freshly-promoted build into the assets inventory (append or fact-merge).</summary>
internal static class ForwardCaptureRecorder
{
    /// <summary>The shared cross-platform content depot (gameevents / items / localization).</summary>
    private const uint ContentDepotId = 2347770;

    /// <summary>The Workshop Tools depot (windows-only editor DLLs; free DLC 2279721-gated).</summary>
    private const uint ToolsDepotId = SteamAppIdMap.Cs2WorkshopToolsDepotId;

    private static readonly JsonParser ProvenanceParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>The outcome of a forward-capture record attempt (for the caller's summary line).</summary>
    internal enum Outcome
    {
        /// <summary>The build was appended to the inventory's builds[].</summary>
        Appended,
        /// <summary>The build's existing row gained missing facts (e.g. a later platform's GID).</summary>
        Merged,
        /// <summary>The build was already fully recorded; nothing changed (idempotent).</summary>
        AlreadyPresent,
        /// <summary>The build id is not numeric / no committed provenance — nothing recorded.</summary>
        Skipped,
    }

    /// <summary>
    /// Record <paramref name="build"/> in the inventory at <paramref name="inventoryPath"/>: append a
    /// new row for a numeric build id absent from builds[], or merge MISSING facts (a later
    /// platform's binaries GID, absent tools/content) into an existing row without touching present
    /// values. Reads the committed artifacts/&lt;build&gt;/&lt;platform&gt;/provenance.json for
    /// date_utc + depot GIDs and resolves the era via <paramref name="resolver"/>. Fail-loud on a
    /// present-but-corrupt provenance or an unreadable inventory.
    /// </summary>
    public static Outcome RecordIfNew(
        string inventoryPath, string repoRoot, string build, string platform, EraWalkerResolver resolver)
    {
        ArgumentException.ThrowIfNullOrEmpty(inventoryPath);
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(build);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        ArgumentNullException.ThrowIfNull(resolver);

        if (!long.TryParse(build, System.Globalization.CultureInfo.InvariantCulture, out var buildId))
        {
            return Outcome.Skipped;   // 'latest' / a label is never an inventory build id.
        }

        var catalog = InventoryCatalog.LoadFromFile(inventoryPath);
        bool exists = catalog.FindBuild(buildId) is not null;

        var provPath = Path.Combine(repoRoot, "artifacts", build, platform, "provenance.json");
        if (!File.Exists(provPath))
        {
            // Nothing authoritative to record/merge from.
            return exists ? Outcome.AlreadyPresent : Outcome.Skipped;
        }

        Schemas.Provenance prov;
        try
        {
            prov = ProvenanceParser.Parse<Schemas.Provenance>(File.ReadAllText(provPath));
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            throw new InvalidDataException(
                $"provenance.json at '{provPath}' does not parse as Provenance: {ex.Message}", ex);
        }

        // The binary depot for this platform is derived from the inventory's depots[] (role=binary),
        // never hard-coded — so a future depot rotation flows through without a host edit.
        var assets = AssetsInventory.Load(inventoryPath);
        uint? binaryDepot = assets.HasBinaryDepotFor(platform) ? assets.BinaryDepotFor(platform) : null;

        // GID sourcing: this recorder reads the freshly-promoted provenance.json's steam.depots[]
        // (NOT the PICS appinfo capture — that sidecar is never parsed here); the "content" and
        // "binaries" GIDs have always been sourced this way, and "tools" mirrors them exactly. The
        // 2347779 entry is present in provenance iff the build's acquire ran the --tools leg (the
        // tools depot identity flows staging -> manifest-record.json -> provenance), so a build
        // captured without tools honestly records no `tools` GID rather than a fabricated one.
        string dateUtc = prov.Steam?.ManifestCreatedUtc ?? "";
        string? contentGid = null;
        string? toolsGid = null;
        var binaries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (prov.Steam is { } steam)
        {
            foreach (var d in steam.Depots)
            {
                if (string.IsNullOrEmpty(d.ManifestId))
                    continue;
                if (d.DepotId == ContentDepotId)
                {
                    contentGid ??= d.ManifestId;
                }
                else if (d.DepotId == ToolsDepotId)
                {
                    toolsGid ??= d.ManifestId;
                }
                else if (binaryDepot is { } bd && d.DepotId == bd)
                {
                    binaries[platform] = d.ManifestId;
                }
            }
        }

        if (exists)
        {
            bool changed = InventoryWriter.MergeBuildFacts(
                inventoryPath, buildId, contentGid,
                binaries.Count > 0 ? binaries : null, toolsGid);
            return changed ? Outcome.Merged : Outcome.AlreadyPresent;
        }

        string era = resolver.DetermineEraOnly(build, platform).Era;

        InventoryWriter.AppendBuild(inventoryPath, new InventoryBuildRecord(
            BuildId: buildId,
            DateUtc: dateUtc,
            Era: era,
            ChangeNumber: null,
            Title: null,
            Content: contentGid,
            Binaries: binaries.Count > 0 ? binaries : null,
            Tools: toolsGid));

        return Outcome.Appended;
    }
}
