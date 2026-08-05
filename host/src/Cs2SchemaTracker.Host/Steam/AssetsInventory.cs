// the host-side reader for data/cs2-assets-inventory.json.
//
// This is the host-native replacement for the inventory parse that used to live
// in scripts/backfill-acquire.ps1. The backfill loop (every (build, platform) in
// the inventory, acquired by its recorded binary manifest GID) is now a FIRST-CLASS
// `acquire` batch-selection mode (`--all` / repeatable `--build`), so the inventory
// parse must live in the host too.
//
// The inventory shape (see the file's _meta):
//   {
//     "app":    { "app_id": 730, ... },
//     "depots": [ { "depot_id": 2347771, "role": "binary",
//                   "platforms": ["windows-x86_64"], "history": [...] }, ... ],
//     "builds": [ { "build_id": 23669931,
//                   "binaries": { "windows-x86_64": "<GID>", "linux-x86_64": "<GID>" },
//                   "content":  "<GID>", "tools": "<GID>", ... }, ... ]
//   }
//
// The per-platform BINARY depot is derived from depots[] (role == "binary",
// platforms[] lists the OS) — never hard-coded here — so a future depot rotation
// in the inventory flows through without a host edit. The per-build per-platform
// manifest GID is builds[].binaries[platform].
//
// Parsing is fail-loud: a missing file, malformed JSON, a non-730 app id,
// or a structurally-wrong document throws InvalidDataException before any Steam
// contact. Builds with no `binaries` block, or a platform a build does not list,
// are simply absent from the selection (not an error — many inventory builds have
// no recorded binary manifest yet).

using System.Globalization;
using System.Text.Json;

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>One (build, platform) the inventory can acquire by its recorded binary GID.</summary>
internal sealed record InventoryBinaryTarget(uint BuildId, string Platform, uint BinaryDepotId, ulong ManifestId)
{
    /// <summary>The in-memory explicit-manifest spec for this target (app 730 + the one binary depot).</summary>
    public ManifestSpec ToManifestSpec(uint appId) =>
        new(appId, BuildId, new[] { new ManifestSpecDepot(BinaryDepotId, ManifestId) });
}

/// <summary>
/// A build's recorded CONTENT manifest (shared content depot 2347770 — cross-platform), used by
/// the UNIFIED batch acquire to co-locate the selective content pak with the binaries. The content
/// depot is platform-independent, so this is keyed by build alone.
/// </summary>
internal sealed record InventoryContentTarget(uint BuildId, uint ContentDepotId, ulong ManifestId)
{
    /// <summary>The in-memory explicit-manifest spec for this target (app 730 + the one content depot).</summary>
    public ManifestSpec ToManifestSpec(uint appId) =>
        new(appId, BuildId, new[] { new ManifestSpecDepot(ContentDepotId, ManifestId) });
}

/// <summary>
/// A build's recorded WORKSHOP TOOLS manifest (tools depot 2347779 — windows-only), used by the
/// `--tools` acquire to co-locate the tools DLL slice with the windows binaries. The tools depot
/// ships windows editor binaries only, so this is keyed by build alone (there is no per-platform
/// dimension — the one target always merges into the windows-x86_64 tuple dir).
/// </summary>
internal sealed record InventoryToolsTarget(uint BuildId, uint ToolsDepotId, ulong ManifestId)
{
    /// <summary>The in-memory explicit-manifest spec for this target (app 730 + the one tools depot).</summary>
    public ManifestSpec ToManifestSpec(uint appId) =>
        new(appId, BuildId, new[] { new ManifestSpecDepot(ToolsDepotId, ManifestId) });
}

/// <summary>
/// Parsed view of data/cs2-assets-inventory.json sufficient to drive the `acquire`
/// batch mode: the app id, the platform -> binary-depot map (from depots[]), and the
/// per-build per-platform binary manifest GIDs (from builds[].binaries).
/// </summary>
internal sealed class AssetsInventory
{
    /// <summary>Default repo-relative path to the inventory file.</summary>
    public const string DefaultRelativePath = "data/cs2-assets-inventory.json";

    public uint AppId { get; }

    /// <summary>platform name -> binary depot id (from depots[] role=="binary").</summary>
    private readonly IReadOnlyDictionary<string, uint> _platformBinaryDepot;

    /// <summary>The shared content depot id (depots[] role=="content"), or null if the inventory has none.</summary>
    private readonly uint? _contentDepotId;

    /// <summary>build_id -> content manifest GID, from builds[].content (absent for builds with no content GID).</summary>
    private readonly IReadOnlyDictionary<uint, ulong> _contentGidByBuild;

    /// <summary>The Workshop Tools depot id (depots[] role=="tools", windows-only), or null if the inventory has none.</summary>
    private readonly uint? _toolsDepotId;

    /// <summary>build_id -> tools manifest GID, from builds[].tools (absent for builds with no tools GID).</summary>
    private readonly IReadOnlyDictionary<uint, ulong> _toolsGidByBuild;

    /// <summary>build_id -> (platform -> manifest GID), from builds[].binaries.</summary>
    private readonly IReadOnlyList<InventoryBuild> _builds;

    /// <summary>EVERY build_id that appears in builds[] (regardless of binaries/content presence).</summary>
    private readonly IReadOnlySet<uint> _allBuildIds;

    /// <summary>build_id -> its declared predecessor build_id (builds[].predecessor). Absent = floor / no predecessor.</summary>
    private readonly IReadOnlyDictionary<uint, uint> _predecessorByBuild;

    private sealed record InventoryBuild(uint BuildId, IReadOnlyDictionary<string, ulong> BinariesByPlatform);

    private AssetsInventory(
        uint appId,
        IReadOnlyDictionary<string, uint> platformBinaryDepot,
        uint? contentDepotId,
        IReadOnlyDictionary<uint, ulong> contentGidByBuild,
        uint? toolsDepotId,
        IReadOnlyDictionary<uint, ulong> toolsGidByBuild,
        IReadOnlyList<InventoryBuild> builds,
        IReadOnlySet<uint> allBuildIds,
        IReadOnlyDictionary<uint, uint> predecessorByBuild)
    {
        AppId = appId;
        _platformBinaryDepot = platformBinaryDepot;
        _contentDepotId = contentDepotId;
        _contentGidByBuild = contentGidByBuild;
        _toolsDepotId = toolsDepotId;
        _toolsGidByBuild = toolsGidByBuild;
        _builds = builds;
        _allBuildIds = allBuildIds;
        _predecessorByBuild = predecessorByBuild;
    }

    /// <summary>
    /// The inventory-declared predecessor of <paramref name="buildId"/> (builds[].predecessor), or
    /// null when the build is the in-scope floor (predecessor null) or is not in the inventory. This
    /// is the platform-AGNOSTIC scope chain; the evolution walk uses the on-disk numeric rule
    /// (<see cref="Changelog.ChangelogPredecessor"/>), and the drift check asserts the two agree.
    /// </summary>
    public uint? PredecessorOf(uint buildId)
        => _predecessorByBuild.TryGetValue(buildId, out var p) ? p : null;

    /// <summary>
    /// True iff <paramref name="buildId"/> appears in builds[] at all — regardless of whether it
    /// carries a binaries block or a content GID. (Contrast <see cref="ContainsBuild"/>, which is
    /// scoped to builds WITH a binary manifest.) Used by <c>reconcile-content-gids</c> to fail loud
    /// on a store build the inventory does not know about.
    /// </summary>
    public bool HasBuild(uint buildId) => _allBuildIds.Contains(buildId);

    /// <summary>
    /// The AUTHORITATIVE content (2347770) manifest GID for a build from builds[].content, or null
    /// when the inventory records no content GID for it. Distinct from <see cref="HasBuild"/>: a build
    /// can be present with no content GID (the ~8 builds with no recorded content).
    /// </summary>
    public ulong? ContentGidFor(uint buildId)
        => _contentGidByBuild.TryGetValue(buildId, out var gid) ? gid : null;

    /// <summary>True iff the inventory derived a shared content depot (depots[] role=="content").</summary>
    public bool HasContentDepot => _contentDepotId.HasValue;

    /// <summary>
    /// The content acquisition target for a build, or null if the inventory records no content GID
    /// for it (or has no content depot). Used by the UNIFIED batch acquire to co-locate the content
    /// pak with the binaries. A null is a per-build skip-of-record (content omitted), not a hard failure.
    /// </summary>
    public InventoryContentTarget? ContentTargetFor(uint buildId)
    {
        if (!_contentDepotId.HasValue)
            return null;
        if (!_contentGidByBuild.TryGetValue(buildId, out var gid))
            return null;
        return new InventoryContentTarget(buildId, _contentDepotId.Value, gid);
    }

    /// <summary>True iff the inventory derived a Workshop Tools depot (depots[] role=="tools").</summary>
    public bool HasToolsDepot => _toolsDepotId.HasValue;

    /// <summary>
    /// The Workshop Tools acquisition target for a build, or null if the inventory records no tools
    /// GID for it (or has no tools depot). Used by the `--tools` batch acquire to co-locate the tools
    /// DLL slice with the windows binaries. A null is a per-build skip-of-record (tools omitted, e.g.
    /// the pre-tools-tracking builds), not a hard failure.
    /// </summary>
    public InventoryToolsTarget? ToolsTargetFor(uint buildId)
    {
        if (!_toolsDepotId.HasValue)
            return null;
        if (!_toolsGidByBuild.TryGetValue(buildId, out var gid))
            return null;
        return new InventoryToolsTarget(buildId, _toolsDepotId.Value, gid);
    }

    /// <summary>True iff the inventory derived a binary depot for <paramref name="platform"/>.</summary>
    public bool HasBinaryDepotFor(string platform) => _platformBinaryDepot.ContainsKey(platform);

    /// <summary>The binary depot id for a platform, or throw (caller validates platform first).</summary>
    public uint BinaryDepotFor(string platform) => _platformBinaryDepot[platform];

    /// <summary>Build ids that list a binary manifest for <paramref name="platform"/>, ascending.</summary>
    public IReadOnlyList<uint> BuildsWithBinaryFor(string platform) =>
        _builds.Where(b => b.BinariesByPlatform.ContainsKey(platform))
               .Select(b => b.BuildId)
               .OrderBy(id => id)
               .ToList();

    /// <summary>
    /// The acquisition target for one (build, platform), or null if that build does
    /// not list a binary manifest for that platform in the inventory. The caller
    /// reports null as a per-build skip-of-record (it is not a hard failure).
    /// </summary>
    public InventoryBinaryTarget? TargetFor(uint buildId, string platform)
    {
        if (!_platformBinaryDepot.TryGetValue(platform, out var depot))
            return null;
        var build = _builds.FirstOrDefault(b => b.BuildId == buildId);
        if (build is null)
            return null;
        if (!build.BinariesByPlatform.TryGetValue(platform, out var gid))
            return null;
        return new InventoryBinaryTarget(buildId, platform, depot, gid);
    }

    /// <summary>True iff <paramref name="buildId"/> appears in the inventory at all.</summary>
    public bool ContainsBuild(uint buildId) => _builds.Any(b => b.BuildId == buildId);

    /// <summary>
    /// Load the inventory from <paramref name="path"/> (absolute or cwd-relative).
    /// Fail-loud: a missing/unreadable file, malformed JSON, or a structurally
    /// wrong document throws <see cref="InvalidDataException"/> before any Steam contact.
    /// </summary>
    public static AssetsInventory Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new InvalidDataException(
                $"assets inventory '{path}' does not exist. The batch mode needs the " +
                "data/cs2-assets-inventory.json that records each build's per-platform binary manifest GID.");
        }
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"assets inventory '{path}' could not be read: {ex.Message}", ex);
        }
        return Parse(json, path);
    }

    /// <summary>Parse the inventory from a JSON string (used by Load and by the test suite).</summary>
    public static AssetsInventory Parse(string json, string source = "<inline>")
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
                $"assets inventory '{source}' is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"assets inventory '{source}' root must be a JSON object.");
            }

            // app.app_id
            if (!root.TryGetProperty("app", out var appEl) || appEl.ValueKind != JsonValueKind.Object ||
                !appEl.TryGetProperty("app_id", out var appIdEl) ||
                appIdEl.ValueKind != JsonValueKind.Number || !appIdEl.TryGetUInt32(out var appId))
            {
                throw new InvalidDataException(
                    $"assets inventory '{source}' must have app.app_id (a uint32).");
            }

            // depots[]: derive platform -> binary depot (role == "binary").
            if (!root.TryGetProperty("depots", out var depotsEl) || depotsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"assets inventory '{source}' must have a 'depots' array.");
            }
            var platformBinaryDepot = new Dictionary<string, uint>(StringComparer.Ordinal);
            uint? contentDepotId = null;
            uint? toolsDepotId = null;
            foreach (var depotEl in depotsEl.EnumerateArray())
            {
                if (depotEl.ValueKind != JsonValueKind.Object)
                    continue;
                if (!depotEl.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var role = roleEl.GetString();

                // role=="content": capture the single shared content depot id (cross-platform). The
                // UNIFIED batch acquire co-locates this depot's selective pak with the binaries.
                if (string.Equals(role, "content", StringComparison.Ordinal))
                {
                    if (depotEl.TryGetProperty("depot_id", out var cidEl) &&
                        cidEl.ValueKind == JsonValueKind.Number && cidEl.TryGetUInt32(out var cid))
                    {
                        if (contentDepotId.HasValue && contentDepotId.Value != cid)
                        {
                            throw new InvalidDataException(
                                $"assets inventory '{source}' lists two content depots " +
                                $"({contentDepotId.Value} and {cid}); cannot resolve the content depot.");
                        }
                        contentDepotId = cid;
                    }
                    continue;
                }

                // role=="tools": capture the single Workshop Tools depot id (2347779; windows-only —
                // Valve ships no Linux/mac Workshop Tools). The `--tools` acquire co-locates this
                // depot's DLL slice with the windows binaries via ToolsTargetFor.
                if (string.Equals(role, "tools", StringComparison.Ordinal))
                {
                    if (depotEl.TryGetProperty("depot_id", out var tidEl) &&
                        tidEl.ValueKind == JsonValueKind.Number && tidEl.TryGetUInt32(out var tid))
                    {
                        if (toolsDepotId.HasValue && toolsDepotId.Value != tid)
                        {
                            throw new InvalidDataException(
                                $"assets inventory '{source}' lists two tools depots " +
                                $"({toolsDepotId.Value} and {tid}); cannot resolve the tools depot.");
                        }
                        toolsDepotId = tid;
                    }
                    continue;
                }

                if (!string.Equals(role, "binary", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!depotEl.TryGetProperty("depot_id", out var depotIdEl) ||
                    depotIdEl.ValueKind != JsonValueKind.Number || !depotIdEl.TryGetUInt32(out var depotId))
                {
                    throw new InvalidDataException(
                        $"assets inventory '{source}' has a binary depot with no uint32 depot_id.");
                }
                if (!depotEl.TryGetProperty("platforms", out var platformsEl) ||
                    platformsEl.ValueKind != JsonValueKind.Array)
                {
                    continue;   // a binary depot with no platforms[] is not selectable.
                }
                foreach (var pEl in platformsEl.EnumerateArray())
                {
                    if (pEl.ValueKind != JsonValueKind.String)
                        continue;
                    var plat = pEl.GetString()!;
                    if (platformBinaryDepot.TryGetValue(plat, out var existing) && existing != depotId)
                    {
                        throw new InvalidDataException(
                            $"assets inventory '{source}' maps platform '{plat}' to two binary " +
                            $"depots ({existing} and {depotId}); cannot resolve the binary depot.");
                    }
                    platformBinaryDepot[plat] = depotId;
                }
            }

            // builds[]: build_id + binaries{platform -> GID-string}.
            if (!root.TryGetProperty("builds", out var buildsEl) || buildsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"assets inventory '{source}' must have a 'builds' array.");
            }
            var builds = new List<InventoryBuild>();
            var contentGidByBuild = new Dictionary<uint, ulong>();
            var toolsGidByBuild = new Dictionary<uint, ulong>();
            var allBuildIds = new HashSet<uint>();
            var predecessorByBuild = new Dictionary<uint, uint>();
            foreach (var buildEl in buildsEl.EnumerateArray())
            {
                if (buildEl.ValueKind != JsonValueKind.Object)
                    continue;
                if (!buildEl.TryGetProperty("build_id", out var buildIdEl) ||
                    buildIdEl.ValueKind != JsonValueKind.Number || !buildIdEl.TryGetUInt32(out var buildId))
                {
                    continue;   // a build with no integer build_id is skipped (not selectable).
                }
                allBuildIds.Add(buildId);

                // builds[].predecessor: the declared next-lower in-scope build_id (number), or null at
                // the floor. Absent/null -> no entry (PredecessorOf returns null).
                if (buildEl.TryGetProperty("predecessor", out var predEl) &&
                    predEl.ValueKind == JsonValueKind.Number && predEl.TryGetUInt32(out var predId))
                {
                    predecessorByBuild[buildId] = predId;
                }

                // builds[].content: the per-build shared-content manifest GID (string preferred). Recorded
                // independent of binaries — the UNIFIED batch acquire reads it via ContentTargetFor. A build
                // with no `content` simply has no recorded content GID (content omitted for it).
                if (buildEl.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    contentGidByBuild[buildId] = ParseGid(contentEl, source, buildId, "content");
                }

                // builds[].tools: the per-build Workshop Tools manifest GID (string preferred, like
                // "content"). Recorded independent of binaries — the `--tools` acquire reads it via
                // ToolsTargetFor. A build with no `tools` simply has no recorded tools GID (tools
                // omitted for it).
                if (buildEl.TryGetProperty("tools", out var toolsEl) &&
                    toolsEl.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    toolsGidByBuild[buildId] = ParseGid(toolsEl, source, buildId, "tools");
                }

                if (!buildEl.TryGetProperty("binaries", out var binEl) || binEl.ValueKind != JsonValueKind.Object)
                {
                    continue;   // no binaries block -> not selectable for any platform.
                }
                var byPlatform = new Dictionary<string, ulong>(StringComparer.Ordinal);
                foreach (var binProp in binEl.EnumerateObject())
                {
                    var gid = ParseGid(binProp.Value, source, buildId, binProp.Name);
                    byPlatform[binProp.Name] = gid;
                }
                if (byPlatform.Count > 0)
                {
                    builds.Add(new InventoryBuild(buildId, byPlatform));
                }
            }

            return new AssetsInventory(
                appId, platformBinaryDepot, contentDepotId, contentGidByBuild,
                toolsDepotId, toolsGidByBuild, builds, allBuildIds, predecessorByBuild);
        }
    }

    /// <summary>Read a binary manifest GID (uint64): a JSON string (canonical) or a number.</summary>
    private static ulong ParseGid(JsonElement el, string source, uint buildId, string platform)
    {
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
            $"assets inventory '{source}' build {buildId} binaries['{platform}'] must be a " +
            $"uint64 manifest GID (string preferred; got {el.ValueKind}).");
    }
}
