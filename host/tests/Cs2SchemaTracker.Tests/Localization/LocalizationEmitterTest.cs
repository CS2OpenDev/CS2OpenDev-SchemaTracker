// tests — localization token-table extraction from the resource/csgo_<lang>.txt KV1
// family inside a content-depot VPK.
//
// Every fixture is a hand-constructed in-memory VPK (mirroring ItemDefinitionsEmitterTest)
// carrying one or more resource/csgo_<lang>.txt files. We assert:
//   * the COMBINED token-keyed mapping (one row per token, per-language values, english_value
//     convenience copy), with the token universe spanning every language;
// * UTF-16LE source bytes are normalized deterministically before parsing;
//   * deterministic byte-identical output across two runs, languages/tokens/values all
//     Ordinal-sorted regardless of source order;
// * fail-loud on malformed KV1, missing entry family, a missing english table,
//     and a scalar Tokens block — with NO output bytes.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Localization;
using Cs2SchemaTracker.Host.Vpk;

using Xunit;

namespace Cs2SchemaTracker.Tests.Localization;

public class LocalizationEmitterTest
{
    private const string BuildId = "13371337";
    private const string Platform = "windows-x86_64";

    private const uint Signature = 0x55AA1234u;
    private const ushort Embedded = 0x7FFF;
    private const ushort Terminator = 0xFFFF;

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private sealed record FileSpec(string Path, string Ext, string Name, byte[] Body);

    private static byte[] BuildEmbeddedVpk(int version, IReadOnlyList<FileSpec> files)
    {
        var tree = new MemoryStream();
        var dataSection = new MemoryStream();

        var offsets = new Dictionary<FileSpec, uint>();
        foreach (var f in files)
        {
            offsets[f] = (uint)dataSection.Length;
            dataSection.Write(f.Body);
        }

        foreach (var byExt in files.GroupBy(f => f.Ext))
        {
            WriteCString(tree, byExt.Key);
            foreach (var byPath in byExt.GroupBy(f => f.Path))
            {
                WriteCString(tree, byPath.Key);
                foreach (var f in byPath)
                {
                    WriteCString(tree, f.Name);
                    WriteU32(tree, Crc32(f.Body));
                    WriteU16(tree, 0);
                    WriteU16(tree, Embedded);
                    WriteU32(tree, offsets[f]);
                    WriteU32(tree, (uint)f.Body.Length);
                    WriteU16(tree, Terminator);
                }
                tree.WriteByte(0);
            }
            tree.WriteByte(0);
        }
        tree.WriteByte(0);

        byte[] treeBytes = tree.ToArray();
        byte[] dataBytes = dataSection.ToArray();

        var ms = new MemoryStream();
        WriteU32(ms, Signature);
        WriteU32(ms, (uint)version);
        WriteU32(ms, (uint)treeBytes.Length);
        if (version == 2)
        {
            WriteU32(ms, (uint)dataBytes.Length);
            WriteU32(ms, 0);
            WriteU32(ms, 0);
            WriteU32(ms, 0);
        }
        ms.Write(treeBytes);
        ms.Write(dataBytes);
        return ms.ToArray();
    }

    private static void WriteCString(Stream s, string value) { s.Write(Encoding.UTF8.GetBytes(value)); s.WriteByte(0); }
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteU16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); s.Write(b); }

    private static LocalizationEmitter NewEmitter() => new(SchemaFamily.Version, BuildId, Platform);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string English =
        """
        "lang"
        {
            "Language" "English"
            "Tokens"
            {
                "weapon_ak47" "AK-47"
                "SFUI_only_english" "EnglishOnly"
            }
        }
        """;

    private const string German =
        """
        "lang"
        {
            "Language" "German"
            "Tokens"
            {
                "weapon_ak47" "AK-47 (de)"
                "weapon_german_only" "NurDeutsch"
            }
        }
        """;

    private static VpkArchive ArchiveWith(IEnumerable<FileSpec> files) =>
        VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files.ToList()));

    private static FileSpec Lang(string lang, byte[] body) => new("resource", "txt", "csgo_" + lang, body);
    private static FileSpec LangUtf8(string lang, string body) => Lang(lang, Encoding.UTF8.GetBytes(body));

    [Fact]
    public void Combines_Tokens_Across_Languages_TokenKeyed()
    {
        var dir = NewWorkDir();
        try
        {
            // German first to exercise the deterministic merge order independence.
            var archive = ArchiveWith(new[] { LangUtf8("german", German), LangUtf8("english", English) });
            var outPath = Path.Combine(dir, "localization.json");
            NewEmitter().Emit(archive, outPath);

            var bytes = File.ReadAllBytes(outPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "localization.json must not have a UTF-8 BOM");
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());

            // languages Ordinal-sorted: english < german.
            var langs = root.GetProperty("languages");
            Assert.Equal(2, langs.GetArrayLength());
            Assert.Equal("english", langs[0].GetString());
            Assert.Equal("german", langs[1].GetString());

            // tokens Ordinal-sorted across the UNION of both files:
            // SFUI_only_english, weapon_ak47, weapon_german_only.
            var tokens = root.GetProperty("tokens");
            Assert.Equal(3, tokens.GetArrayLength());
            Assert.Equal("SFUI_only_english", tokens[0].GetProperty("token").GetString());
            Assert.Equal("weapon_ak47", tokens[1].GetProperty("token").GetString());
            Assert.Equal("weapon_german_only", tokens[2].GetProperty("token").GetString());

            // weapon_ak47 has both languages; values Ordinal-sorted by language code.
            var ak = tokens[1];
            Assert.Equal("AK-47", ak.GetProperty("englishValue").GetString());
            var vals = ak.GetProperty("values");
            Assert.Equal(2, vals.GetArrayLength());
            Assert.Equal("english", vals[0].GetProperty("language").GetString());
            Assert.Equal("AK-47", vals[0].GetProperty("value").GetString());
            Assert.Equal("german", vals[1].GetProperty("language").GetString());
            Assert.Equal("AK-47 (de)", vals[1].GetProperty("value").GetString());

            // A german-only token: english_value defaults to "", only the german value present.
            var go = tokens[2];
            Assert.Equal("", go.GetProperty("englishValue").GetString());
            Assert.Equal(1, go.GetProperty("values").GetArrayLength());
            Assert.Equal("german", go.GetProperty("values")[0].GetProperty("language").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Decodes_Utf16LE_Source()
    {
        var dir = NewWorkDir();
        try
        {
            // english file encoded as UTF-16LE with a leading BOM (the common CS2 lang-file
            // encoding). GetBytes() does NOT emit a BOM, so prepend the FF FE preamble explicitly.
            var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            byte[] body = enc.GetBytes(English);
            byte[] utf16 = new byte[2 + body.Length];
            utf16[0] = 0xFF;
            utf16[1] = 0xFE;
            Array.Copy(body, 0, utf16, 2, body.Length);
            var archive = ArchiveWith(new[] { Lang("english", utf16) });
            var outPath = Path.Combine(dir, "localization.json");
            NewEmitter().Emit(archive, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var tokens = doc.RootElement.GetProperty("tokens");
            // The UTF-16 source must have decoded and parsed identically to UTF-8.
            Assert.Equal(2, tokens.GetArrayLength());
            Assert.Equal("weapon_ak47", tokens[1].GetProperty("token").GetString());
            Assert.Equal("AK-47", tokens[1].GetProperty("englishValue").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Two_Runs_Byte_Identical(int version)
    {
        var dir = NewWorkDir();
        try
        {
            var files = new List<FileSpec> { LangUtf8("german", German), LangUtf8("english", English) };
            var archiveBytes = BuildEmbeddedVpk(version, files);
            var a = Path.Combine(dir, "a.json");
            var b = Path.Combine(dir, "b.json");
            NewEmitter().Emit(VpkArchive.Parse("pak01_dir.vpk", archiveBytes), a);
            NewEmitter().Emit(VpkArchive.Parse("pak01_dir.vpk", archiveBytes), b);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- fail-loud paths ----

    [Fact]
    public void FailLoud_No_Lang_Files()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith(new[] { new FileSpec("scripts", "txt", "items", Encoding.ASCII.GetBytes("x")) });
            var outPath = Path.Combine(dir, "localization.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("csgo_<lang>", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // HasSource: the full-extract path uses this to tell a GENUINE absence (no csgo_<lang>.txt this
    // era — e.g. the 2023 baseline build 10832117 ships zero — ⇒ graceful omission) from a present
    // source. Emit still fail-louds on the explicit single-artifact path; HasSource just routes the
    // full extract to OMIT instead of dying.
    [Fact]
    public void HasSource_FalseWhenNoLangFiles_TrueWhenPresent()
    {
        var none = ArchiveWith(new[] { new FileSpec("scripts", "txt", "items", Encoding.ASCII.GetBytes("x")) });
        Assert.False(LocalizationEmitter.HasSource(none));

        var present = ArchiveWith(new[] { LangUtf8("english", English) });
        Assert.True(LocalizationEmitter.HasSource(present));
    }

    [Fact]
    public void FailLoud_Missing_English()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith(new[] { LangUtf8("german", German) });
            var outPath = Path.Combine(dir, "localization.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("english", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Malformed_Kv1()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith(new[] { LangUtf8("english", "\"lang\"\n{\n  \"Tokens\"\n  {\n") });
            var outPath = Path.Combine(dir, "localization.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("KV1", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Scalar_Tokens_Section()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith(new[] { LangUtf8("english", "\"lang\" { \"Tokens\" \"oops\" }") });
            var outPath = Path.Combine(dir, "localization.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("Tokens", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Missing_Vpk()
    {
        var dir = NewWorkDir();
        try
        {
            var missing = Path.Combine(dir, "does-not-exist_dir.vpk");
            var outPath = Path.Combine(dir, "localization.json");
            Assert.Throws<FileNotFoundException>(() => NewEmitter().EmitFromVpk(missing, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
