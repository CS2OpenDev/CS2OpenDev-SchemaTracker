// tests — weapon VData extraction from the COMPILED scripts/weapons.vdata_c
// (binary KV3) inside a content-depot VPK.
//
// Unlike the other content-emitter suites the bodies here are almost all the REAL shipped
// weapons.vdata_c (build 25175329, content depot 2347770) already committed under
// Kv3Binary/fixtures/ for the reader tests and copied next to the test binary by the csproj
// glob. A synthesised stub would not decode, and a decode that never runs proves nothing about
// the mapping.
//
// The ONE exception is the depth case. No shipped weapons.vdata_c nests anywhere near the 64
// levels System.Text.Json will read, so that path is unreachable from the committed fixture and
// a synthetic uncompressed KV3 v3 block is hand-built for it (NestedKv3Resource). Because a
// MALFORMED synthetic block would throw the same InvalidDataException as the case under test —
// the decode wrapper names the same source — a shallow control test asserts that the builder
// produces a genuinely decodable resource, so the deep test cannot pass on a decode failure.
//
// We assert:
//   * the happy-path mapping — all 176 top-level entries, verbatim, envelope fields stamped;
//   * weapon_ak47's real values survive unchanged, INCLUDING the two shapes that tempt a
//     normaliser: m_flSpread is a 2-element array and m_flCycleTime is a BARE scalar;
//   * generic_data_type — the one top-level entry that is NOT a map — round-trips as a JSON
//     string (the whole reason the payload field is Value and not Struct);
//   * the numeric item-definition-index alias "1" is present and carries _class weapon_deagle;
//   * entries are Ordinal-sorted (so the numeric aliases sort LEXICOGRAPHICALLY, first);
//   * canonical output: no UTF-8 BOM, no CR, byte-identical across two emits, no leftover .tmp;
//   * HasSource true when the resource ships and false when it does not;
//   * fail-loud with NO output bytes on an absent source, a corrupt vdata body, and a missing
//     VPK path (FileNotFoundException via EmitFromVpk);
//   * a tree that nests deeper than canonical JSON will read fails loud as a typed
//     InvalidDataException naming the source, instead of escaping as a raw JsonException;
//   * the synthetic nested block round-trips when it is shallow (the fixture-validity guard for
//     the case above).

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Host.WeaponVData;

using Xunit;

namespace Cs2SchemaTracker.Tests.WeaponVData;

public class WeaponVDataEmitterTest
{
    private const string BuildId = "13371337";
    private const string Platform = "windows-x86_64";

    /// <summary>Top-level KV3 entries in the committed fixture (45 weapons + 43 prefabs +
    /// 72 numeric aliases + 15 category objects + generic_data_type).</summary>
    private const int TopLevelEntryCount = 176;

    private const uint Signature = 0x55AA1234u;
    private const ushort Embedded = 0x7FFF;
    private const ushort Terminator = 0xFFFF;

    private static byte[] RealWeaponsVdata() => File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Kv3Binary", "fixtures", "weapons.vdata_c"));

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

    // scripts/weapons.vdata_c, embedded in _dir.vpk.
    private static byte[] VpkBytesWith(byte[] body) =>
        BuildEmbeddedVpk(2, new List<FileSpec>
        {
            new("scripts", "vdata_c", "weapons", body),
        });

    private static VpkArchive ArchiveWith(byte[] body) =>
        VpkArchive.Parse("pak01_dir.vpk", VpkBytesWith(body));

    /// <summary>
    /// The DATA block of a hand-built, UNCOMPRESSED KV3 v3 resource whose tree is
    /// <paramref name="depth"/> nested OBJECTs, each carrying the single key "a", innermost value
    /// NULL. Hand-built because the depth ceiling this suite exercises is unreachable from any
    /// shipped resource — the committed weapons.vdata_c nests three or four levels, not sixty.
    /// The geometry below is not a guess: it is Kv3BinaryReader's documented v3 layout, and the
    /// reader's trailer check and RequireFullyConsumed reject anything else. Its correctness is
    /// itself asserted, by Synthetic_Nested_Tree_Round_Trips_When_Shallow.
    /// </summary>
    private static byte[] NestedKv3V3Block(int depth)
    {
        var payload = new MemoryStream();

        // The byte buffer is empty, so the 4-byte slots start at offset 0. The FIRST slot is the
        // string count; the rest are, per OBJECT, its member count then its member's string id.
        WriteU32(payload, 1);
        for (int i = 0; i < depth; i++)
        {
            WriteU32(payload, 1);
            WriteU32(payload, 0);
        }

        // align(8) before the (empty) 8-byte buffer. v3/v4 pad UNCONDITIONALLY, and (1+2*depth)
        // slots is always 4 mod 8 bytes, so these four bytes are always present — omit them and
        // the trailer lands four bytes early and the block is refused.
        payload.Write(new byte[4]);

        // The string + types area: the one NUL-terminated string, then the type stream.
        payload.Write(Encoding.ASCII.GetBytes("a"));
        payload.WriteByte(0);
        for (int i = 0; i < depth; i++)
        {
            payload.WriteByte(0x09);    // OBJECT
        }
        payload.WriteByte(0x01);        // NULL

        // No binary blobs, so the blob length list is empty and the 00 DD EE FF trailer follows
        // the type stream immediately.
        payload.Write(new byte[] { 0x00, 0xDD, 0xEE, 0xFF });

        byte[] payloadBytes = payload.ToArray();

        var block = new byte[64 + payloadBytes.Length];
        new byte[] { 0x03, 0x33, 0x56, 0x4B }.CopyTo(block.AsSpan(0));   // KV3_V3 magic
        // [4..20) is the format GUID, which the reader never reads; it stays zero.
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20, 4), 0);                        // compressionMethod: stored
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28, 4), 0);                        // byte buffer size
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(32, 4), (uint)(1 + (2 * depth)));  // 4-byte slot count
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(36, 4), 0);                        // 8-byte value count
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(40, 4), (uint)(depth + 3));        // string + types area
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(48, 4), (uint)payloadBytes.Length);
        // v3 writes the uncompressed size into the compressed-size field too under method 0.
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(52, 4), (uint)payloadBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(56, 4), 0);                        // blob count
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(60, 4), 0);                        // blob total size
        payloadBytes.CopyTo(block.AsSpan(64));
        return block;
    }

    /// <summary>
    /// <see cref="NestedKv3V3Block"/> wrapped in a minimal compiled-resource container holding
    /// one DATA block — the same shape Kv3BinaryReaderTest.BuildContainer produces, which is what
    /// Kv3BinaryReader.Decode walks to find the payload.
    /// </summary>
    private static byte[] NestedKv3Resource(int depth)
    {
        byte[] block = NestedKv3V3Block(depth);
        const int TableStart = 16;
        const int PayloadStart = TableStart + 12;

        var bytes = new byte[PayloadStart + block.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 12);   // headerVersion
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 0);    // resourceVersion
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), TableStart - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 1);   // blockCount
        Encoding.ASCII.GetBytes("DATA").CopyTo(bytes.AsSpan(TableStart, 4));
        // dataOffset is relative to the address of the dataOffset field itself, not to the file.
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(TableStart + 4, 4), PayloadStart - (TableStart + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TableStart + 8, 4), (uint)block.Length);
        block.CopyTo(bytes.AsSpan(PayloadStart));
        return bytes;
    }

    private static WeaponVDataEmitter NewEmitter() => new(SchemaFamily.Version, BuildId, Platform);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaponvdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JsonElement EntryValue(JsonElement entries, string key)
    {
        foreach (var e in entries.EnumerateArray())
        {
            if (string.Equals(e.GetProperty("key").GetString(), key, StringComparison.Ordinal))
            {
                return e.GetProperty("value");
            }
        }
        throw new Xunit.Sdk.XunitException($"no top-level entry '{key}' in weapon_vdata.json");
    }

    [Fact]
    public void Maps_Every_TopLevel_Entry_Verbatim()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            NewEmitter().Emit(ArchiveWith(RealWeaponsVdata()), outPath);

            var bytes = File.ReadAllBytes(outPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "weapon_vdata.json must not have a UTF-8 BOM");
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
            Assert.False(File.Exists(outPath + ".tmp"), "no .tmp may survive a successful emit");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // Envelope.
            Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());
            Assert.Equal(BuildId, root.GetProperty("buildId").GetString());
            Assert.Equal(Platform, root.GetProperty("platform").GetString());
            Assert.Equal("scripts/weapons.vdata_c", root.GetProperty("sourceFile").GetString());

            var entries = root.GetProperty("entries");
            Assert.Equal(TopLevelEntryCount, entries.GetArrayLength());

            // weapon_ak47: real values, unmodified.
            var ak = EntryValue(entries, "weapon_ak47");
            Assert.Equal(JsonValueKind.Object, ak.ValueKind);
            Assert.Equal(36d, ak.GetProperty("m_nDamage").GetDouble());
            Assert.Equal(223d, ak.GetProperty("m_nRecoilSeed").GetDouble());
            Assert.Equal(1.55d, ak.GetProperty("m_flArmorRatio").GetDouble());

            // _class / _base are genuine KV3 keys, retained as ordinary payload — never promoted.
            Assert.Equal("weapon_ak47", ak.GetProperty("_class").GetString());
            Assert.Equal("weapon_ak47_prefab", ak.GetProperty("_base").GetString());

            // m_flSpread is a 2-element ARRAY in the source and stays one.
            var spread = ak.GetProperty("m_flSpread");
            Assert.Equal(JsonValueKind.Array, spread.ValueKind);
            Assert.Equal(2, spread.GetArrayLength());
            Assert.Equal(0.0006d, spread[0].GetDouble());
            Assert.Equal(0.0006d, spread[1].GetDouble());

            // m_flCycleTime's schema type is a 2-array but the SOURCE carries the scalar
            // shorthand. Emitted verbatim — widening it here would invent data.
            var cycleTime = ak.GetProperty("m_flCycleTime");
            Assert.Equal(JsonValueKind.Number, cycleTime.ValueKind);
            Assert.Equal(0.1d, cycleTime.GetDouble());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Bare_String_Entry_Survives_As_A_Json_String()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            NewEmitter().Emit(ArchiveWith(RealWeaponsVdata()), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            // generic_data_type is the ONE top-level entry that is not a map. A Struct-typed
            // payload could not carry it at all; Value can.
            var generic = EntryValue(doc.RootElement.GetProperty("entries"), "generic_data_type");
            Assert.Equal(JsonValueKind.String, generic.ValueKind);
            Assert.Equal("CBasePlayerWeaponVData", generic.GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Numeric_ItemDefinitionIndex_Alias_Is_Kept()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            NewEmitter().Emit(ArchiveWith(RealWeaponsVdata()), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            // "1" is the deagle's item-definition index; the alias entry is a near-duplicate of
            // the named one and is kept, not de-duplicated away.
            var alias = EntryValue(doc.RootElement.GetProperty("entries"), "1");
            Assert.Equal(JsonValueKind.Object, alias.ValueKind);
            Assert.Equal("weapon_deagle", alias.GetProperty("_class").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Entries_Are_Ordinal_Sorted_By_Key()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            NewEmitter().Emit(ArchiveWith(RealWeaponsVdata()), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var keys = doc.RootElement.GetProperty("entries").EnumerateArray()
                .Select(e => e.GetProperty("key").GetString()!).ToList();

            Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), keys);
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
            // Ordinal, not numeric: the aliases lead and sort lexicographically.
            Assert.Equal("1", keys[0]);
            Assert.Equal("10", keys[1]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_Runs_Byte_Identical()
    {
        var dir = NewWorkDir();
        try
        {
            var archiveBytes = VpkBytesWith(RealWeaponsVdata());
            var a = Path.Combine(dir, "a.json");
            var b = Path.Combine(dir, "b.json");
            NewEmitter().Emit(VpkArchive.Parse("pak01_dir.vpk", archiveBytes), a);
            NewEmitter().Emit(VpkArchive.Parse("pak01_dir.vpk", archiveBytes), b);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void HasSource_True_When_The_Resource_Ships()
        => Assert.True(WeaponVDataEmitter.HasSource(ArchiveWith(RealWeaponsVdata())));

    [Fact]
    public void HasSource_False_When_The_Resource_Is_Absent()
    {
        var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, new List<FileSpec>
        {
            new("resource", "txt", "other", Encoding.ASCII.GetBytes("nope")),
        }));
        Assert.False(WeaponVDataEmitter.HasSource(archive));
    }

    // ---- fail-loud paths ----

    [Fact]
    public void FailLoud_Source_Absent_From_The_Archive()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, new List<FileSpec>
            {
                new("resource", "txt", "other", Encoding.ASCII.GetBytes("nope")),
            }));
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("scripts/weapons.vdata_c", ex.Message, StringComparison.Ordinal);
            Assert.Contains("pak01_dir.vpk", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Corrupt_Vdata_Bytes()
    {
        var dir = NewWorkDir();
        try
        {
            // Container header and block table survive; the DATA block runs off the end. The
            // reader's Kv3BinaryException must surface WRAPPED, naming the source.
            byte[] truncated = RealWeaponsVdata()[..1024];
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            var ex = Assert.Throws<InvalidDataException>(
                () => NewEmitter().Emit(ArchiveWith(truncated), outPath));
            Assert.Contains("scripts/weapons.vdata_c", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Garbage_Bytes_Are_Not_A_Compiled_Resource()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            Assert.Throws<InvalidDataException>(
                () => NewEmitter().Emit(ArchiveWith(Encoding.ASCII.GetBytes("not a resource")), outPath));
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
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            Assert.Throws<FileNotFoundException>(() => NewEmitter().EmitFromVpk(missing, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The FIXTURE-VALIDITY GUARD for FailLoud_Tree_Too_Deep_For_Canonical_Json below. That test
    /// asserts an InvalidDataException naming scripts/weapons.vdata_c — which is also exactly
    /// what a synthetic block the reader could not decode would produce, so it could pass for
    /// entirely the wrong reason. This proves the builder emits a resource that really decodes:
    /// the shape asserted here exercises every part of it — the v3 header geometry, the
    /// unconditional align(8) padding, the string table, the OBJECT/NULL type stream, the
    /// trailer. It passes both before and after the depth fix and is not a regression test.
    /// </summary>
    [Fact]
    public void Synthetic_Nested_Tree_Round_Trips_When_Shallow()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            NewEmitter().Emit(ArchiveWith(NestedKv3Resource(3)), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var entries = doc.RootElement.GetProperty("entries");
            Assert.Equal(1, entries.GetArrayLength());

            var value = EntryValue(entries, "a");
            Assert.Equal(JsonValueKind.Object, value.ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                value.GetProperty("a").GetProperty("a").ValueKind);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Tree_Too_Deep_For_Canonical_Json()
    {
        var dir = NewWorkDir();
        try
        {
            // 80 nested KV3 objects put the JSON document 82 levels deep — root object, entries
            // array, entry object, then the 79 objects under the top-level entry's value. That is
            // over JsonDocument's default MaxDepth of 64 and well under Kv3BinaryReader's
            // MaxDepth of 512, so the decode and every check in Emit pass first and the failure
            // happens at serialization, where nothing would otherwise name the resource.
            var outPath = Path.Combine(dir, "weapon_vdata.json");
            var ex = Assert.Throws<InvalidDataException>(
                () => NewEmitter().Emit(ArchiveWith(NestedKv3Resource(80)), outPath));

            Assert.Contains("scripts/weapons.vdata_c", ex.Message, StringComparison.Ordinal);
            // A fragment unique to the serialization wrap, so the decode wrapper's message — which
            // names the same source — cannot satisfy this assertion.
            Assert.Contains("REFUSED TO SERIALIZE", ex.Message, StringComparison.Ordinal);
            // The wrap names depth as the LIKELY cause without asserting it (the catch cannot tell a
            // depth failure from any other JsonException), so both halves are pinned: the diagnosis
            // is offered, and the serializer's own message is carried through for the operator to read.
            Assert.Contains("The usual cause is DEPTH", ex.Message, StringComparison.Ordinal);
            Assert.Contains(ex.InnerException!.Message, ex.Message, StringComparison.Ordinal);
            // Pins that the real serializer failure was wrapped rather than a hand-rolled
            // pre-check substituted for it.
            Assert.IsAssignableFrom<JsonException>(ex.InnerException);

            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
