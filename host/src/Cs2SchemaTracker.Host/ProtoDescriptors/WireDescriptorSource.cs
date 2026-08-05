// Loader for data/wire_descriptors.pb — the SDK-sourced engine wire-message descriptors that the
// proto extractor merges into every build's protos.descriptorset.
//
// WHY: CS2 ships the engine wire-message protos (netmessages, usermessages, gameevents, te,
// clientmessages, cs_gameevents, cstrike15_usermessages, networkbasetypes) built WITHOUT an embedded
// FileDescriptorProto — proven by an all-182-binary, both-platform scan that recovers 33 descriptors
// and none of these. They are exactly the families network_messages.json / demo_messages.json bind
// wire IDs to, so without them those joins reference message types defined nowhere in the artifact
// set (3/191 resolve). The definitions DO exist as source in the pinned hl2sdk submodule; the
// committed data/wire_descriptors.pb is their compiled form (scripts/gen-wire-descriptors.sh),
// carrying ONLY those wire files. Their non-wire imports stay the canonical binary-derived copies.
//
// The file is committed AND shipped next to the host (csproj Content copy), so it resolves the same
// way the assets inventory does: repo-root copy first (live in-repo dev), else the app-dir copy.

using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Cs2SchemaTracker.Host.ProtoDescriptors;

internal static class WireDescriptorSource
{
    /// <summary>The committed wire descriptor set, repo-relative.</summary>
    public const string FileName = "wire_descriptors.pb";

    /// <summary>
    /// Resolve the wire descriptor set path: the LIVE repo copy at
    /// <c>&lt;repoRoot&gt;/data/wire_descriptors.pb</c> when present, else the app-dir copy shipped by
    /// the csproj Content include. Mirrors <see cref="Inventory.InventoryCatalogProvider.ResolveInventoryPath"/>.
    /// </summary>
    public static string ResolvePath(string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        var repoCopy = Path.Combine(repoRoot, "data", FileName);
        if (File.Exists(repoCopy))
        {
            return repoCopy;
        }
        return Path.Combine(AppContext.BaseDirectory, "data", FileName);
    }

    /// <summary>
    /// Load the wire descriptor FileDescriptorProtos. Fail-loud: the file is committed + shipped, so
    /// a missing or unparseable copy is a real deployment/corruption fault, never a silent skip — the
    /// wire-ID joins would silently dangle otherwise.
    /// </summary>
    public static IReadOnlyList<FileDescriptorProto> Load(string repoRoot)
    {
        var path = ResolvePath(repoRoot);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"WireDescriptorSource: '{FileName}' not found at '{path}'. It is a committed, shipped "
                + "artifact (data/wire_descriptors.pb; regenerate with scripts/gen-wire-descriptors.sh). "
                + "Without it the engine wire descriptors are absent and network_messages.json / "
                + "demo_messages.json wire-ID joins dangle.", path);
        }

        FileDescriptorSet set;
        try
        {
            set = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(path));
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException(
                $"WireDescriptorSource: '{path}' is not a valid FileDescriptorSet: {ex.Message}", ex);
        }

        if (set.File.Count == 0)
        {
            throw new InvalidDataException(
                $"WireDescriptorSource: '{path}' carries zero descriptors — refusing to run with an "
                + "empty wire set (regenerate with scripts/gen-wire-descriptors.sh).");
        }
        return set.File.ToList();
    }
}
