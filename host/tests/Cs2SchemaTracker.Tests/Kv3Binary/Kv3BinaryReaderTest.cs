// Binary-KV3 reader coverage against real shipped Valve bytes, across all three KV3 versions
// CS2 has used: v3, v4 and v5.
//
// Six compiled resources sit under Kv3Binary/fixtures/, copied next to the test binary by the
// csproj, each paired with a <resource>.json so the whole decoded tree can be diffed rather
// than spot-checked.
//
// Four are KV3_V5 from build 25175329: weapons.vdata_c, the primary fixture; a .vnmclip_c that
// reaches what weapons.vdata_c cannot (a binary blob, the linked-block LZ4 path, types 7/8/12
// and 24); a stored-uncompressed .vdata_c with an empty auxiliary 8-byte buffer, the one case
// where the align(8) padding must NOT be applied; and a .vmdl_c whose all-bits-set
// m_refMeshGroupMasks is the only place signed-vs-unsigned actually shows. Their reference JSON
// comes from a Python reference decoder written before this reader existed and verified against
// 2,581 KV3_V5 blocks with zero errors, so for those four the diff is genuine ground truth.
//
// The other two are the same resource — scripts/weapons.vdata_c — at older points in CS2's
// life: v3 from build 10832117 (2023-03-22), v4 from 14470938 (2024-05-23). One logical file,
// three binary formats. 14470938 sits inside the same schema era as v5 builds, which is why
// dispatch is on the block's own magic and never on a build id or an era.
//
// Their reference JSON came from a Python prototype of the same v3/v4 derivation this reader
// implements, not an independent decoder, so it pins regressions rather than proving correctness.
// It was produced only after a sweep of all 28,343 v3/v4 blocks in 25175329's pak01 consumed
// every buffer to its declared end, and after the AK-47 decoded identically to the v5 fixture.
//
// The reader asserts internally that every cursor lands on its declared end, so any of these
// passing already means a whole block was consumed without an off-by-one. Both sides of the
// tree diff are normalized the same way first — numbers re-emitted through
// Utf8JsonWriter.WriteNumberValue(double), then the production CanonicalJson layer — so no
// value, key, array order or type is weakened by the comparison. The one thing that comparison
// does lose is an integer above 2^53, which a double cannot hold: every such token in the
// corpus is declared per fixture in that theory's InlineData, so a new one fails by name rather
// than rounding away on both sides at once.
// The loss record is pinned the same two ways. What the reader cannot carry — a repeated KV3
// key, a 64-bit integer past 2^53 — is absent from every shipped resource but the vmdl, whose
// two ulong.MaxValue values are asserted against the fixture itself; the repeated key and the
// signed case are hand-built blocks, for the same reason the allocation guards are. Each of
// those tests also asserts the TREE, because recording the loss was not allowed to change what
// the decode returns: the duplicate must still collapse last-wins and the integer must still
// arrive rounded.
// The fail-loud tests pin the deliberate refusals:
// truncated and corrupt input in every supported version, a non-KV3 magic, a container with no
// DATA block, v0/v1/v2, and zstd, each raising Kv3BinaryException naming what it found. The
// allocation guards are driven by blocks assembled byte by byte in this file rather than by
// fixtures: they state the header lies a shipped resource never tells — a count or a size the
// block's own bytes cannot justify — which is exactly what those guards refuse.

using System.Buffers.Binary;
using System.Globalization;
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
    private const string WeaponsV5 = "weapons.vdata_c";
    private const string WeaponsV4 = "weapons-v4-14470938.vdata_c";
    private const string WeaponsV3 = "weapons-v3-10832117.vdata_c";

    private const int WeaponsVdataLength = 32538;
    private const int WeaponsV4Length = 27600;
    private const int WeaponsV3Length = 22915;

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Kv3Binary", "fixtures");

    private static byte[] Fixture(string name, int expectedLength)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, name));
        Assert.Equal(expectedLength, bytes.Length);
        return bytes;
    }

    private static byte[] WeaponsVdata() => Fixture(WeaponsV5, WeaponsVdataLength);

    private static Struct DecodeWeapons() => DecodeStruct(WeaponsVdata());

    private static Struct DecodeStruct(byte[] resource)
    {
        Value root = Kv3BinaryReader.Decode(resource);
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

    [Theory]
    [InlineData(WeaponsV5, WeaponsVdataLength)]
    [InlineData(WeaponsV4, WeaponsV4Length)]
    [InlineData(WeaponsV3, WeaponsV3Length)]
    public void Decode_Is_Deterministic(string fixture, int length)
    {
        byte[] bytes = Fixture(fixture, length);
        Value first = Kv3BinaryReader.Decode(bytes);
        Value second = Kv3BinaryReader.Decode(bytes);

        // Value equality covers keys, values and types; the raw (UNSORTED) JSON additionally
        // pins member insertion order, which is what the emitters' canonical layer sorts.
        Assert.Equal(first, second);
        Assert.Equal(ToRawJson(first), ToRawJson(second));
    }

    // -----------------------------------------------------------------------------------
    // KV3 v3 and v4: the same resource, two older binary layouts
    // -----------------------------------------------------------------------------------

    [Fact]
    public void Decode_WeaponsV3_TopLevel_And_Ak47_Are_Plausible()
    {
        // KV3_V3 (build 10832117, 2023-03-22): one payload buffer, string table at its end,
        // OBJECT counts from the general 4-byte buffer, no auxiliary buffers.
        Struct root = DecodeStruct(Fixture(WeaponsV3, WeaponsV3Length));

        Assert.Equal(218, root.Fields.Count);
        Assert.Equal("CBasePlayerWeaponVData", root.Fields["generic_data_type"].StringValue);
        Assert.Equal(217, CountStructs(root));

        Struct ak = root.Fields["weapon_ak47"].StructValue;
        Assert.Equal(91, ak.Fields.Count);
        AssertAk47Constants(ak);

        // The v3 file still carries the pre-2024 model fields, which the v5 fixture no longer
        // has — the tree really is this build's, not a v5 tree read through a v3 header.
        Assert.Equal("weapons/models/ak47/weapon_rif_ak47.vmdl",
            ak.Fields["m_szViewModel"].StringValue);

        // Arrays in v3 are ARRAY_TYPED (type 10) — the count comes from the 4-byte buffer, not
        // from the byte buffer as in v4/v5. Reading them the v4 way desynchronises immediately.
        AssertNumberList(ak.Fields["m_flSpread"], 0.0006d, 0.0006d);
        AssertNumberList(ak.Fields["m_flMaxSpeed"], 215.0d, 215.0d);

        // Numeric alias keys survive as verbatim string-table lookups here too.
        Struct alias = root.Fields["1"].StructValue;
        Assert.Equal("weapon_deagle", alias.Fields["_class"].StringValue);
        Assert.Equal(53d, Number(alias, "m_nDamage"));
        Assert.Equal(1454d, Number(alias, "m_nRecoilSeed"));
    }

    [Fact]
    public void Decode_WeaponsV4_TopLevel_And_Ak47_Are_Plausible()
    {
        // KV3_V4 (build 14470938, 2024-05-23): v3's payload layout behind an 8-byte longer
        // header, and ARRAY_TYPE_BYTE_LENGTH (type 24) arrays whose count comes from the byte
        // buffer. This build is in the SAME schema era as v5 builds.
        Struct root = DecodeStruct(Fixture(WeaponsV4, WeaponsV4Length));

        Assert.Equal(219, root.Fields.Count);
        Assert.Equal("CBasePlayerWeaponVData", root.Fields["generic_data_type"].StringValue);
        Assert.Equal(218, CountStructs(root));

        Struct ak = root.Fields["weapon_ak47"].StructValue;
        Assert.Equal(92, ak.Fields.Count);
        AssertAk47Constants(ak);

        Assert.Equal("weapons/models/ak47/weapon_rif_ak47.vmdl",
            ak.Fields["m_szViewModel"].StringValue);
        AssertNumberList(ak.Fields["m_flSpread"], 0.0006d, 0.0006d);
        AssertNumberList(ak.Fields["m_flMaxSpeed"], 215.0d, 215.0d);

        Struct alias = root.Fields["1"].StructValue;
        Assert.Equal("weapon_deagle", alias.Fields["_class"].StringValue);
        Assert.Equal(53d, Number(alias, "m_nDamage"));

        // Prefabs sit alongside the concrete weapons in every version.
        Struct rifle = root.Fields["rifle"].StructValue;
        Assert.Equal(1.0d, Number(rifle, "m_flArmorRatio"));
        Assert.Equal(42d, Number(rifle, "m_nDamage"));
        Assert.False(root.Fields["statted_item_base"].StructValue.Fields.ContainsKey("_base"));
    }

    [Fact]
    public void Decode_All_Three_Versions_Agree_On_The_Ak47()
    {
        // The semantic cross-check. These three files are the same logical resource three years
        // apart in three binary formats; the AK-47 has been 36 damage / 1.55 armour ratio for
        // all of CS2's life. A v3 or v4 decode that produced garbage floats or a key set with
        // nothing in common with v5's would be wrong even though it threw nothing.
        Struct v3 = DecodeStruct(Fixture(WeaponsV3, WeaponsV3Length)).Fields["weapon_ak47"].StructValue;
        Struct v4 = DecodeStruct(Fixture(WeaponsV4, WeaponsV4Length)).Fields["weapon_ak47"].StructValue;
        Struct v5 = DecodeWeapons().Fields["weapon_ak47"].StructValue;

        foreach (Struct ak in new[] { v3, v4, v5 })
        {
            AssertAk47Constants(ak);
        }

        // Field names drift as the schema evolves, but the overlap must stay overwhelming.
        var v5Keys = new HashSet<string>(v5.Fields.Keys, StringComparer.Ordinal);
        foreach (Struct older in new[] { v3, v4 })
        {
            int shared = older.Fields.Keys.Count(v5Keys.Contains);
            Assert.True(
                shared >= 75,
                $"only {shared} of {older.Fields.Count} keys are shared with the v5 decode; " +
                $"a v3/v4 tree that looks nothing like the v5 one is a misparse");
        }
    }

    /// <summary>
    /// The AK-47 stats that have not moved across CS2's life. Asserted identically on every
    /// version's fixture, so a version-specific misparse cannot hide behind "values drift".
    /// </summary>
    private static void AssertAk47Constants(Struct ak)
    {
        Assert.Equal(36d, Number(ak, "m_nDamage"));
        Assert.Equal(1.55d, Number(ak, "m_flArmorRatio"));
        Assert.Equal(223d, Number(ak, "m_nRecoilSeed"));
        Assert.Equal(4.0d, Number(ak, "m_flHeadshotMultiplier"));
        Assert.Equal(2.0d, Number(ak, "m_flPenetration"));
        Assert.Equal(8192.0d, Number(ak, "m_flRange"));
        Assert.Equal(0.98d, Number(ak, "m_flRangeModifier"));
        Assert.Equal(300d, Number(ak, "m_nKillAward"));
        Assert.Equal(0.368d, Number(ak, "m_flRecoveryTimeStand"));

        // A bare DOUBLE, NOT a one-element array — the distinction is exactly what a
        // desynchronised walk would get wrong first.
        Assert.Equal(Value.KindOneofCase.NumberValue, ak.Fields["m_flCycleTime"].KindCase);
        Assert.Equal(0.1d, ak.Fields["m_flCycleTime"].NumberValue);

        AssertNumberList(ak.Fields["m_flSpread"], 0.0006d, 0.0006d);
        AssertNumberList(ak.Fields["m_flInaccuracyStand"], 0.00641d, 0.00641d);

        Assert.Equal("weapon_ak47", ak.Fields["_class"].StringValue);
        Assert.Equal("weapon_ak47_prefab", ak.Fields["_base"].StringValue);

        // A nested OBJECT, whose member count is the one thing v5 reads from a dedicated
        // buffer and v3/v4 read from the general 4-byte buffer.
        Struct sounds = ak.Fields["m_aShootSounds"].StructValue;
        Assert.Equal("Weapon_AK47.Single", sounds.Fields["WEAPON_SOUND_SINGLE"].StringValue);
    }

    private static int CountStructs(Struct root)
    {
        int objects = 0;
        foreach (Value entry in root.Fields.Values)
        {
            if (entry.KindCase == Value.KindOneofCase.StructValue)
            {
                objects++;
            }
        }
        return objects;
    }

    // -----------------------------------------------------------------------------------
    // whole-tree diff against the Python reference decode
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("weapons.vdata_c", 500_000, "")]
    [InlineData("pistol_jump_crouch_w_pistol.vnmclip_c", 50_000, "")]
    [InlineData("soundeventgroups.vdata_c", 100, "")]
    [InlineData(
        "aztec_skybox_tree_card_large_01.vmdl_c",
        1_000,
        "18446744073709551615,18446744073709551615")]
    [InlineData(WeaponsV3, 500_000, "")]
    [InlineData(WeaponsV4, 500_000, "")]
    public void Decode_Matches_The_Reference_Decode_Exactly(
        string fixture, int minimumLength, string roundedIntegers)
    {
        Value root = Kv3BinaryReader.Decode(File.ReadAllBytes(Path.Combine(FixtureDir, fixture)));
        string actual = CanonicalJson.SerializeRawJson(ToRawJson(root));

        string referenceJson = File.ReadAllText(Path.Combine(FixtureDir, fixture + ".json"));
        using var reference = JsonDocument.Parse(referenceJson);
        (string renumbered, IReadOnlyList<string> rounded) = Renumber(reference.RootElement);
        string expected = CanonicalJson.SerializeRawJson(renumbered);

        // The reference side is re-emitted through a double, which cannot hold every integer
        // the format can carry, so each token that loses precision has to be named here, once
        // per occurrence. The whole corpus contains exactly two, both ulong.MaxValue and both
        // in the vmdl: m_nDefaultMeshGroupMask and m_refMeshGroupMasks[0]. A new one — a
        // regenerated reference, a new fixture, an emitter that starts feeding large integers
        // through — then fails this test instead of disappearing into a comparison that rounds
        // both sides the same way.
        Assert.Equal(roundedIntegers.Split(',', StringSplitOptions.RemoveEmptyEntries), rounded);

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

    [Theory]
    [InlineData(WeaponsV3, WeaponsV3Length)]
    [InlineData(WeaponsV4, WeaponsV4Length)]
    public void Decode_Truncated_Older_Resource_Throws(string fixture, int length)
    {
        byte[] full = Fixture(fixture, length);

        // Chop the DATA payload mid-way. The container header and the block table still parse,
        // so the failure has to come from the KV3 layer — as a named Kv3BinaryException, never
        // as an IndexOutOfRange from a slice that ran off the end.
        Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(full[..(full.Length / 2)]));

        // Chop to just past the container header: the DATA block no longer fits at all.
        var chopped = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(full[..64]));
        Assert.Contains("DATA block spans", chopped.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WeaponsV3, WeaponsV3Length, 3, 64)]
    [InlineData(WeaponsV4, WeaponsV4Length, 4, 72)]
    public void Decode_Corrupt_Older_Payload_Throws(string fixture, int length, byte version, int headerSize)
    {
        // The container and the v3/v4 header stay intact; only the single compressed payload
        // that follows the header is clobbered. A zeroed LZ4 stream decodes its first token as
        // "0 literals, match offset 0", which is unrepresentable.
        byte[] corrupt = Fixture(fixture, length);
        int dataStart = DataBlockOffset(corrupt);
        Assert.Equal(version, corrupt[dataStart]);   // the fixture really is this version
        Array.Fill(corrupt, (byte)0, dataStart + headerSize, 256);

        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(corrupt));
        Assert.Contains("Lz4Block", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WeaponsV3, WeaponsV3Length)]
    [InlineData(WeaponsV4, WeaponsV4Length)]
    public void Decode_Older_Resource_With_A_Falsified_Header_Count_Throws(string fixture, int length)
    {
        // The payload decompresses cleanly; only the header lies about how the buffers inside
        // it are laid out. Every cursor is bounded by its DECLARED end and the whole-buffer
        // consumption check runs unconditionally, so the walk cannot quietly produce a wrong
        // tree — it has to name what did not line up.
        byte[] corrupt = Fixture(fixture, length);
        int dataStart = DataBlockOffset(corrupt);

        // Offset 36 is the 8-byte value count; inflating it shifts the string + types area.
        uint eight = BinaryPrimitives.ReadUInt32LittleEndian(corrupt.AsSpan(dataStart + 36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(dataStart + 36, 4), eight + 1);

        Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(corrupt));
    }

    [Theory]
    [InlineData(3, 64)]
    [InlineData(4, 72)]
    public void Decode_Older_Block_Shorter_Than_Its_Header_Throws(int version, int headerSize)
    {
        byte[] resource = BuildContainer(
            ("DATA", MakeBlock((byte)version, 0x33, 0x56, 0x4B).AsSpan(0, headerSize - 1).ToArray()));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains($"KV3 v{version} DATA block is", ex.Message, StringComparison.Ordinal);
        Assert.Contains("shorter than", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Decode_Older_Zstd_Compression_Is_Refused_By_Name(int version)
    {
        byte[] block = MakeBlock((byte)version, 0x33, 0x56, 0x4B);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20, 4), 2u);   // compressionMethod
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(48, 4), 64u);  // uncompressed size
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(52, 4), 16u);  // compressed size

        byte[] resource = BuildContainer(("DATA", block));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains("unsupported compression method 2 (zstd)", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"KV3 v{version} DATA block", ex.Message, StringComparison.Ordinal);
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
    public void Decode_Unmeasured_Kv3_Versions_Are_Refused_By_Name(int version)
    {
        // v1 and v2 lay their headers out differently again, and neither occurs anywhere in the
        // measured corpus — there is nothing to verify an implementation against, so they stay
        // named and refused rather than guessed at.
        byte[] resource = BuildContainer(("DATA", MakeBlock((byte)version, 0x33, 0x56, 0x4B)));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains($"unsupported KV3 version {version}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("only v3, v4 and v5 are implemented", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_Legacy_Vkv3_Block_Is_Refused_By_Name()
    {
        byte[] resource = BuildContainer(("DATA", MakeBlock(0x56, 0x4B, 0x56, 0x03)));
        var ex = Assert.Throws<Kv3BinaryException>(() => Kv3BinaryReader.Decode(resource));
        Assert.Contains("unsupported KV3 version 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("only v3, v4 and v5 are implemented", ex.Message, StringComparison.Ordinal);
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
    // allocation and bounds guards
    // -----------------------------------------------------------------------------------
    //
    // Every block in this section is assembled in code rather than shipped as a fixture,
    // because these are the header lies a real resource never tells: a count or a size that the
    // bytes the block actually carries cannot possibly justify. The reader's rule is that such
    // a field is bounded BEFORE anything is allocated from it, so a corrupt or truncated depot
    // file fails as a named Kv3BinaryException — the type WeaponVDataEmitter catches — instead
    // of exhausting the extract host or escaping as an OutOfMemoryException or an
    // IndexOutOfRangeException that nothing catches.

    [Fact]
    public void Decode_TypedArray_With_An_Impossible_Element_Count_Is_Refused_By_Name()
    {
        // ARRAY_TYPED reads ONE shared element tag and then loops the declared count, so when
        // that tag is a zero-width type (13 = BOOLEAN_TRUE here) nothing the loop does advances
        // any cursor and no per-buffer guard can bound it. Until the count was checked against
        // the payload this 134-byte block decoded with NO exception at all, passed
        // RequireFullyConsumed, and handed back a 1,000,000-element ListValue.
        var ex = Assert.Throws<Kv3BinaryException>(
            () => Kv3BinaryReader.DecodeBlock(TypedArrayBlock(1_000_000)));
        Assert.Contains("ARRAY_TYPED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("declares 1000000 elements", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_TypedArray_Of_ZeroWidth_Elements_Still_Decodes()
    {
        // The other half of the same guard: a genuine typed array of a zero-width type spends
        // no payload bytes at all and must keep decoding. This pins the bound against being
        // tightened to something a real block could trip.
        Value root = Kv3BinaryReader.DecodeBlock(TypedArrayBlock(3));
        Assert.Equal(Value.KindOneofCase.ListValue, root.KindCase);
        Assert.Equal(3, root.ListValue.Values.Count);
        foreach (Value element in root.ListValue.Values)
        {
            Assert.Equal(Value.KindOneofCase.BoolValue, element.KindCase);
            Assert.True(element.BoolValue);
        }
    }

    [Fact]
    public void Decode_V5_Blob_Frame_Table_Larger_Than_The_Buffer_Is_Refused_By_Name()
    {
        // The v5 header word at offset 68 sizes an int[] at one element per two declared bytes.
        // Nothing compared it to the buffer those bytes have to come out of, so the array was
        // allocated first and the per-entry bounds check only ran afterwards. 100,000,000 is
        // far below the int.MaxValue the field accepts — it sizes a 50,000,000-element int[]
        // and no more, which keeps the cost of this test bounded on a reader without the guard.
        var ex = Assert.Throws<Kv3BinaryException>(
            () => Kv3BinaryReader.DecodeBlock(TypedArrayBlock(3, blobFrameTableSize: 100_000_000)));
        Assert.Contains("blob frame table", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_V5_Segment_Declaring_An_Impossible_Uncompressed_Size_Is_Refused_By_Name()
    {
        // The segment's uncompressed-size word is range-checked against int.MaxValue and
        // nothing else, and the RequireRange beside it bounds only the COMPRESSED slice — so
        // that word drove the destination allocation on its own. Here a 5-byte LZ4 block that
        // produces 4 bytes claims 50,000: without the expansion ceiling the 50,000 bytes were
        // reserved first and the mismatch was reported only afterwards.
        byte[] segment1 = TypedArraySegment1(3);
        var block = new V5Block
        {
            CompressionMethod = 1,
            Segment0Bytes = [0x40, 0x00, 0x00, 0x00, 0x00],
            Segment0Uncompressed = 50_000,
            Segment1Bytes = Lz4Literals(segment1),
            Segment1Uncompressed = segment1.Length,
            IntCount = 1,
            TypesSize = 2,
        };

        var ex = Assert.Throws<Kv3BinaryException>(
            () => Kv3BinaryReader.DecodeBlock(block.ToBytes()));
        Assert.Contains("can possibly expand to", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_V5_Blob_Total_Beyond_What_The_Frames_Can_Produce_Is_Refused_By_Name()
    {
        // blockTotalSize sizes the blob payload buffer, and under LZ4 the only thing that can
        // fill it is the frame table that follows the trailer. One 4-byte frame cannot produce
        // 50,000,000 bytes, but the buffer was allocated before anything said so.
        byte[] segment1 =
        [
            0x01,                         // types: a NULL root
            0x80, 0xF0, 0xFA, 0x02,       // blob length list: one blob of 50,000,000 bytes
            0x00, 0xDD, 0xEE, 0xFF,       // buffer-area trailer
            0x04, 0x00,                   // frame-size table: one 4-byte LZ4 frame
        ];
        var block = new V5Block
        {
            CompressionMethod = 1,
            Segment0Bytes = [0x40, 0x00, 0x00, 0x00, 0x00],
            Segment0Uncompressed = 4,
            Segment1Bytes = Lz4Literals(segment1),
            Segment1Uncompressed = segment1.Length,
            Trailing = [0x00, 0x00, 0x00, 0x00],
            TypesSize = 1,
            BlobCount = 1,
            BlobTotalSize = 50_000_000,
            BlobFrameTableSize = 2,
        };

        var ex = Assert.Throws<Kv3BinaryException>(
            () => Kv3BinaryReader.DecodeBlock(block.ToBytes()));
        Assert.Contains("LZ4 blob frame", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_V5_Aux_Align8_Padding_Is_Conditional_On_A_NonEmpty_EightByte_Buffer()
    {
        // The align(8) before segment 0's auxiliary 8-byte buffer is materialised ONLY when
        // that buffer is non-empty. soundeventgroups.vdata_c cannot hold that branch down: its
        // auxiliary 4-byte area already ends at 104, which is 8-aligned, so applying the
        // padding unconditionally would decode it identically. This block ends that area at 12
        // — 4 mod 8 — so the conditional is the only thing keeping the layout honest: applied
        // unconditionally, the unpadded block computes an end past its own segment and the
        // padded one, which no encoder emits, starts decoding.
        Struct root = Kv3BinaryReader.DecodeBlock(AuxAlignBlock(padSegment0: false)).StructValue;
        Value list = root.Fields["a"];
        Assert.Equal(Value.KindOneofCase.ListValue, list.KindCase);
        Assert.Equal(7d, Assert.Single(list.ListValue.Values).NumberValue);

        var ex = Assert.Throws<Kv3BinaryException>(
            () => Kv3BinaryReader.DecodeBlock(AuxAlignBlock(padSegment0: true)));
        Assert.Contains(
            "SEG0 layout computes an end of 12 but the segment is 16 bytes",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Lz4_Declared_Output_Beyond_The_Expansion_Ceiling_Is_Refused_By_Name()
    {
        // Nothing related the declared output length to the block that has to produce it, so
        // the destination was allocated from a container header field alone — and a value near
        // int.MaxValue is past the CLR's array cap, which escapes as an OutOfMemoryException.
        var ex = Assert.Throws<Kv3BinaryException>(() => Lz4Block.Decode(new byte[8], 100_000));
        Assert.Contains("can possibly expand to", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lz4_High_Ratio_Block_Still_Decodes()
    {
        // 6 input bytes to 275 output bytes, a 45.8x expansion: well inside the 255x ceiling,
        // and its single 0xFF match-length extension runs to 270 against a limit of 274. Both
        // new bounds are one step from firing here, which is the point — a ceiling picked too
        // tight, or an off-by-one in the extension limit, fails this test rather than a fixture.
        byte[] block = [0x1F, (byte)'a', 0x01, 0x00, 0xFF, 0x00];
        byte[] decoded = Lz4Block.Decode(block, 275);
        Assert.Equal(new string('a', 275), Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void Lz4_Runaway_Match_Length_Extension_Is_Refused_By_Name()
    {
        // A 0xFF extension run accumulated into an int with no cap: 8,500,000 of them reach
        // 2,167,500,015, which wraps negative, and the overrun guard below it wrapped in step
        // and PASSED. The copy loop was then skipped, outPos went negative, and the following
        // sequence indexed the destination at a negative offset — an ArgumentOutOfRangeException
        // that WeaponVDataEmitter's catch (Kv3BinaryException) does not catch. ~8.4M extension
        // bytes is the arithmetic minimum to overflow the accumulator, so this block cannot be
        // made materially smaller; with the limit in place the first extension byte ends it.
        var block = new byte[4 + 8_500_000 + 4];
        block[0] = 0x1F;              // 1 literal, match-length nibble saturated
        block[1] = (byte)'a';
        block[2] = 0x01;              // match offset 1
        block[3] = 0x00;
        Array.Fill(block, (byte)0xFF, 4, 8_500_000);
        block[4 + 8_500_000] = 0x00;  // ends the extension run
        block[5 + 8_500_000] = 0x20;  // 2 literals
        block[6 + 8_500_000] = (byte)'x';
        block[7 + 8_500_000] = (byte)'y';

        Assert.Throws<Kv3BinaryException>(() => Lz4Block.Decode(block, 64));
    }

    [Fact]
    public void Reference_Renumber_Reports_An_Integer_A_Double_Cannot_Hold()
    {
        // 2^53+1 is the smallest positive integer a double cannot represent. The reference side
        // of the whole-tree diff rounds it silently, so before Renumber reported the loss these
        // two documents normalized to identical text and the diff could not tell them apart —
        // which is why an int64/uint64 above 2^53 could never make that comparison fail.
        using var document = JsonDocument.Parse(
            """{"lossy":9007199254740993,"exact":9007199254740992}""");
        (string json, IReadOnlyList<string> rounded) = Renumber(document.RootElement);

        Assert.Equal(["9007199254740993"], rounded);
        Assert.Equal("""{"lossy":9007199254740992,"exact":9007199254740992}""", json);
    }

    // -----------------------------------------------------------------------------------
    // what the decode could not carry
    // -----------------------------------------------------------------------------------

    [Fact]
    public void DecodeWithLoss_Records_A_Repeated_Key_And_Still_Collapses_It_LastWins()
    {
        (Value root, Kv3DecodeLoss loss) = Kv3BinaryReader.DecodeBlockWithLoss(DuplicateKeyBlock());

        // The tree is the one this block has always produced: a Struct cannot hold two members
        // under one key, so the second value wins and the first is gone. Recording the loss was
        // not licence to change that — every other consumer still gets these exact bytes.
        var only = Assert.Single(root.StructValue.Fields);
        Assert.Equal("a", only.Key);
        Assert.Equal(1d, only.Value.NumberValue);

        // ... and the old entry point, which nothing had to touch, still returns it too.
        Assert.Equal(
            1d, Kv3BinaryReader.DecodeBlock(DuplicateKeyBlock()).StructValue.Fields["a"].NumberValue);

        // The difference: the collapse is now visible to a caller that cares.
        Assert.Equal(["a"], loss.DuplicateKeys);
        Assert.Empty(loss.NarrowedIntegers);
        Assert.False(loss.IsLossless);
    }

    [Fact]
    public void DecodeWithLoss_Records_Each_Int64_A_Double_Cannot_Hold()
    {
        // 2^53+1 is the smallest positive integer a double cannot represent; 2^53 is the
        // largest it can. Both are recorded or not on the same round trip the reference side of
        // the whole-tree diff uses, so the two sides cannot drift apart in what they call lossy.
        (Value root, Kv3DecodeLoss loss) = Kv3BinaryReader.DecodeBlockWithLoss(
            Int64ArrayBlock(9007199254740993L, 9007199254740992L, -9007199254740993L));

        // Narrowing behaviour is untouched: the tree still carries the rounded doubles, and the
        // lossy value is still indistinguishable in it from the exact one beside it.
        AssertNumberList(root, 9007199254740992d, 9007199254740992d, -9007199254740992d);

        // The record is what tells them apart. It is per OCCURRENCE and in tree order, and the
        // sign is carried: a magnitude computed the lazy way turns long.MinValue into itself.
        Assert.Equal(["9007199254740993", "-9007199254740993"], loss.NarrowedIntegers);
        Assert.Empty(loss.DuplicateKeys);
        Assert.False(loss.IsLossless);
    }

    [Fact]
    public void DecodeWithLoss_Records_Both_Uint64s_The_Vmdl_Already_Ships()
    {
        // The reason the reader records instead of throwing. This is a resource Valve shipped:
        // m_nDefaultMeshGroupMask is a plain UINT64 and m_refMeshGroupMasks[0] a UINT64 in the
        // segment-0 auxiliary buffer, so the two narrowing sites are BOTH exercised here, and
        // both values are all-bits-set, which no double holds. A reader that refused at the
        // narrowing site could not read this file at all.
        byte[] bytes = File.ReadAllBytes(
            Path.Combine(FixtureDir, "aztec_skybox_tree_card_large_01.vmdl_c"));
        (Value root, Kv3DecodeLoss loss) = Kv3BinaryReader.DecodeWithLoss(bytes);

        Assert.Equal(
            (double)ulong.MaxValue,
            root.StructValue.Fields["m_refMeshGroupMasks"].ListValue.Values[0].NumberValue);

        // The same two tokens Decode_Matches_The_Reference_Decode_Exactly declares for this
        // fixture, which is the point: the production reader and the reference-side round trip
        // agree on exactly which values the corpus cannot carry.
        Assert.Equal(
            ["18446744073709551615", "18446744073709551615"], loss.NarrowedIntegers);
        Assert.Empty(loss.DuplicateKeys);
    }

    [Theory]
    [InlineData("weapons.vdata_c")]
    [InlineData("pistol_jump_crouch_w_pistol.vnmclip_c")]
    [InlineData("soundeventgroups.vdata_c")]
    [InlineData(WeaponsV3)]
    [InlineData(WeaponsV4)]
    public void DecodeWithLoss_Reports_No_Loss_For_The_Fixtures_That_Lose_Nothing(string fixture)
    {
        // The other half of the record. A loss list that filled up on ordinary blocks would be
        // useless to a caller that fails loud on it, and the vmdl above is the ONLY fixture in
        // the suite that puts anything in one.
        (_, Kv3DecodeLoss loss) = Kv3BinaryReader.DecodeWithLoss(
            File.ReadAllBytes(Path.Combine(FixtureDir, fixture)));

        Assert.Empty(loss.DuplicateKeys);
        Assert.Empty(loss.NarrowedIntegers);
        Assert.True(loss.IsLossless);
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

    /// <summary>
    /// A hand-assembled KV3 v5 block. Every field below is written into the header verbatim,
    /// INCLUDING sizes that contradict the bytes the block actually carries — which is the
    /// whole point: no shipped resource lies about its own geometry, and the fixtures are
    /// read-only, so the lying blocks the allocation guards refuse have to be stated in code.
    /// Segment bytes are supplied exactly as they appear in the DATA block, i.e. already LZ4
    /// encoded when <see cref="CompressionMethod"/> is 1.
    /// </summary>
    private sealed class V5Block
    {
        public byte[] Segment0Bytes { get; init; } = [];
        public byte[] Segment1Bytes { get; init; } = [];

        /// <summary>The blob frame payload region, which follows both stored segments.</summary>
        public byte[] Trailing { get; init; } = [];

        public uint CompressionMethod { get; init; }
        public int StringBlobSize { get; init; }
        public int AuxSlotCount { get; init; } = 1;
        public int AuxEightByteCount { get; init; }
        public int TypesSize { get; init; }
        public int BlobCount { get; init; }
        public int BlobTotalSize { get; init; }
        public int BlobFrameTableSize { get; init; }

        /// <summary>
        /// Declared SEG0 uncompressed size; -1 means "however many bytes SEG0 holds", which is
        /// right for a stored segment and has to be stated explicitly for an LZ4 one.
        /// </summary>
        public int Segment0Uncompressed { get; init; } = -1;

        /// <summary>Declared SEG1 uncompressed size; -1 as for <see cref="Segment0Uncompressed"/>.</summary>
        public int Segment1Uncompressed { get; init; } = -1;

        public int ByteCount { get; init; }
        public int IntCount { get; init; }
        public int EightByteCount { get; init; }
        public int ObjectCount { get; init; }

        public byte[] ToBytes()
        {
            int seg0Unc = Segment0Uncompressed >= 0 ? Segment0Uncompressed : Segment0Bytes.Length;
            int seg1Unc = Segment1Uncompressed >= 0 ? Segment1Uncompressed : Segment1Bytes.Length;

            var block = new byte[
                120 + Segment0Bytes.Length + Segment1Bytes.Length + Trailing.Length];
            block[0] = 0x05;
            block[1] = 0x33;
            block[2] = 0x56;
            block[3] = 0x4B;
            Word(block, 20, CompressionMethod);
            Word(block, 28, StringBlobSize);
            Word(block, 32, AuxSlotCount);
            Word(block, 36, AuxEightByteCount);
            Word(block, 40, TypesSize);
            Word(block, 48, (long)seg0Unc + seg1Unc);
            Word(block, 56, BlobCount);
            Word(block, 60, BlobTotalSize);
            Word(block, 68, BlobFrameTableSize);
            Word(block, 72, seg0Unc);
            // v5 zeroes the per-segment compressed size when the segment is stored verbatim.
            Word(block, 76, CompressionMethod == 0 ? 0 : Segment0Bytes.Length);
            Word(block, 80, seg1Unc);
            Word(block, 84, CompressionMethod == 0 ? 0 : Segment1Bytes.Length);
            Word(block, 88, ByteCount);
            Word(block, 96, IntCount);
            Word(block, 100, EightByteCount);
            Word(block, 108, ObjectCount);

            int cursor = 120;
            Segment0Bytes.CopyTo(block.AsSpan(cursor));
            cursor += Segment0Bytes.Length;
            Segment1Bytes.CopyTo(block.AsSpan(cursor));
            cursor += Segment1Bytes.Length;
            Trailing.CopyTo(block.AsSpan(cursor));
            return block;

            static void Word(byte[] target, int offset, long value) =>
                BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), (uint)value);
        }
    }

    /// <summary>
    /// Segment 1 of a block whose whole tree is one ARRAY_TYPED of <paramref name="count"/>
    /// BOOLEAN_TRUE elements: the count in the 4-byte buffer, the array tag and its ONE shared
    /// element tag in the types buffer, then the trailer. Nothing here grows with the count —
    /// which is exactly why the count needs a bound of its own.
    /// </summary>
    private static byte[] TypedArraySegment1(uint count)
    {
        byte[] segment1 = [0, 0, 0, 0, 0x0A, 0x0D, 0x00, 0xDD, 0xEE, 0xFF];
        BinaryPrimitives.WriteUInt32LittleEndian(segment1.AsSpan(0, 4), count);
        return segment1;
    }

    /// <summary>
    /// A complete 134-byte stored-uncompressed v5 block around
    /// <see cref="TypedArraySegment1"/>. Segment 0 is the four bytes of a zero string count.
    /// </summary>
    private static byte[] TypedArrayBlock(uint count, int blobFrameTableSize = 0) =>
        new V5Block
        {
            Segment0Bytes = [0x00, 0x00, 0x00, 0x00],
            Segment1Bytes = TypedArraySegment1(count),
            IntCount = 1,
            TypesSize = 2,
            BlobFrameTableSize = blobFrameTableSize,
        }.ToBytes();

    /// <summary>
    /// A stored-uncompressed v5 block decoding to <c>{ "a": [7] }</c>, whose auxiliary 4-byte
    /// area ends at 12 — 4 mod 8 — so the conditional align(8) in front of the (empty) auxiliary
    /// 8-byte buffer is load-bearing. <paramref name="padSegment0"/> appends the four padding
    /// bytes an unconditional align(8) would expect, which no encoder emits.
    /// </summary>
    private static byte[] AuxAlignBlock(bool padSegment0)
    {
        // "a\0", the align(4) pad, the string count, then the single auxiliary 4-byte value.
        var segment0 = new byte[padSegment0 ? 16 : 12];
        segment0[0] = (byte)'a';
        BinaryPrimitives.WriteUInt32LittleEndian(segment0.AsSpan(4, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(segment0.AsSpan(8, 4), 7u);

        // The object's member count, the byte buffer holding the type-25 element count, the
        // align(4) pad at 5..8, string id 0 (the key "a") at 8..12, three type tags, trailer.
        var segment1 = new byte[19];
        BinaryPrimitives.WriteUInt32LittleEndian(segment1.AsSpan(0, 4), 1u);
        segment1[4] = 0x01;
        segment1[12] = 0x09;   // OBJECT
        segment1[13] = 0x19;   // ARRAY_TYPE_AUXILIARY_BUFFER
        segment1[14] = 0x0B;   // INT32
        segment1[15] = 0x00;
        segment1[16] = 0xDD;
        segment1[17] = 0xEE;
        segment1[18] = 0xFF;

        return new V5Block
        {
            Segment0Bytes = segment0,
            Segment1Bytes = segment1,
            StringBlobSize = 2,
            AuxSlotCount = 2,
            ObjectCount = 1,
            ByteCount = 1,
            IntCount = 1,
            TypesSize = 3,
        }.ToBytes();
    }

    /// <summary>
    /// A stored-uncompressed v5 block decoding to <c>{ "a": 1 }</c> from an OBJECT that carries
    /// the key "a" TWICE — string id 0 both times, INT64_ZERO then INT64_ONE, neither of which
    /// spends a byte of any buffer. No shipped resource repeats a key, so the collapse the
    /// reader records has to be stated in code; the two zero-width values make the SECOND one
    /// identifiable in the tree, which is how last-wins is asserted rather than assumed.
    /// </summary>
    private static byte[] DuplicateKeyBlock()
    {
        // "a\0", the align(4) pad, then the string count.
        var segment0 = new byte[8];
        segment0[0] = (byte)'a';
        BinaryPrimitives.WriteUInt32LittleEndian(segment0.AsSpan(4, 4), 1u);

        // The object member count, the two string ids in the 4-byte buffer, three type tags,
        // trailer. Both ids are 0, which is the string "a".
        var segment1 = new byte[19];
        BinaryPrimitives.WriteUInt32LittleEndian(segment1.AsSpan(0, 4), 2u);
        segment1[12] = 0x09;   // OBJECT
        segment1[13] = 0x0F;   // INT64_ZERO
        segment1[14] = 0x10;   // INT64_ONE
        segment1[15] = 0x00;
        segment1[16] = 0xDD;
        segment1[17] = 0xEE;
        segment1[18] = 0xFF;

        return new V5Block
        {
            Segment0Bytes = segment0,
            Segment1Bytes = segment1,
            StringBlobSize = 2,
            AuxSlotCount = 1,
            ObjectCount = 1,
            IntCount = 2,
            TypesSize = 3,
        }.ToBytes();
    }

    /// <summary>
    /// A stored-uncompressed v5 block whose whole tree is an ARRAY of the given INT64 values,
    /// each tagged INT64 individually so the array is the ordinary type-8 kind rather than a
    /// typed one. The 8-byte buffer is where an INT64 actually lives, which is the cursor the
    /// narrowing sits on.
    /// </summary>
    private static byte[] Int64ArrayBlock(params long[] values)
    {
        // The array element count, the align(8) pad, then the values themselves.
        var segment1 = new byte[8 + (values.Length * 8) + 1 + values.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(segment1.AsSpan(0, 4), (uint)values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(segment1.AsSpan(8 + (i * 8), 8), values[i]);
        }

        int types = 8 + (values.Length * 8);
        segment1[types] = 0x08;                        // ARRAY
        for (int i = 0; i < values.Length; i++)
        {
            segment1[types + 1 + i] = 0x03;            // INT64
        }

        int trailer = types + 1 + values.Length;
        segment1[trailer] = 0x00;
        segment1[trailer + 1] = 0xDD;
        segment1[trailer + 2] = 0xEE;
        segment1[trailer + 3] = 0xFF;

        return new V5Block
        {
            Segment0Bytes = [0x00, 0x00, 0x00, 0x00],
            Segment1Bytes = segment1,
            AuxSlotCount = 1,
            IntCount = 1,
            EightByteCount = values.Length,
            TypesSize = 1 + values.Length,
        }.ToBytes();
    }

    /// <summary>
    /// Wrap bytes as a raw LZ4 block of pure literals — the identity encoding, and the only one
    /// that can be written by hand for an arbitrary payload.
    /// </summary>
    private static byte[] Lz4Literals(byte[] payload)
    {
        var block = new List<byte>();
        if (payload.Length < 15)
        {
            block.Add((byte)(payload.Length << 4));
        }
        else
        {
            block.Add(0xF0);
            int remaining = payload.Length - 15;
            while (remaining >= 0xFF)
            {
                block.Add(0xFF);
                remaining -= 0xFF;
            }
            block.Add((byte)remaining);
        }
        block.AddRange(payload);
        return [.. block];
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
    /// <para>
    /// What it DOES lose is an integer above 2^53, and it used to lose it in silence: such a
    /// token came back out of <see cref="JsonElement.GetDouble"/> as the nearest double, so a
    /// correctly decoded int64/uint64 and a mangled one normalized to the same bytes and the
    /// whole-tree diff could not tell them apart. Every rounded token is now returned alongside
    /// the JSON for the caller to declare. The diff still compares doubles, because
    /// Value.NumberValue IS a double and the reader is documented to produce nothing else.
    /// </para>
    /// </summary>
    private static (string Json, IReadOnlyList<string> RoundedIntegers) Renumber(JsonElement element)
    {
        var rounded = new List<string>();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, RawWriterOptions))
        {
            WriteElement(writer, element, rounded);
        }
        return (Encoding.UTF8.GetString(stream.ToArray()), rounded);
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, List<string> rounded)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value, rounded);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteElement(writer, item, rounded);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, element, rounded);
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
    /// Write one reference number as a double, recording its raw text first when it is an
    /// integer token that does not survive the round trip — the only thing this normalization
    /// loses, and the reason the loss is reported rather than absorbed.
    /// </summary>
    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element, List<string> rounded)
    {
        string raw = element.GetRawText();
        double value = element.GetDouble();
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0 &&
            !string.Equals(
                value.ToString("F0", CultureInfo.InvariantCulture), raw, StringComparison.Ordinal))
        {
            rounded.Add(raw);
        }
        writer.WriteNumberValue(value);
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
