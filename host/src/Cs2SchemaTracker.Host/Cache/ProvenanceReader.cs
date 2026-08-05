// Read the input-binary references out of a committed provenance.json.
//
// Both the populate path (upload) and the fetch path (download) need the same thing from a
// provenance.json: the list of (relative path, SHA-256) input binaries. provenance.json is canonical
// proto3 JSON, so we parse it through the generated Schemas.Provenance message — the single source
// of truth for the shape — rather than hand-rolling JSON traversal.
//
// Fail-loud: a missing / unparseable provenance.json, or an input row with an empty path or a
// malformed SHA-256, throws before any cache work.

using System.Globalization;

using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cache;

/// <summary>One input-binary reference from a provenance.json.</summary>
internal sealed record ProvenanceBinaryRef(string Path, string Sha256, ulong FileSize);

internal static class ProvenanceReader
{
    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>
    /// Read the Steam identity (app_id, build_id, per-depot manifest GIDs) out of a
    /// committed provenance.json as a <see cref="ManifestSpec"/> — the exact pinned
    /// set to re-acquire. Reuses ManifestSpec so the re-acquire path is identical to
    /// <c>--from-manifest</c>. Fail-loud: a missing / unparseable provenance.json, an absent steam
    /// block, a missing build/app id, an
    /// empty depot list, or a malformed manifest GID throws before any Steam contact.
    /// </summary>
    public static ManifestSpec ReadSteamSpec(string provenancePath)
    {
        var prov = Parse(provenancePath);

        if (prov.Steam is null)
        {
            throw new InvalidDataException(
                $"provenance.json at '{provenancePath}' has no steam block; cannot re-acquire its pinned inputs.");
        }

        // Build id: prefer steam.steam_build_id, fall back to the top-level build_id.
        var buildIdStr = !string.IsNullOrEmpty(prov.Steam.SteamBuildId) ? prov.Steam.SteamBuildId : prov.BuildId;
        if (string.IsNullOrEmpty(buildIdStr) ||
            !uint.TryParse(buildIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var buildId))
        {
            throw new InvalidDataException(
                $"provenance.json at '{provenancePath}' has no parseable Steam build id (got '{buildIdStr}').");
        }

        if (prov.Steam.AppId == 0)
        {
            throw new InvalidDataException(
                $"provenance.json at '{provenancePath}' has app_id 0; cannot re-acquire.");
        }

        if (prov.Steam.Depots.Count == 0)
        {
            throw new InvalidDataException(
                $"provenance.json at '{provenancePath}' lists zero depots; nothing to re-acquire.");
        }

        var depots = new List<ManifestSpecDepot>(prov.Steam.Depots.Count);
        var seen = new HashSet<uint>();
        foreach (var d in prov.Steam.Depots)
        {
            if (!ulong.TryParse(d.ManifestId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gid))
            {
                throw new InvalidDataException(
                    $"provenance.json at '{provenancePath}' depot {d.DepotId} has a malformed manifest_id '{d.ManifestId}'.");
            }
            if (!seen.Add(d.DepotId))
            {
                throw new InvalidDataException(
                    $"provenance.json at '{provenancePath}' lists depot {d.DepotId} more than once.");
            }
            depots.Add(new ManifestSpecDepot(d.DepotId, gid));
        }

        return new ManifestSpec(prov.Steam.AppId, buildId, depots);
    }

    private static Schemas.Provenance Parse(string provenancePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(provenancePath);
        if (!File.Exists(provenancePath))
        {
            throw new FileNotFoundException(
                $"provenance.json not found at '{provenancePath}'.", provenancePath);
        }

        try
        {
            return Parser.Parse<Schemas.Provenance>(File.ReadAllText(provenancePath));
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException(
                $"provenance.json at '{provenancePath}' is not valid (schemas/provenance.proto): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parse <paramref name="provenancePath"/> and return its input-binary refs.
    /// Throws <see cref="FileNotFoundException"/> if the file is absent and
    /// <see cref="InvalidDataException"/> if it is unparseable or carries a malformed input row.
    /// </summary>
    public static IReadOnlyList<ProvenanceBinaryRef> ReadInputs(string provenancePath)
    {
        var refs = ReadInputsAllowEmpty(provenancePath);
        if (refs.Count == 0)
        {
            throw new InvalidDataException(
                $"provenance.json at '{provenancePath}' lists zero input binaries (requires every input).");
        }
        return refs;
    }

    /// <summary>
    /// Like <see cref="ReadInputs"/> but a provenance with ZERO inputs returns an empty
    /// list instead of throwing. Used by the at-use verification: a committed provenance that carries
    /// no input hashes (e.g. a legacy/minimal record) has nothing to verify, which is a documented
    /// SKIP, not a fail. Still fail-loud on a missing/unparseable file or a malformed input row.
    /// </summary>
    public static IReadOnlyList<ProvenanceBinaryRef> ReadInputsAllowEmpty(string provenancePath)
    {
        var prov = Parse(provenancePath);

        var refs = new List<ProvenanceBinaryRef>(prov.Inputs.Count);
        foreach (var ib in prov.Inputs)
        {
            if (string.IsNullOrEmpty(ib.Path))
            {
                throw new InvalidDataException(
                    $"provenance.json at '{provenancePath}' has an input binary with an empty path.");
            }
            // Validate the key now so a malformed SHA fails here, not deep in a store.
            var sha = Sha256Hex.Validate(ib.Sha256);
            refs.Add(new ProvenanceBinaryRef(ib.Path, sha, ib.FileSize));
        }
        return refs;
    }

    /// <summary>
    /// Resolve a provenance-relative binary path (forward slashes) to a local path
    /// under <paramref name="baseDir"/>. Rejects any path that escapes the base dir (fail-loud) — a
    /// provenance.json must never address bytes outside its own (build, platform) directory.
    /// </summary>
    public static string ResolveLocal(string baseDir, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDir);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var baseFull = Path.GetFullPath(baseDir);
        var full = Path.GetFullPath(Path.Combine(baseFull, normalized));

        var rooted = baseFull.EndsWith(Path.DirectorySeparatorChar) ? baseFull : baseFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rooted, StringComparison.Ordinal) &&
            !string.Equals(full, baseFull, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"provenance input path '{relativePath}' escapes the binaries directory '{baseDir}'.");
        }
        return full;
    }
}
