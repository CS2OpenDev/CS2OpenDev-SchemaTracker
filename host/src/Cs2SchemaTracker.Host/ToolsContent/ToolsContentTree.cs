// Read access to the loose Workshop Tools content files for one (build, platform).
//
// The Workshop Tools depot (2347779, Windows-only) ships the FGD files and Valve's cs_script
// declaration file as LOOSE files, not inside a VPK. The tools acquire keeps exactly the slice
// ToolsBinSelector.IsToolsContentFile selects and lands it at its depot-relative paths under the
// per-build tuple dir (beside the binaries, never inside a bin/ directory):
//
//   <tuple>/game/csgo/*.fgd
//   <tuple>/game/core/*.fgd
//   <tuple>/content/csgo_addons/cs_script_demo/maps/scripts/point_script.d.ts
//   <tuple>/content/csgo_addons/cs_script_demo/maps/scripts/tsconfig.json
//
// A local CS2 install with the Workshop Tools has the same layout under its install root, which
// is what the development override (--tools-content-root) points at.
//
// Paths in and out of this type are depot-relative with forward slashes — the form provenance
// records and the artifacts carry.

using System.Security.Cryptography;
using System.Text;

namespace Cs2SchemaTracker.Host.ToolsContent;

internal sealed class ToolsContentTree
{
    public ToolsContentTree(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The directory the depot-relative paths resolve against.</summary>
    public string Root { get; }

    public string FullPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        if (relativePath.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                $"ToolsContentTree: '{relativePath}' is not a depot-relative path inside the tools content tree.");
        }
        var exact = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(exact))
            return exact;

        // Depot manifests carry their own casing and a case-sensitive filesystem keeps it, so fall
        // back to a per-segment case-insensitive lookup. A segment with two case-variant matches is
        // ambiguous and resolves to nothing rather than to whichever the directory listed first.
        var current = Root;
        foreach (var segment in relativePath.Split('/'))
        {
            if (!Directory.Exists(current))
                return exact;
            var matches = Directory.EnumerateFileSystemEntries(current)
                .Where(e => string.Equals(Path.GetFileName(e), segment, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (matches.Count != 1)
                return exact;
            current = matches[0];
        }
        return current;
    }

    public bool Exists(string relativePath) => File.Exists(FullPath(relativePath));

    public byte[] ReadAllBytes(string relativePath)
    {
        var full = FullPath(relativePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"ToolsContentTree: '{relativePath}' not found under '{Root}'.", full);
        }
        return File.ReadAllBytes(full);
    }

    /// <summary>
    /// Decode a text source. UTF-8 (with or without BOM) is the only encoding these files ship in;
    /// a byte sequence that is not valid UTF-8 fails loud rather than decoding to replacement
    /// characters that would then be published as if Valve wrote them.
    /// </summary>
    public static string DecodeText(byte[] bytes, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"ToolsContentTree: '{relativePath}' is not valid UTF-8: {ex.Message}", ex);
        }
    }

    public static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
