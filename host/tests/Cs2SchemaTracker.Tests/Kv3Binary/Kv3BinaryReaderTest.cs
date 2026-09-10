// Binary-KV3 reader coverage against REAL shipped Valve bytes.
//
// Four genuine compiled resources from CS2 build 25175329 (content depot 2347770) are
// committed under Kv3Binary/fixtures/ and copied next to the test binary by the csproj. Each
// is paired with <resource>.json: the output of an independently written Python reference
// decoder, itself verified against 2,581 KV3_V5 blocks across ~25,000 files with zero errors.
//
//   scripts/weapons.vdata_c                     32,538 B  the primary fixture. LZ4, two
//       segments, 507 objects / 20,544 4-byte values / 2,049 8-byte values / 2,445
//       auxiliary-buffer arrays; types 5, 6, 9, 11, 13, 14, 15, 16, 17, 18, 25.
//   .../pistol_jump_crouch_w_pistol.vnmclip_c    6,433 B  reaches what weapons.vdata_c cannot:
//       a BINARY_BLOB (2,262 bytes across one LZ4 frame in the block area, i.e. the
//       linked-block decoder and the base64 mapping) plus types 7, 8, 12 and 24.
//   soundstacks/soundeventgroups.vdata_c         1,434 B  compressionMethod 0 — segments
//       stored verbatim with a per-segment compressed size of 0 — and an EMPTY auxiliary
//       8-byte buffer, which is the case where the align(8) padding must NOT be applied.
//   .../aztec_skybox_tree_card_large_01.vmdl_c   4,559 B  a UINT64 (type 4) auxiliary array:
//       m_refMeshGroupMasks is an all-bits-set mask, i.e. the one place signed-vs-unsigned
//       actually shows. Its committed reference JSON reads that element UNSIGNED, matching
//       both the declared KV3 type and the reference decoder's own main-buffer path (the
//       reference reads auxiliary UINT64 through its signed reader — see the report).
//
// Kv3BinaryReader also asserts internally that every buffer cursor lands on its exact declared
// end, so any of these tests passing already means the whole block was consumed without a
// single off-by-one.
//
// The Matches_The_Reference_Decode theory diffs our ENTIRE decoded tree against ground truth
// for all four, so these tests do not merely spot check: an emitter-visible change anywhere
// fails the build. Both sides of that diff are normalized identically first — every number is
// re-emitted through Utf8JsonWriter.WriteNumberValue(double), so doubles are compared as
// doubles rather than as Python's repr vs .NET's shortest-round-trip text (those differ in
// exponent case, e.g. 8e-05 vs 8E-05) — and then both strings go through the production
// Serialization/CanonicalJson layer for key sorting and formatting. No value, key, array
// order or type is weakened by that pass.
//
// The fail-loud tests pin the deliberate refusals: a truncated file, a corrupt LZ4 payload, a
// non-KV3 magic, a container with no DATA block, a v1-v4 block, and compressionMethod 2 (zstd)
// each raise Kv3BinaryException naming what was found — never IndexOutOfRange / Overflow, and
// never a silently wrong tree.

using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Cs2SchemaTracker.Host.Kv3Binary;
using Cs2SchemaTracker.Host.Serialization;

using Google.Protobuf.WellKnownTypes;

using Xunit;

namespace Cs2SchemaTracker.Tests.Kv3Binary;

public class Kv3BinaryReaderTest
{
    private const int WeaponsVdataLength = 32538;

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Kv3Binary", "fixtures");

    private static byte[] WeaponsVdata()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "weapons.vdata_c"));
        Assert.Equal(WeaponsVdataLength, bytes.Length);
        return bytes;
    }

    private static Struct DecodeWeapons()
    {
        Value root = Kv3BinaryReader.Decode(WeaponsVdata());
        Assert.Equal(Value.KindOneofCase.StructValue, root.KindCase);
        return root.StructValue;
    }

    // -----------------------------------------------------------------------------------
    // structural pins
    // -----------------------------------------------------------------------------------

    [Fact]
    public void Decode_Weapons_TopLevel_Has_176_Entries()
    {
        Struct root = DecodeWeapons();
        Assert.Equal(176, root.Fields.Count);

        // 175 of them are objects (weapon definitions and prefabs). The odd one out is the
        // vdata file's own type marker, a bare string sitting at the top level next to them.
        Assert.Equal("CBasePlayerWeaponVData", root.Fields["generic_data_type"].StringValue);

        int objects = 0;
        foreach (Value entry in root.Fields.Values)
        {
            if (entry.KindCase == Value.KindOneofCase.StructValue)
            {
                objects++;
            }
        }
        Assert.Equal(175, objects);
    }

    [Fact]
    public void Decode_WeaponAk47_Has_87_Fields_With_Expected_Values()
    {
        Struct ak = DecodeWeapons().Fields["weapon_ak47"].StructValue;
        Assert.Equal(87, ak.Fields.Count);

        Assert.Equal(36d, Number(ak, "m_nDamage"));
        Assert.Equal(4.0d, Number(ak, "m_flHeadshotMultiplier"));
        Assert.Equal(1.55d, Number(ak, "m_flArmorRatio"));
        Assert.Equal(2.0d, Number(ak, "m_flPenetration"));
        Assert.Equal(8192.0d, Number(ak, "m_flRange"));
        Assert.Equal(0.98d, Number(ak, "m_flRangeModifier"));
        Assert.Equal(300d, Number(ak, "m_nKillAward"));
        Assert.Equal(223d, Number(ak, "m_nRecoilSeed"));
        Assert.Equal(0.368d, Number(ak, "m_flRecoveryTimeStand"));

        // ARRAY_TYPE_AUXILIARY_BUFFER (type 25): the pair lives in segment 0, not segment 1.
        AssertNumberList(ak.Fields["m_flSpread"], 0.0006d, 0.0006d);
        AssertNumberList(ak.Fields["m_flInaccuracyStand"], 0.00641d, 0.00641d);

        // A bare DOUBLE, NOT a one-element array — the distinction is exactly what a
        // desynchronised walk would get wrong first.
        Assert.Equal(Value.KindOneofCase.NumberValue, ak.Fields["m_flCycleTime"].KindCase);
        Assert.Equal(0.1d, ak.Fields["m_flCycleTime"].NumberValue);

        Assert.Equal("weapon_ak47", ak.Fields["_class"].StringValue);
        Assert.Equal("weapon_ak47_prefab", ak.Fields["_base"].StringValue);
    }

    [Fact]
    public void Decode_Weapons_Has_138_Entries_With_A_NonZero_RecoilSeed()
    {
        Struct root = DecodeWeapons();

        int withSeed = 0;
        foreach (Value entry in root.Fields.Values)
        {
            if (entry.KindCase == Value.KindOneofCase.StructValue &&
                entry.StructValue.Fields.TryGetValue("m_nRecoilSeed", out Value? seed) &&
                seed.NumberValue != 0d)
            {
                withSeed++;
            }
        }

        Assert.Equal(138, withSeed);
    }

    [Fact]
    public void Decode_Weapons_NumericAliasEntry_Resolves()
    {
        // Top-level keys are not all identifiers: the file also carries numeric aliases whose
        // key is the string "1". A key read as anything but a verbatim string-table lookup
        // would lose these.
        Struct root = DecodeWeapons();
        Assert.True(root.Fields.ContainsKey("1"));

        Struct alias = root.Fields["1"].StructValue;
        Assert.Equal("weapon_deagle", alias.Fields["_class"].StringValue);
        Assert.Equal(53d, Number(alias, "m_nDamage"));
        Assert.Equal(1454d, Number(alias, "m_nRecoilSeed"));
    }

    [Fact]
    public void Decode_Weapons_Prefabs_Resolve_Independently()
    {
        // Prefab entries sit alongside the concrete weapons and are NOT flattened into them;
        // each is decoded on its own terms, and a root prefab simply has no _base.
        Struct root = DecodeWeapons();

        Struct rifle = root.Fields["rifle"].StructValue;
        Assert.Equal(1.0d, Number(rifle, "m_flArmorRatio"));
        Assert.Equal(42d, Number(rifle, "m_nDamage"));

        Struct statted = root.Fields["statted_item_base"].StructValue;
        Assert.False(statted.Fields.ContainsKey("_base"));
    }

    [Fact]
    public void Decode_Is_Deterministic()
    {
        byte[] bytes = WeaponsVdata();
        Value first = Kv3BinaryReader.Decode(bytes);
        Value second = Kv3BinaryReader.Decode(bytes);

        // Value equality covers keys, values and types; the raw (UNSORTED) JSON additionally
        // pins member insertion order, which is what the emitters' canonical layer sorts.
        Assert.Equal(first, second);
        Assert.Equal(ToRawJson(first), ToRawJson(second));
    }

    // -----------------------------------------------------------------------------------
    // whole-tree diff against the Python reference decode
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("weapons.vdata_c", 500_000)]
    [InlineData("pistol_jump_crouch_w_pistol.vnmclip_c", 50_000)]
    [InlineData("soundeventgroups.vdata_c", 100)]
    [InlineData("aztec_skybox_tree_card_large_01.vmdl_c", 1_000)]
    public void Decode_Matches_The_Reference_Decode_Exactly(string fixture, int minimumLength)
    {
        Value root = Kv3BinaryReader.Decode(File.ReadAllBytes(Path.Combine(FixtureDir, fixture)));
        string actual = CanonicalJson.SerializeRawJson(ToRawJson(root));

        string referenceJson = File.ReadAllText(Path.Combine(FixtureDir, fixture + ".json"));
        using var reference = JsonDocument.Parse(referenceJson);
        string expected = CanonicalJson.SerializeRawJson(Renumber(reference.RootElement));

        // Guard against a vacuous pass (an empty fixture, an empty tree, two empty strings).
        Assert.True(
            expected.Length >= minimumLength,
            $"reference decode of {fixture} is only {expected.Length} chars");

        AssertSameJson(expected, actual);
    }

    [Fact]
    public void Decode_BinaryBlob_Becomes_Base64_And_Round_Trips()
    {
        // The block area: one 2,262-byte blob, LZ4-framed, living in the DATA block AFTER both
        // compressed segments. google.protobuf.Value has no bytes case, so it lands as base64.
        byte[] bytes = File.ReadAllBytes(
            Path.Combine(FixtureDir, "pistol_jump_crouch_w_pistol.vnmclip_c"));
        Struct root = Kv3BinaryReader.Decode(bytes).StructValue;

        Value blob = root.Fields["m_compressedPoseData"];
        Assert.Equal(Value.KindOneofCase.StringValue, blob.KindCase);
        Assert.Equal(2262, Convert.FromBase64String(blob.StringValue).Length);

        // Sanity: the rest of the clip decoded too, so the blob did not swallow the walk.
        Assert.Equal("animation/skeletons/characters/worldmodel.vnmskel",
            root.Fields["m_skeleton"].StringValue);
    }

    [Fact]
    public void Decode_Uncompressed_Segments_And_Empty_Aux_Buffer()
    {
        // compressionMethod 0: each segment is stored verbatim and its compressed-size field is
        // 0, so a decoder that trusts that field reads nothing. The auxiliary 8-byte buffer is
        // also empty here, which is the case where the align(8) padding must NOT be applied.
        byte[] bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "soundeventgroups.vdata_c"));
        Struct root = Kv3BinaryReader.Decode(bytes).StructValue;

        Assert.Equal(2, root.Fields.Count);
        Assert.Equal("CSosSoundEventGroupSchema", root.Fields["generic_data_type"].StringValue);
        Assert.Equal("SOS_GROUPTYPE_STATIC",
            root.Fields["non_mvp_music_group"].StructValue.Fields["m_nGroupType"].StringValue);
    }

    [Fact]
    public void Decode_Auxiliary_Uint64_Is_Unsigned()
    {
        // m_refMeshGroupMasks is a UINT64 (type 4) auxiliary-array element holding an
        // all-bits-set mask. Reading it signed yields -1 and silently loses the mask semantics.
        byte[] bytes = File.ReadAllBytes(
            Path.Combine(FixtureDir, "aztec_skybox_tree_card_large_01.vmdl_c"));
        Struct root = Kv3BinaryReader.Decode(bytes).StructValue;

        Value masks = root.Fields["m_refMeshGroupMasks"];
        Assert.Equal(Value.KindOneofCase.ListValue, masks.KindCase);
        Assert.Equal((double)ulong.MaxValue, masks.ListValue.Values[0].NumberValue);
    }

    // -----------------------------------------------------------------------------------
    // fail-loud
    // -----------------------------------------------------------------------------------

    [Fact]
    public void Decode_Truncated_Resource_Throws()
    {
        byte[] full = WeaponsVdata();

        // Too short even for the container header.
        var tiny = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(full[..8]));
        Assert.Contains("too small for a compiled resource", tiny.Message, StringComparison.Ordinal);

        // Header and block table survive, but the DATA block runs off the end.
        var chopped = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(full[..1024]));
        Assert.Contains("DATA block spans", chopped.Message, StringComparison.Ordinal);

        // Half the file: the block table still parses, the DATA payload does not fit.
        Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(full[..(full.Length / 2)]));
    }

    [Fact]
    public void Decode_Corrupt_Lz4_Payload_Throws()
    {
        // The container and the v5 header stay intact; only the compressed payload is
        // clobbered. A zeroed LZ4 stream decodes its first token as "0 literals, match offset
        // 0", which is unrepresentable — the block must be refused, not partially decoded.
        byte[] corrupt = WeaponsVdata();
        int dataStart = DataBlockOffset(corrupt);
        Assert.Equal(0x05, corrupt[dataStart]);   // the fixture really is KV3_V5
        Array.Fill(corrupt, (byte)0, dataStart + 120, 256);

        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(corrupt));
        Assert.Contains("Lz4Block", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Offset of the DATA block payload, found the same way the reader finds it: through the
    /// container's block table, not by scanning for a magic that could collide with payload bytes.
    /// </summary>
    private static int DataBlockOffset(byte[] resource)
    {
        int tableStart = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(resource.AsSpan(8, 4));
        int blockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(resource.AsSpan(12, 4));
        for (int i = 0; i < blockCount; i++)
        {
            int entry = tableStart + (i * 12);
            if (Encoding.ASCII.GetString(resource, entry, 4) == "DATA")
            {
                return entry + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(resource.AsSpan(entry + 4, 4));
            }
        }
        Assert.Fail("the fixture has no DATA block");
        return -1;
    }

    [Fact]
    public void Decode_Bogus_Magic_Throws()
    {
        byte[] resource = BuildContainer(("DATA", MakeBlock(0x4E, 0x4F, 0x50, 0x45)));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains("not binary KV3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_Container_Without_A_Data_Block_Throws()
    {
        byte[] resource = BuildContainer(("RED2", new byte[16]), ("FLCI", new byte[8]));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains("no DATA block", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RED2, FLCI", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Decode_Older_Kv3_Versions_Are_Refused_By_Name(int version)
    {
        // v1-v4 lay their headers out differently; reading them with v5 offsets yields nonsense
        // rather than a clean failure, so they are named and refused until measured.
        byte[] resource = BuildContainer(("DATA", MakeBlock((byte)version, 0x33, 0x56, 0x4B)));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains($"unsupported KV3 version {version}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("only v5 is implemented", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_Legacy_Vkv3_Block_Is_Refused_By_Name()
    {
        byte[] resource = BuildContainer(("DATA", MakeBlock(0x56, 0x4B, 0x56, 0x03)));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains("unsupported KV3 version 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_Zstd_Compression_Is_Refused_By_Name()
    {
        byte[] block = MakeBlock(0x05, 0x33, 0x56, 0x4B);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20, 4), 2u);   // compressionMethod
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(72, 4), 64u);  // SEG0 uncompressed
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(76, 4), 16u);  // SEG0 compressed

        byte[] resource = BuildContainer(("DATA", block));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains("unsupported compression method 2 (zstd)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_Kv3_Block_Shorter_Than_Its_Header_Throws()
    {
        byte[] resource = BuildContainer(("DATA", [0x05, 0x33, 0x56, 0x4B, 0x00, 0x00]));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains("shorter than", ex.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------
    // LZ4 block decoder
    // -----------------------------------------------------------------------------------

    [Fact]
    public void Lz4_Literals_Then_Overlapping_Match_Decodes()
    {
        // token 0x30 = 3 literals, match length nibble 0 (-> 4 bytes) with offset 1, i.e. an
        // RLE run off the last literal. Expected output: "abccccc"... exactly "abc" + "cccc".
        byte[] block = [0x30, (byte)'a', (byte)'b', (byte)'c', 0x01, 0x00];
        byte[] decoded = Lz4Block.Decode(block, 7);
        Assert.Equal("abccccc", Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void Lz4_Bad_Match_Offset_Throws()
    {
        // Match offset 9 with only 3 bytes of output produced so far points before the buffer.
        byte[] block = [0x30, (byte)'a', (byte)'b', (byte)'c', 0x09, 0x00];
        Assert.Throws<Kv3BinaryException>(() => Lz4Block.Decode(block, 7));
    }

    [Fact]
    public void Lz4_Short_Output_Throws()
    {
        byte[] block = [0x30, (byte)'a', (byte)'b', (byte)'c'];
        var ex = Assert.Throws<Kv3BinaryException>(() => Lz4Block.Decode(block, 16));
        Assert.Contains("produced 3 bytes, expected 16", ex.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------------------

    private static double Number(Struct s, string key)
    {
        Value v = s.Fields[key];
        Assert.Equal(Value.KindOneofCase.NumberValue, v.KindCase);
        return v.NumberValue;
    }

    private static void AssertNumberList(Value value, params double[] expected)
    {
        Assert.Equal(Value.KindOneofCase.ListValue, value.KindCase);
        Assert.Equal(expected.Length, value.ListValue.Values.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(Value.KindOneofCase.NumberValue, value.ListValue.Values[i].KindCase);
            Assert.Equal(expected[i], value.ListValue.Values[i].NumberValue);
        }
    }

    /// <summary>A 120-byte KV3 block header carrying <paramref name="magic"/> and nothing else.</summary>
    private static byte[] MakeBlock(byte m0, byte m1, byte m2, byte m3)
    {
        var block = new byte[120];
        block[0] = m0;
        block[1] = m1;
        block[2] = m2;
        block[3] = m3;
        return block;
    }

    /// <summary>
    /// Assemble a minimal compiled-resource container around the given blocks, laid out exactly
    /// as a shipped `*_c` file: 16-byte header, a 12-byte-per-entry block table at offset 16,
    /// then the payloads, each addressed by an offset RELATIVE to its own table entry.
    /// </summary>
    private static byte[] BuildContainer(params (string Name, byte[] Payload)[] blocks)
    {
        const int TableStart = 16;
        int payloadStart = TableStart + (blocks.Length * 12);
        int total = payloadStart;
        foreach ((_, byte[] payload) in blocks)
        {
            total += payload.Length;
        }

        var bytes = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), (uint)total);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)(TableStart - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)blocks.Length);

        int cursor = payloadStart;
        for (int i = 0; i < blocks.Length; i++)
        {
            int entry = TableStart + (i * 12);
            Encoding.ASCII.GetBytes(blocks[i].Name).AsSpan(0, 4).CopyTo(bytes.AsSpan(entry, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 4, 4), (uint)(cursor - (entry + 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 8, 4), (uint)blocks[i].Payload.Length);
            blocks[i].Payload.AsSpan().CopyTo(bytes.AsSpan(cursor));
            cursor += blocks[i].Payload.Length;
        }
        return bytes;
    }

    private static readonly JsonWriterOptions RawWriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serialize a decoded tree to compact JSON in SOURCE order. Numbers go through
    /// <see cref="Utf8JsonWriter.WriteNumberValue(double)"/>, matching <see cref="Renumber"/>.
    /// </summary>
    private static string ToRawJson(Value value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, RawWriterOptions))
        {
            WriteValue(writer, value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, Value value)
    {
        switch (value.KindCase)
        {
            case Value.KindOneofCase.NullValue:
                writer.WriteNullValue();
                break;
            case Value.KindOneofCase.BoolValue:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case Value.KindOneofCase.NumberValue:
                writer.WriteNumberValue(value.NumberValue);
                break;
            case Value.KindOneofCase.StringValue:
                writer.WriteStringValue(value.StringValue);
                break;
            case Value.KindOneofCase.StructValue:
                writer.WriteStartObject();
                foreach (var member in value.StructValue.Fields)
                {
                    writer.WritePropertyName(member.Key);
                    WriteValue(writer, member.Value);
                }
                writer.WriteEndObject();
                break;
            case Value.KindOneofCase.ListValue:
                writer.WriteStartArray();
                foreach (Value item in value.ListValue.Values)
                {
                    WriteValue(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException($"unexpected Value kind {value.KindCase}");
        }
    }

    /// <summary>
    /// Re-emit a parsed JSON document with every number routed through
    /// <see cref="Utf8JsonWriter.WriteNumberValue(double)"/>, so the reference decoder's Python
    /// float text (e.g. <c>8e-05</c>) and .NET's (<c>8E-05</c>) become the same bytes. Nothing
    /// else is touched: keys, strings, booleans, nulls and array order pass through verbatim,
    /// and two distinct doubles can never normalize to the same text.
    /// </summary>
    private static string Renumber(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, RawWriterOptions))
        {
            WriteElement(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteNumberValue(element.GetDouble());
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"unexpected JSON kind {element.ValueKind}");
        }
    }

    /// <summary>
    /// Compare two ~700 KB canonical JSON documents, reporting the first divergence with a
    /// readable window instead of dumping both documents into the failure message.
    /// </summary>
    private static void AssertSameJson(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        int i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i])
        {
            i++;
        }

        Assert.Fail(
            $"decoded tree diverges from the reference decode at offset {i} " +
            $"(expected {expected.Length} chars, got {actual.Length}):\n" +
            $"  expected: {Excerpt(expected, i)}\n" +
            $"  actual:   {Excerpt(actual, i)}");
    }

    private static string Excerpt(string s, int at)
    {
        int start = Math.Max(0, at - 60);
        int end = Math.Min(s.Length, at + 60);
        return s[start..end].Replace('\n', ' ');
    }
}
