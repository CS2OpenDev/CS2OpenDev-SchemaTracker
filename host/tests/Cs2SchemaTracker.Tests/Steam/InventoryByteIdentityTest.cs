// Determinism guard for data/cs2-assets-inventory.json.
//
// Locks the canonical-form contract: the REAL committed inventory, round-tripped through the host
// writer (InventoryWriter's canonical serializer), must come back BYTE-IDENTICAL. The file is
// maintained in the Python ingest form — json.dumps(obj, indent=2, ensure_ascii=False) + "\n":
// 2-space indent, ": " after keys, LF endings, UTF-8 no BOM, single trailing newline, and NO
// HTML/relaxed escaping of ' / < / > / + / non-ASCII.
//
// This test FAILS if the writer's JsonSerializerOptions ever regresses to STJ's default
// JavaScriptEncoder (which HTML-escapes ' -> ', < -> <, > -> >, + -> +), which
// would clobber the canonical form and spray a spurious diff across every forward-capture write.
//
// EOL note: on a Windows checkout core.autocrlf rewrites the working-tree file to CRLF (data/** is
// not pinned in .gitattributes), while git stores — and the writer emits — LF. We normalize the
// on-disk bytes to LF to recover the OS-independent canonical form, then assert the writer
// reproduces exactly that. On a LF checkout (Linux CI) the normalization is a no-op.

using System.Text;
using System.Text.Json.Nodes;

using Cs2SchemaTracker.Host.Inventory;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public sealed class InventoryByteIdentityTest
{
    [Fact]
    public void Real_Inventory_RoundTrips_ByteIdentically_Through_Host_Writer()
    {
        var repoRoot = FindRepoRoot();
        var realPath = Path.Combine(repoRoot, "data", "cs2-assets-inventory.json");
        Assert.True(File.Exists(realPath), $"expected the committed inventory at {realPath}");

        // The canonical form is LF (git-stored). Undo any autocrlf CRLF the working tree carries so
        // the assertion is OS-independent and reflects exactly what the writer must emit + commit.
        var onDisk = File.ReadAllBytes(realPath);
        var canonical = NormalizeToLf(onDisk);

        // Sanity: the fixture must actually exercise the encoder — it has to contain characters the
        // DEFAULT STJ encoder would HTML-escape, or this test could pass a broken encoder vacuously.
        var canonicalText = Encoding.UTF8.GetString(canonical);
        Assert.Contains("'", canonicalText, StringComparison.Ordinal);
        Assert.True(
            canonicalText.Contains('<') || canonicalText.Contains('>') || canonicalText.Contains('+'),
            "expected the inventory to contain at least one of < > + so the encoder is under test.");

        // Round-trip through the REAL writer's canonical serializer: parse the canonical (LF) bytes to
        // a JsonNode tree and re-serialize with NO mutation. The output must be a fixpoint.
        var tree = JsonNode.Parse(canonicalText)!;
        var reserialized = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(InventoryWriter.CanonicalSerialize(tree));

        Assert.True(
            canonical.AsSpan().SequenceEqual(reserialized),
            "host writer output is NOT byte-identical to the canonical inventory " +
            $"(expected {canonical.Length} bytes, got {reserialized.Length}). Likely an encoder regression: " +
            "the writer must use JavaScriptEncoder.UnsafeRelaxedJsonEscaping (Python ensure_ascii=False form).");
    }

    /// <summary>Undo CRLF (autocrlf) to recover the OS-independent LF canonical bytes.</summary>
    private static byte[] NormalizeToLf(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(lf);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "data", "cs2-assets-inventory.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root (data/cs2-assets-inventory.json).");
    }
}
