// Binary KV3 (Valve KeyValues v3, COMPILED form) reader for Source 2 `*_c` resources.
// Supports KV3 v3, v4 and v5 — the three versions CS2 has shipped.
//
// === Why this exists ===
// EntitySchema/Kv3.cs parses the KV3 *text* grammar Valve emits into schema metadata. The
// content depot ships a different thing: compiled resources such as `scripts/weapons.vdata_c`
// whose DATA block is the same key/value tree encoded as a compressed, column-oriented binary
// blob. Nothing in the text parser reads it. This file is the binary side, hand-rolled for the
// same reasons VpkArchive is (independence — no ValveResourceFormat runtime dependency; all
// fail-loud paths in our own code; host\Directory.Packages.props unchanged — see Lz4Block.cs).
//
// === Which version is where ===
// The block's own 4-byte magic decides, and NOTHING else does — the version boundary does not
// line up with build ids or schema eras. Measured on `scripts/weapons.vdata_c` across the
// corpus: v3 in 2023-03, v4 from 2023-09 through some point inside the long 2024-06 era, v5
// from 2025-03 onwards. All three use LZ4; zstd has never appeared.
//
// === Output shape ===
// Mirrors EntitySchema/Kv3.cs exactly, so both KV3 parsers hand the emitters the same
// structural google.protobuf.Value:
//   - map / object -> Value.StructValue   (keys verbatim, inserted in SOURCE order)
//   - array        -> Value.ListValue
//   - int / real   -> Value.NumberValue   (both become a proto double)
//   - string       -> Value.StringValue
//   - bool         -> Value.BoolValue
//   - null         -> Value.NullValue
//   - binary blob  -> Value.StringValue holding standard base64 (see "Binary blobs" below)
//
// === Container: the compiled resource wrapper ===
// CONFIRMED against real bytes. Shared by every Source 2 `*_c` file and by every KV3 version:
//
//   0    u32   fileSize
//   4    u16   headerVersion            (12 for CS2)
//   6    u16   resourceVersion          (0)
//   8    u32   blockOffset              -- relative to offset 8
//   12   u32   blockCount
//   8+blockOffset: blockCount x 12-byte entries
//       +0   char[4]  block name: "RED2", "DATA", "FLCI", "NTRO", ...
//       +4   u32      dataOffset  -- relative to the address of THIS field
//       +8   u32      dataSize
//
// Only DATA matters here; it holds the KV3 blob.
//
// === KV3 v5 block header (offsets relative to the start of DATA) ===
// CONFIRMED. Values in the right column are the 2026 weapons.vdata_c's.
//
//    0   4  magic 05 33 56 4B                                 KV3_V5
//    4  16  format GUID (generic KV3; does not change layout)  7412167c-06e9-...
//   20   4  compressionMethod   0 = none, 1 = LZ4, 2 = zstd    1
//   24   2  compressionDictionaryId                            0
//   26   2  compressionFrameSize (blob frames only)            16384
//   28   4  SEG0 string blob size, bytes                       14891
//   32   4  SEG0 4-byte slot count (first slot = stringCount)  43
//   36   4  SEG0 8-byte value count                            4968
//   40   4  SEG1 types buffer size, bytes                      19401
//   44   2  u16 mirror of objectCount        (advisory)        507
//   46   2  u16 array count                  (advisory)        2445
//   48   4  total uncompressed size (SEG0 + SEG1)              177261
//   52   4  total compressed size                              17864
//   56   4  blockCount    (binary blobs)                       0
//   60   4  blockTotalSize (uncompressed bytes of all blobs)   0
//   64   4  UNKNOWN                                            0
//   68   4  size in bytes of the blob LZ4 frame-size table     0
//   72   4  SEG0 uncompressed size                             54808
//   76   4  SEG0 compressed size                               9174
//   80   4  SEG1 uncompressed size                             122453
//   84   4  SEG1 compressed size                               8690
//   88   4  SEG1 byte buffer size                              2445
//   92   4  UNKNOWN                                            0
//   96   4  SEG1 4-byte value count                            20544
//  100   4  SEG1 8-byte value count                            2049
//  104   4  member count + 1                 (advisory)        16105
//  108   4  SEG1 object count                                  507
//  112   4  UNKNOWN                                            0
//  116   4  UNKNOWN                                            0
//  120  --  payload: SEG0 then SEG1, back to back, then the blob frames
//
// Offsets 44/46/104 are informational and NOT used; 64/92/112/116 have no established
// meaning. None of them are needed to decode, so none of them are trusted.
//
// === KV3 v5 segment 0: string table + auxiliary element buffers ===
//   0                    string blob, stringBlobSize bytes: NUL-terminated UTF-8 strings,
//                        no count prefix, no offset table; referenced by ZERO-BASED index
//   align(4)
//   +0   u32             stringCount   (this is the FIRST of the 4-byte slots, hence the
//                                       header's slot count being one MORE than usable)
//   +4   (slots-1) x u32 auxiliary 4-byte buffer  (INT32 / UINT32 / FLOAT elements)
//   align(8)             ONLY when the 8-byte buffer is non-empty
//   +    count x 8       auxiliary 8-byte buffer  (DOUBLE / INT64 / UINT64 elements)
//
// === KV3 v5 segment 1: the tree ===
//   0                    objectCount x u32   member counts, one per OBJECT tag, in tree order
//   +                    byteCount bytes     byte buffer
//   align(4)
//   +                    intCount x u32      4-byte values
//   align(8)             ONLY when the 8-byte buffer is non-empty
//   +                    eightByteCount x 8  8-byte values
//   +                    typesSize bytes     type tag stream
//   +                    blockCount x u32    uncompressed blob lengths
//   +                    4 bytes             00 DD EE FF trailer
//   +                    (off 68) bytes      u16 LZ4 frame sizes for the blob payload
//
// === KV3 v3 / v4 block header ===
// CONFIRMED — derived from the bytes and verified across the whole corpus sweep below. The
// first fourteen fields are laid out identically in v3 and v4; only the header LENGTH differs.
// Values in the right columns are the 2023-03 (v3) and 2023-09 (v4) weapons.vdata_c's.
//
//    0   4  magic 03/04 33 56 4B                            KV3_V3      KV3_V4
//    4  16  format GUID                                     7412167c-...  (same)
//   20   4  compressionMethod  0 = none, 1 = LZ4, 2 = zstd   1           1
//   24   2  compressionDictionaryId                         0           0
//   26   2  compressionFrameSize (blob frames only)         16384       16384
//   28   4  byte buffer size, bytes                         0           2974
//   32   4  4-byte slot count (first slot = stringCount)    30481       27507
//   36   4  8-byte value count                              8898        8898
//   40   4  string + types area size, bytes                 40387       40387
//   44   2  u16 object count                   (advisory)   624         624
//   46   2  u16 array count                    (advisory)   2974        2974
//   48   4  uncompressed payload size                       233503      224583
//   52   4  compressed payload size                         19379       19343
//   56   4  blockCount    (binary blobs)                    0           0
//   60   4  blockTotalSize (uncompressed bytes of all blobs) 0          0
//   64  --  v3: payload begins here
//   64   4  UNKNOWN — 0 in every v4 block measured          --          0
//   68   4  UNKNOWN — 0 in every v4 block measured          --          0
//   72  --  v4: payload begins here
//
// Offsets 44/46 are informational and NOT used. Across 28,343 blocks the u16 at 44 equalled
// the number of OBJECT tags and the u16 at 46 the number of array tags (types 8, 10 and 24)
// EXACTLY, every time — which is how they were identified, and why they are still not trusted.
//
// v4's two extra header words are UNKNOWN and were 0 in all 26,033 v4 blocks, INCLUDING the
// 122 that carry binary blobs. So, unlike v5's offset 68, neither of them is the blob
// frame-size table size: in v3/v4 that table is simply whatever is left of the payload buffer
// after the trailer.
//
// With compressionMethod 0 the payload is stored verbatim and v3/v4 write the SAME value into
// both the uncompressed and the compressed size field (117/117 blocks) — unlike v5, which
// zeroes the compressed field. This reader uses the UNCOMPRESSED size in that mode, which is
// correct for both conventions.
//
// === KV3 v3 / v4 payload: ONE buffer, not two ===
// CONFIRMED. There is no segment table and no auxiliary area; every buffer lives in a single
// compressed region starting right after the header:
//
//   0                    byteCount bytes     byte buffer
//   align(4)
//   +0   u32             stringCount         (the FIRST of the 4-byte slots, exactly as in
//                                             v5's segment 0 — so the header's slot count is
//                                             one MORE than the usable 4-byte values)
//   +4   (slots-1) x u32 4-byte values
//   align(8)             ALWAYS — see below
//   +                    eightByteCount x 8  8-byte values
//   +                    stringCount NUL-terminated UTF-8 strings, then immediately the type
//                        tag stream; the two together occupy exactly the (off 40) bytes of the
//                        "string + types area", and the boundary between them is found ONLY by
//                        walking the declared number of strings
//   +                    blockCount x u32    uncompressed blob lengths
//   +                    4 bytes             00 DD EE FF trailer
//   +                    the rest of the buffer: u16 LZ4 frame sizes for the blob payload
//
// The align(8) before the 8-byte buffer is UNCONDITIONAL in v3/v4 — the exact opposite of v5,
// where it is materialised only when that buffer is non-empty. Measured: of the 498 v3/v4
// blocks with an empty 8-byte buffer, 220 sit at a 4-mod-8 offset and their trailer only lands
// when the padding IS applied; 0 blocks contradict. Getting this wrong shifts the buffer area
// by 4 bytes and destroys the trailer check.
//
// The blob frame payloads are NOT in the buffer: they sit in the DATA block immediately after
// the stored payload, i.e. at headerSize + (compressionMethod == 0 ? uncompressed : compressed).
//
// === What actually differs between v3/v4 and v5 ===
// Only two things change the WALK; everything else is header geometry.
//   1. OBJECT member counts. v5 has a dedicated u32 buffer at the start of segment 1, one
//      entry per OBJECT tag. v3/v4 have no such buffer — OBJECT reads its count from the
//      general 4-byte buffer, exactly like ARRAY.
//   2. The auxiliary element buffers, and with them type 25 (ARRAY_TYPE_AUXILIARY_BUFFER),
//      exist ONLY in v5. A type 25 tag in a v3/v4 block is refused by name.
// Everything else — the tag encoding, the flag byte, the type ids, the string table semantics,
// the blob area, the trailer — is shared, and so is the code below.
//
// v3 vs v4 differ from each other in the header LENGTH only (64 vs 72 bytes). The corpus shows
// a behavioural difference in what the compiler EMITS rather than in what the format allows:
// v3 blocks use ARRAY_TYPED (type 10, count from the 4-byte buffer) where v4 blocks use
// ARRAY_TYPE_BYTE_LENGTH (type 24, count from the byte buffer). Both versions are read by the
// same code and either tag is accepted in either.
//
// === Type tag stream ===
//   b = next types byte; if (b & 0x80) then type = b & 0x7F and a second byte carries FLAGS,
//   else type = b and flags = 0. Flags annotate strings only (resource / resource_name /
//   soundevent); they never change how many bytes a value consumes, and since they only
//   matter when RE-SERIALISING to KV3 text they are read and DROPPED here.
//
//   id  name                          consumes
//    0  STRING_MULTI                  u32 string id from the 4-byte buffer     INFERRED
//    1  NULL                          nothing                                  CONFIRMED
//    2  BOOLEAN                       1 byte from the byte buffer              INFERRED
//    3  INT64                         8 bytes from the 8-byte buffer, signed   INFERRED
//    4  UINT64                        8 bytes from the 8-byte buffer, unsigned CONFIRMED
//    5  DOUBLE                        8 bytes from the 8-byte buffer           CONFIRMED
//    6  STRING                        u32 string id; -1 means ""               CONFIRMED
//    7  BINARY_BLOB                   next blob from the block area, or (when
//                                     blockCount == 0) u32 length from the
//                                     4-byte buffer + that many bytes from the
//                                     byte buffer                CONFIRMED / INFERRED
//    8  ARRAY                         u32 count from the 4-byte buffer, then
//                                     count fully tagged values                CONFIRMED
//    9  OBJECT                        u32 count — v5: from the OBJECT-COUNT
//                                     buffer; v3/v4: from the 4-byte buffer —
//                                     then count x (u32 string id, tagged value) CONFIRMED
//   10  ARRAY_TYPED                   u32 count, one element tag, count values CONFIRMED
//   11  INT32                         4 bytes from the 4-byte buffer, signed   CONFIRMED
//   12  UINT32                        4 bytes from the 4-byte buffer, unsigned CONFIRMED
//   13  BOOLEAN_TRUE                  nothing -> true                          CONFIRMED
//   14  BOOLEAN_FALSE                 nothing -> false                         CONFIRMED
//   15  INT64_ZERO                    nothing -> 0                             CONFIRMED
//   16  INT64_ONE                     nothing -> 1                             CONFIRMED
//   17  DOUBLE_ZERO                   nothing -> 0.0                           CONFIRMED
//   18  DOUBLE_ONE                    nothing -> 1.0                           CONFIRMED
//   19  FLOAT                         4 bytes from the 4-byte buffer           INFERRED
//   23  INT32_AS_BYTE                 1 byte from the byte buffer              INFERRED
//   24  ARRAY_TYPE_BYTE_LENGTH        1-byte count from the BYTE buffer, one
//                                     element tag, count values from the MAIN
//                                     buffers                                  CONFIRMED
//   25  ARRAY_TYPE_AUXILIARY_BUFFER   as 24, but the ELEMENTS come from the
//                                     segment-0 AUXILIARY buffers; v5 ONLY     CONFIRMED
//   20, 21, 22                        unused / reserved -> hard error
//
// Type 25 exists purely for compression — it hoists the engine's small fixed-size numeric
// structs (CFiringModeFloat = float32[2], colours, vectors) into one contiguous, near-identical
// run that LZ4s far better than it would interleaved. Reals are stored as binary64 even where
// the schema type is float32, so a value the game treats as 0.0006f decodes to the double
// 0.0006 in every version.
//
// === CONFIRMED vs INFERRED ===
// "CONFIRMED" means the byte layout was verified numerically: a decoder consuming every buffer
// to its exact declared end, with zero mismatches and zero parse errors, over
//   * 2,581 KV3_V5 blocks sampled from a shipped pak01, and
//   * 28,343 KV3 v3/v4 blocks — 2,310 v3 and 26,033 v4, every one in build 25175329's pak01
//     (vmat_c, vmdl_c, vpcf_c, vsmart_c, vcompmat_c, vsnap_c, vpost_c, vdata_c), 117 of them
//     stored uncompressed and 237 of them carrying binary blobs.
// An off-by-one in any rule desynchronises the walk immediately, so a clean sweep of that size
// is the format's own proof.
//
// "INFERRED" means the rule is consistent with the format but no file in either corpus
// exercised it. Types 0, 2, 3, 19 and 23 are in that bucket, as is the inline BINARY_BLOB path.
// They are implemented, not trusted. The v3/v4 sweep exercised types 1, 4, 5, 6, 7, 8, 9, 10,
// 11, 12, 13, 14, 15, 16, 17 and 18 plus 24, and never produced a type 25 — which is the
// evidence for treating the auxiliary buffers as a v5-only feature rather than an absent one.
//
// === Deliberately NOT implemented ===
//   * KV3 v1 and v2, and the legacy uncompressed VKV3 (v0). NONE of them occurs anywhere in
//     the measured corpus, so there is nothing to verify an implementation against, and a
//     speculative one would risk returning a silently wrong tree. The reader names the exact
//     version it found and throws.
//   * compressionMethod 2 (zstd). Absent from every block measured, in every version. Adding
//     it would mean adding a package dependency, which is out of scope here.
// Both are hard errors on purpose: a silently wrong tree is far worse than a loud refusal.
//
// === Binary blobs ===
// A BINARY_BLOB value becomes a base64 Value.StringValue. google.protobuf.Value has no bytes
// case, and the alternatives (a list of byte numbers; a lossy UTF-8 decode) are respectively
// enormous and destructive. weapons.vdata_c has blockCount == 0 in every version and contains
// NO BINARY_BLOB value, so this choice affects no current caller; it is recorded here so that
// whoever first feeds a blob-bearing resource through this reader knows what they will get.
//
// === Determinism ===
// Pure function of the input bytes. Object members are inserted into the Struct in source
// order (the CanonicalJson layer sorts keys later, exactly as the text parser's output is
// treated); array order is the file's order; no timestamp, path, locale or environment state
// enters the result. Decoding the same bytes twice yields an identical tree.
//
// === Fail-loud ===
// This reads INPUT BINARY, so it never degrades — every failure throws Kv3BinaryException:
//   * every container, header, buffer and segment read is bounds-checked BEFORE it happens
//     and reports the offset and what was expected, so a truncated or corrupt file can never
//     surface as IndexOutOfRangeException / OverflowException;
//   * each cursor is bounded by its DECLARED end, not merely by the array length, so a
//     desynchronised walk fails at the exact tag that caused it;
//   * the computed buffer layout must match the actual payload length, and the 00 DD EE FF
//     trailer must be where the layout says;
//   * after the walk every cursor must sit exactly on its declared end. That whole-buffer
//     consumption check is the strongest correctness signal the format offers, and it runs
//     unconditionally rather than behind a debug flag;
//   * unknown type ids, unsupported versions and unsupported compression methods are named
//     and refused rather than skipped.

using System.Buffers.Binary;
using System.Text;

using Google.Protobuf.WellKnownTypes;

namespace Cs2SchemaTracker.Host.Kv3Binary;

/// <summary>Thrown on a malformed or unsupported compiled-resource / binary-KV3 payload.</summary>
internal sealed class Kv3BinaryException : Exception
{
    public Kv3BinaryException(string message) : base(message) { }
    public Kv3BinaryException(string message, Exception inner) : base(message, inner) { }
}

internal static class Kv3BinaryReader
{
    /// <summary>Byte length of the compiled-resource container header.</summary>
    private const int ContainerHeaderSize = 16;

    /// <summary>Byte length of one entry in the container's block table.</summary>
    private const int BlockTableEntrySize = 12;

    /// <summary>Byte length of a KV3 v3 block header; its payload starts here.</summary>
    private const int V3HeaderSize = 64;

    /// <summary>Byte length of a KV3 v4 block header; its payload starts here.</summary>
    private const int V4HeaderSize = 72;

    /// <summary>Byte length of a KV3 v5 block header; its two segments start here.</summary>
    private const int V5HeaderSize = 120;

    /// <summary>The u32 that terminates the buffer area: bytes 00 DD EE FF.</summary>
    private const uint SegmentTrailer = 0xFFEEDD00u;

    /// <summary>
    /// Recursion guard. Real Source 2 KV3 trees nest a couple of dozen levels at most; this
    /// only exists so a hostile or corrupt types stream fails loudly instead of overflowing
    /// the stack.
    /// </summary>
    private const int MaxDepth = 512;

    /// <summary>
    /// Guard on the container block count. A real resource has a handful of blocks; a wild
    /// value here means the header is not a compiled resource at all.
    /// </summary>
    private const int MaxBlockTableEntries = 1024;

    // KV3 type ids. See the table in the file header for what each one consumes.
    private const int TypeStringMulti = 0;
    private const int TypeNull = 1;
    private const int TypeBoolean = 2;
    private const int TypeInt64 = 3;
    private const int TypeUInt64 = 4;
    private const int TypeDouble = 5;
    private const int TypeString = 6;
    private const int TypeBinaryBlob = 7;
    private const int TypeArray = 8;
    private const int TypeObject = 9;
    private const int TypeArrayTyped = 10;
    private const int TypeInt32 = 11;
    private const int TypeUInt32 = 12;
    private const int TypeBooleanTrue = 13;
    private const int TypeBooleanFalse = 14;
    private const int TypeInt64Zero = 15;
    private const int TypeInt64One = 16;
    private const int TypeDoubleZero = 17;
    private const int TypeDoubleOne = 18;
    private const int TypeFloat = 19;
    private const int TypeInt32AsByte = 23;
    private const int TypeArrayByteLength = 24;
    private const int TypeArrayAuxiliaryBuffer = 25;

    /// <summary>
    /// Decode a compiled Source 2 resource (e.g. a .vdata_c file's full bytes): locate its DATA
    /// block, decode the binary KV3 payload, and return the structural tree.
    /// </summary>
    public static Value Decode(byte[] resourceBytes)
    {
        ArgumentNullException.ThrowIfNull(resourceBytes);
        (int dataOffset, int dataSize) = FindDataBlock(resourceBytes);
        return DecodeBlock(resourceBytes.AsSpan(dataOffset, dataSize));
    }

    /// <summary>
    /// Decode a bare KV3 block — the payload of a compiled resource's DATA block, without the
    /// container around it. Exposed for tests and for callers that already sliced the block out.
    /// </summary>
    public static Value DecodeBlock(ReadOnlySpan<byte> block)
    {
        int version = IdentifyVersion(block);
        Walker walker = version == 5 ? BuildV5Walker(block) : BuildV3OrV4Walker(block, version);
        Value root = walker.ReadRoot();
        walker.RequireFullyConsumed();
        return root;
    }

    // -----------------------------------------------------------------------------------
    // container
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Walk the compiled-resource block table and return the (offset, size) of the DATA block.
    /// </summary>
    private static (int Offset, int Size) FindDataBlock(byte[] resourceBytes)
    {
        ReadOnlySpan<byte> span = resourceBytes;
        if (span.Length < ContainerHeaderSize)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: {span.Length} bytes is too small for a compiled resource " +
                $"container header ({ContainerHeaderSize} bytes required).");
        }

        uint blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
        uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));

        if (blockCount > MaxBlockTableEntries)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: container declares {blockCount} blocks, which exceeds the " +
                $"{MaxBlockTableEntries}-block sanity limit; this is not a compiled resource.");
        }

        long tableStart = 8L + blockOffset;
        long tableEnd = tableStart + ((long)blockCount * BlockTableEntrySize);
        if (tableStart < ContainerHeaderSize || tableEnd > span.Length)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: container block table spans [{tableStart}, {tableEnd}) which " +
                $"is outside the {span.Length}-byte file.");
        }

        var names = new List<string>((int)blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            int entry = (int)tableStart + (i * BlockTableEntrySize);
            string name = DecodeBlockName(span.Slice(entry, 4));
            names.Add(name);
            if (!string.Equals(name, "DATA", StringComparison.Ordinal))
            {
                continue;
            }

            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entry + 4, 4));
            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entry + 8, 4));
            long start = entry + 4L + dataOffset;
            long end = start + dataSize;
            if (start < 0 || end > span.Length)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: DATA block spans [{start}, {end}) which is outside the " +
                    $"{span.Length}-byte file (truncated or corrupt resource).");
            }
            return ((int)start, (int)dataSize);
        }

        throw new Kv3BinaryException(
            $"Kv3BinaryReader: compiled resource has no DATA block (blocks: " +
            $"{(names.Count == 0 ? "<none>" : string.Join(", ", names))}).");
    }

    /// <summary>Render a 4-byte block tag for comparison and for error messages.</summary>
    private static string DecodeBlockName(ReadOnlySpan<byte> tag)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = tag[i];
            chars[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }
        return new string(chars);
    }

    // -----------------------------------------------------------------------------------
    // version dispatch
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Identify the block by its 4-byte magic and return the KV3 version. Dispatch is on the
    /// file's OWN magic and on nothing else — never on a build id or a schema era, because the
    /// version boundary does not line up with either. Versions this reader cannot verify are
    /// named and refused; see "Deliberately NOT implemented".
    /// </summary>
    private static int IdentifyVersion(ReadOnlySpan<byte> block)
    {
        if (block.Length < 4)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: DATA block is {block.Length} bytes, too small to hold a " +
                $"4-byte KV3 magic.");
        }

        string magicHex = Convert.ToHexString(block[..4]);

        // Legacy uncompressed "VKV\x03".
        if (block[0] == 0x56 && block[1] == 0x4B && block[2] == 0x56 && block[3] == 0x03)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: unsupported KV3 version 0 (legacy uncompressed VKV3; only v3, " +
                $"v4 and v5 are implemented); source=DATA block magic {magicHex}.");
        }

        // Versioned magics are "\xNN3VK" -> NN 33 56 4B.
        if (block[1] != 0x33 || block[2] != 0x56 || block[3] != 0x4B || block[0] is 0 or > 5)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: DATA block is not binary KV3; source=DATA block magic " +
                $"{magicHex}.");
        }

        int version = block[0];
        if (version is not (3 or 4 or 5))
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: unsupported KV3 version {version} (only v3, v4 and v5 are " +
                $"implemented); source=DATA block magic {magicHex}.");
        }

        return version;
    }

    /// <summary>
    /// Read a u32 size/count field out of a block header and range-check it into an int, so a
    /// garbage header fails here by name rather than as an OverflowException deeper in the walk.
    /// </summary>
    private static int HeaderLength(ReadOnlySpan<byte> block, int offset, string what)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(offset, 4));
        if (value > int.MaxValue)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: header field '{what}' at offset {offset} is {value}, " +
                $"outside the addressable range.");
        }
        return (int)value;
    }

    /// <summary>Bounds-check a slice of the DATA block before taking it.</summary>
    private static void RequireRange(ReadOnlySpan<byte> block, int offset, int length, string what)
    {
        if (offset < 0 || length < 0 || (long)offset + length > block.Length)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: {what} spans [{offset}, {(long)offset + length}) which is " +
                $"outside the {block.Length}-byte DATA block.");
        }
    }

    private static int Align(int value, int alignment) =>
        (value + (alignment - 1)) & ~(alignment - 1);

    // -----------------------------------------------------------------------------------
    // KV3 v5: two segments
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Parsed KV3 v5 block header. Only the fields the walk actually needs are surfaced; the
    /// advisory and UNKNOWN fields are deliberately not read.
    /// </summary>
    private sealed class Kv3V5Header
    {
        private Kv3V5Header(ReadOnlySpan<byte> block)
        {
            CompressionMethod = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(20, 4));
            StringBlobSize = HeaderLength(block, 28, "SEG0 string blob size");
            AuxSlotCount = HeaderLength(block, 32, "SEG0 4-byte slot count");
            AuxEightByteCount = HeaderLength(block, 36, "SEG0 8-byte value count");
            TypesSize = HeaderLength(block, 40, "SEG1 types buffer size");
            UncompressedTotal = HeaderLength(block, 48, "total uncompressed size");
            BlobCount = HeaderLength(block, 56, "blob count");
            BlobTotalSize = HeaderLength(block, 60, "blob total size");
            BlobFrameTableSize = HeaderLength(block, 68, "blob frame table size");
            Segment0Uncompressed = HeaderLength(block, 72, "SEG0 uncompressed size");
            Segment0Compressed = HeaderLength(block, 76, "SEG0 compressed size");
            Segment1Uncompressed = HeaderLength(block, 80, "SEG1 uncompressed size");
            Segment1Compressed = HeaderLength(block, 84, "SEG1 compressed size");
            ByteCount = HeaderLength(block, 88, "SEG1 byte buffer size");
            IntCount = HeaderLength(block, 96, "SEG1 4-byte value count");
            EightByteCount = HeaderLength(block, 100, "SEG1 8-byte value count");
            ObjectCount = HeaderLength(block, 108, "SEG1 object count");
        }

        public uint CompressionMethod { get; }
        public int StringBlobSize { get; }
        public int AuxSlotCount { get; }
        public int AuxEightByteCount { get; }
        public int TypesSize { get; }
        public int UncompressedTotal { get; }
        public int BlobCount { get; }
        public int BlobTotalSize { get; }
        public int BlobFrameTableSize { get; }
        public int Segment0Uncompressed { get; }
        public int Segment0Compressed { get; }
        public int Segment1Uncompressed { get; }
        public int Segment1Compressed { get; }
        public int ByteCount { get; }
        public int IntCount { get; }
        public int EightByteCount { get; }
        public int ObjectCount { get; }

        public static Kv3V5Header Parse(ReadOnlySpan<byte> block)
        {
            if (block.Length < V5HeaderSize)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: KV3 v5 DATA block is {block.Length} bytes, shorter than " +
                    $"its {V5HeaderSize}-byte header.");
            }

            return new Kv3V5Header(block);
        }
    }

    /// <summary>
    /// Resolve a v5 block into a ready-to-walk state: decompress both segments, split the
    /// string table, lay the seven buffers out and recover any binary blobs.
    /// </summary>
    private static Walker BuildV5Walker(ReadOnlySpan<byte> block)
    {
        var header = Kv3V5Header.Parse(block);
        (byte[] segment0, byte[] segment1, int blobRegionOffset) = DecompressSegments(header, block);

        long total = (long)segment0.Length + segment1.Length;
        if (total != header.UncompressedTotal)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: segments decompressed to {total} bytes but the header " +
                $"declares uncompressedTotal = {header.UncompressedTotal}.");
        }

        // ---- segment 0: string blob, then the two auxiliary buffers ----
        RequireBuffer(segment0, 0, header.StringBlobSize, "SEG0 string blob");
        string[] strings = SplitStrings(segment0.AsSpan(0, header.StringBlobSize));

        int p = Align(header.StringBlobSize, 4);
        RequireBuffer(segment0, p, 4, "SEG0 string count");
        int declaredStringCount = ReadInt32Checked(segment0, p, "SEG0 string count");
        if (declaredStringCount != strings.Length)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: SEG0 declares {declaredStringCount} strings but the blob " +
                $"holds {strings.Length}.");
        }

        int auxIntBase = p + 4;
        int auxIntCount = Math.Max(header.AuxSlotCount - 1, 0);
        int auxIntEnd = auxIntBase + (auxIntCount * 4);

        // The align(8) before the auxiliary 8-byte buffer is materialised ONLY when that
        // buffer is non-empty; nothing follows it, so an empty one leaves no padding.
        int q = auxIntEnd;
        int segment0End = q;
        if (header.AuxEightByteCount != 0)
        {
            q = Align(q, 8);
            segment0End = q + (header.AuxEightByteCount * 8);
        }
        int auxEightBase = q;
        int auxEightEnd = auxEightBase + (header.AuxEightByteCount * 8);
        if (segment0End != segment0.Length)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: SEG0 layout computes an end of {segment0End} but the " +
                $"segment is {segment0.Length} bytes.");
        }

        // ---- segment 1: object counts, bytes, ints, 8-byte values, types ----
        int objectBase = 0;
        int objectEnd = objectBase + (header.ObjectCount * 4);
        int byteBase = objectEnd;
        int byteEnd = byteBase + header.ByteCount;
        int intBase = Align(byteEnd, 4);
        int intEnd = intBase + (header.IntCount * 4);
        int r = intEnd;
        if (header.EightByteCount != 0)
        {
            r = Align(r, 8);
        }
        int eightBase = r;
        int eightEnd = eightBase + (header.EightByteCount * 8);
        int typesBase = eightEnd;
        int typesEnd = typesBase + header.TypesSize;

        byte[][] blobs = ReadBlobArea(
            segment1,
            typesEnd,
            header.BlobCount,
            header.BlobTotalSize,
            header.BlobFrameTableSize,
            header.CompressionMethod,
            block,
            blobRegionOffset,
            5);

        return new Walker(
            version: 5,
            main: segment1,
            auxiliary: segment0,
            strings: strings,
            blobs: blobs,
            blobCount: header.BlobCount,
            hasObjectCountBuffer: true,
            objectBase: objectBase,
            objectEnd: objectEnd,
            byteBase: byteBase,
            byteEnd: byteEnd,
            intBase: intBase,
            intEnd: intEnd,
            eightBase: eightBase,
            eightEnd: eightEnd,
            typesBase: typesBase,
            typesEnd: typesEnd,
            hasAuxiliaryBuffers: true,
            auxIntBase: auxIntBase,
            auxIntEnd: auxIntEnd,
            auxEightBase: auxEightBase,
            auxEightEnd: auxEightEnd);
    }

    /// <summary>
    /// Decompress the two v5 segments and report where the blob payload region begins in the
    /// DATA block (immediately after both stored segments).
    /// </summary>
    private static (byte[] Segment0, byte[] Segment1, int BlobRegionOffset) DecompressSegments(
        Kv3V5Header header, ReadOnlySpan<byte> block)
    {
        Span<int> uncompressed = [header.Segment0Uncompressed, header.Segment1Uncompressed];
        Span<int> compressed = [header.Segment0Compressed, header.Segment1Compressed];

        var segments = new byte[2][];
        int pos = V5HeaderSize;

        for (int i = 0; i < 2; i++)
        {
            int unc = uncompressed[i];
            int comp = compressed[i];

            if (unc == 0 && comp == 0)
            {
                // An empty segment occupies no payload bytes. No v5 block measured has one, but
                // handling it keeps the two segments positionally addressed rather than packed.
                segments[i] = [];
                continue;
            }

            switch (header.CompressionMethod)
            {
                case 0:
                    // Stored verbatim. The per-segment compressed-size field is 0 in this mode,
                    // so the cursor must advance by the UNCOMPRESSED size.
                    RequireRange(block, pos, unc, $"segment {i} (stored uncompressed)");
                    segments[i] = block.Slice(pos, unc).ToArray();
                    pos += comp != 0 ? comp : unc;
                    break;

                case 1:
                    RequireRange(block, pos, comp, $"segment {i} (LZ4)");
                    segments[i] = Lz4Block.Decode(block.Slice(pos, comp), unc);
                    pos += comp;
                    break;

                case 2:
                    throw new Kv3BinaryException(
                        $"Kv3BinaryReader: unsupported compression method 2 (zstd); only method 0 " +
                        $"(none) and method 1 (LZ4) are implemented; source=KV3 v5 DATA block.");

                default:
                    throw new Kv3BinaryException(
                        $"Kv3BinaryReader: unsupported compression method {header.CompressionMethod}; " +
                        $"only method 0 (none) and method 1 (LZ4) are implemented; source=KV3 v5 " +
                        $"DATA block.");
            }
        }

        return (segments[0], segments[1], pos);
    }

    // -----------------------------------------------------------------------------------
    // KV3 v3 / v4: one payload buffer
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Parsed KV3 v3 / v4 block header. v3 and v4 share every field below; v4 only appends two
    /// UNKNOWN words, which is why its payload starts 8 bytes later.
    /// </summary>
    private sealed class Kv3V34Header
    {
        private Kv3V34Header(ReadOnlySpan<byte> block, int version)
        {
            Version = version;
            HeaderSize = version == 3 ? V3HeaderSize : V4HeaderSize;
            CompressionMethod = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(20, 4));
            ByteCount = HeaderLength(block, 28, "byte buffer size");
            IntSlotCount = HeaderLength(block, 32, "4-byte slot count");
            EightByteCount = HeaderLength(block, 36, "8-byte value count");
            StringAndTypesSize = HeaderLength(block, 40, "string + types area size");
            UncompressedSize = HeaderLength(block, 48, "uncompressed payload size");
            CompressedSize = HeaderLength(block, 52, "compressed payload size");
            BlobCount = HeaderLength(block, 56, "blob count");
            BlobTotalSize = HeaderLength(block, 60, "blob total size");
        }

        public int Version { get; }
        public int HeaderSize { get; }
        public uint CompressionMethod { get; }
        public int ByteCount { get; }
        public int IntSlotCount { get; }
        public int EightByteCount { get; }
        public int StringAndTypesSize { get; }
        public int UncompressedSize { get; }
        public int CompressedSize { get; }
        public int BlobCount { get; }
        public int BlobTotalSize { get; }

        public static Kv3V34Header Parse(ReadOnlySpan<byte> block, int version)
        {
            int size = version == 3 ? V3HeaderSize : V4HeaderSize;
            if (block.Length < size)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: KV3 v{version} DATA block is {block.Length} bytes, shorter " +
                    $"than its {size}-byte header.");
            }

            return new Kv3V34Header(block, version);
        }
    }

    /// <summary>
    /// Resolve a v3 or v4 block into a ready-to-walk state. Everything lives in ONE buffer:
    /// bytes, then the 4-byte slots (the first of which is the string count), then the 8-byte
    /// values, then the strings and the type stream, then the blob area.
    /// </summary>
    private static Walker BuildV3OrV4Walker(ReadOnlySpan<byte> block, int version)
    {
        var header = Kv3V34Header.Parse(block, version);
        (byte[] buffer, int blobRegionOffset) = DecompressPayload(header, block);

        int byteBase = 0;
        int byteEnd = byteBase + header.ByteCount;

        // The string count is the FIRST 4-byte slot, so the header's slot count is one more
        // than the number of usable 4-byte values — exactly as in v5's segment 0.
        int slotsBase = Align(byteEnd, 4);
        if (header.IntSlotCount < 1)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: KV3 v{version} declares 0 4-byte slots, but the first slot " +
                $"always holds the string count.");
        }
        RequireBuffer(buffer, slotsBase, 4, $"KV3 v{version} string count");
        int stringCount = ReadInt32Checked(buffer, slotsBase, $"KV3 v{version} string count");

        int intBase = slotsBase + 4;
        int intEnd = slotsBase + (header.IntSlotCount * 4);

        // Unlike v5, the align(8) here is UNCONDITIONAL — it is applied even when the 8-byte
        // buffer is empty. 220 corpus blocks prove it; none contradict.
        int eightBase = Align(intEnd, 8);
        int eightEnd = eightBase + (header.EightByteCount * 8);

        // The strings and the type stream share one area whose total size the header gives;
        // the boundary between them is found only by walking the declared string count.
        RequireBuffer(buffer, eightEnd, header.StringAndTypesSize,
            $"KV3 v{version} string + types area");
        int areaEnd = eightEnd + header.StringAndTypesSize;
        (string[] strings, int typesBase) = ReadStringTable(
            buffer, eightEnd, areaEnd, stringCount, version);

        byte[][] blobs = ReadBlobArea(
            buffer,
            areaEnd,
            header.BlobCount,
            header.BlobTotalSize,
            // v3/v4 do not declare a frame-table size: the table is the rest of the buffer.
            frameTableSize: -1,
            header.CompressionMethod,
            block,
            blobRegionOffset,
            version);

        return new Walker(
            version: version,
            main: buffer,
            auxiliary: [],
            strings: strings,
            blobs: blobs,
            blobCount: header.BlobCount,
            // No dedicated object-count buffer: OBJECT reads its member count from the 4-byte
            // buffer, exactly like ARRAY.
            hasObjectCountBuffer: false,
            objectBase: 0,
            objectEnd: 0,
            byteBase: byteBase,
            byteEnd: byteEnd,
            intBase: intBase,
            intEnd: intEnd,
            eightBase: eightBase,
            eightEnd: eightEnd,
            typesBase: typesBase,
            typesEnd: areaEnd,
            // No auxiliary buffers at all, so no type 25.
            hasAuxiliaryBuffers: false,
            auxIntBase: 0,
            auxIntEnd: 0,
            auxEightBase: 0,
            auxEightEnd: 0);
    }

    /// <summary>
    /// Decompress the single v3/v4 payload and report where the blob payload region begins in
    /// the DATA block (immediately after the stored payload).
    /// </summary>
    private static (byte[] Buffer, int BlobRegionOffset) DecompressPayload(
        Kv3V34Header header, ReadOnlySpan<byte> block)
    {
        int pos = header.HeaderSize;
        int unc = header.UncompressedSize;

        switch (header.CompressionMethod)
        {
            case 0:
                // Stored verbatim. v3/v4 write the uncompressed size into the compressed-size
                // field too, so advancing by the UNCOMPRESSED size is right either way.
                RequireRange(block, pos, unc, $"KV3 v{header.Version} payload (stored uncompressed)");
                return (block.Slice(pos, unc).ToArray(), pos + unc);

            case 1:
                RequireRange(block, pos, header.CompressedSize, $"KV3 v{header.Version} payload (LZ4)");
                byte[] buffer = Lz4Block.Decode(block.Slice(pos, header.CompressedSize), unc);
                return (buffer, pos + header.CompressedSize);

            case 2:
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: unsupported compression method 2 (zstd); only method 0 " +
                    $"(none) and method 1 (LZ4) are implemented; source=KV3 v{header.Version} " +
                    $"DATA block.");

            default:
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: unsupported compression method {header.CompressionMethod}; " +
                    $"only method 0 (none) and method 1 (LZ4) are implemented; source=KV3 " +
                    $"v{header.Version} DATA block.");
        }
    }

    /// <summary>
    /// Read <paramref name="count"/> NUL-terminated UTF-8 strings starting at
    /// <paramref name="start"/> and return them with the offset the type stream begins at.
    /// Nothing separates the two: the count is the only boundary marker the format has.
    /// </summary>
    private static (string[] Strings, int TypesBase) ReadStringTable(
        byte[] buffer, int start, int end, int count, int version)
    {
        // Every string costs at least its NUL, so a count larger than the area cannot be real.
        // Checked BEFORE allocating, so a garbage count fails by name instead of as an
        // OutOfMemoryException.
        if (count > end - start)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: KV3 v{version} declares {count} strings but the string + " +
                $"types area is only {end - start} bytes.");
        }

        var strings = new string[count];
        int p = start;
        for (int i = 0; i < count; i++)
        {
            int nul = Array.IndexOf(buffer, (byte)0, p, end - p);
            if (nul < 0)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: KV3 v{version} string {i} of {count} starting at offset " +
                    $"{p} has no NUL terminator before the string + types area ends at {end}.");
            }
            strings[i] = Encoding.UTF8.GetString(buffer.AsSpan(p, nul - p));
            p = nul + 1;
        }
        return (strings, p);
    }

    // -----------------------------------------------------------------------------------
    // shared buffer helpers
    // -----------------------------------------------------------------------------------

    private static void RequireBuffer(byte[] buffer, int offset, int length, string what)
    {
        if (offset < 0 || length < 0 || (long)offset + length > buffer.Length)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: {what} spans [{offset}, {(long)offset + length}) which is " +
                $"outside the {buffer.Length}-byte payload buffer.");
        }
    }

    private static int ReadInt32Checked(byte[] buffer, int offset, string what)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
        if (value < 0)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: {what} at offset {offset} is {value}, which cannot be negative.");
        }
        return value;
    }

    /// <summary>
    /// Split the v5 string blob on NUL. The blob ends with the terminator of its last string,
    /// so the trailing empty element is dropped.
    /// </summary>
    private static string[] SplitStrings(ReadOnlySpan<byte> blob)
    {
        if (blob.Length == 0)
        {
            return [];
        }

        var strings = new List<string>();
        int start = 0;
        for (int i = 0; i < blob.Length; i++)
        {
            if (blob[i] != 0)
            {
                continue;
            }
            strings.Add(Encoding.UTF8.GetString(blob.Slice(start, i - start)));
            start = i + 1;
        }
        if (start != blob.Length)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: SEG0 string blob does not end with a NUL terminator " +
                $"({blob.Length - start} bytes trail the last complete string).");
        }
        return [.. strings];
    }

    /// <summary>
    /// Read what follows the type stream — the u32 blob lengths, the 00 DD EE FF trailer and
    /// the u16 LZ4 frame-size table — and recover the individual binary blobs.
    /// <paramref name="frameTableSize"/> is the header's declared table size for v5, or -1 for
    /// v3/v4, which do not declare one and simply run the table to the end of the buffer.
    /// </summary>
    private static byte[][] ReadBlobArea(
        byte[] buffer,
        int cursor,
        int blobCount,
        int blobTotalSize,
        int frameTableSize,
        uint compressionMethod,
        ReadOnlySpan<byte> block,
        int blobRegionOffset,
        int version)
    {
        RequireBuffer(buffer, cursor, 0, $"KV3 v{version} buffer area");

        // The u32 length list has to fit in what is left of the buffer. Checked BEFORE
        // allocating, so a garbage count fails by name instead of as an OutOfMemoryException.
        if ((long)blobCount * 4 > buffer.Length - cursor)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: KV3 v{version} declares {blobCount} binary blobs, whose " +
                $"length list does not fit in the {buffer.Length - cursor} bytes left after the " +
                $"types buffer at offset {cursor}.");
        }

        var blobLengths = new int[blobCount];
        for (int i = 0; i < blobCount; i++)
        {
            RequireBuffer(buffer, cursor, 4, $"KV3 v{version} blob length");
            blobLengths[i] = ReadInt32Checked(buffer, cursor, $"KV3 v{version} blob length");
            cursor += 4;
        }

        int tail = buffer.Length - cursor;
        if (tail >= 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(cursor, 4)) == SegmentTrailer)
        {
            cursor += 4;
            int tableSize = frameTableSize >= 0 ? frameTableSize : buffer.Length - cursor;
            int frameCount = tableSize / 2;
            var frameSizes = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                RequireBuffer(buffer, cursor + (i * 2), 2, $"KV3 v{version} blob frame size");
                frameSizes[i] = BinaryPrimitives.ReadUInt16LittleEndian(
                    buffer.AsSpan(cursor + (i * 2), 2));
            }
            cursor += tableSize;
            if (cursor != buffer.Length)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: KV3 v{version} payload has {buffer.Length - cursor} " +
                    $"unexplained bytes after the blob frame table (offset {cursor} of " +
                    $"{buffer.Length}).");
            }
            return blobCount == 0
                ? []
                : SliceBlobs(
                    compressionMethod, blobTotalSize, block, blobRegionOffset, blobLengths, frameSizes);
        }

        if (tail == 0 && version == 5)
        {
            // Trailer omitted. Seen only in v5, with an empty 8-byte buffer, where the missing
            // align(8) would otherwise be mistaken for a missing trailer. v3/v4 pad
            // unconditionally, so that ambiguity cannot arise there and the trailer is
            // required — all 28,343 measured v3/v4 blocks carry one.
            return [];
        }

        throw new Kv3BinaryException(
            $"Kv3BinaryReader: KV3 v{version} payload layout mismatch at offset {cursor}: {tail} " +
            $"trailing bytes and no 00 DD EE FF trailer (an apparently absent trailer usually " +
            $"means the 8-byte buffer padding was applied when it should not have been, or the " +
            $"other way round).");
    }

    /// <summary>
    /// Recover the individual binary blobs. The frame payloads are NOT in the buffer: they sit
    /// in the DATA block immediately after the stored payload, and (under LZ4) they are
    /// LINKED — a match in frame n may reference output produced by frame n-1 — so they must be
    /// decoded into one continuous buffer, then sliced by the u32 lengths.
    /// </summary>
    private static byte[][] SliceBlobs(
        uint compressionMethod,
        int blobTotalSize,
        ReadOnlySpan<byte> block,
        int blobRegionOffset,
        int[] blobLengths,
        int[] frameSizes)
    {
        long declared = 0;
        foreach (int length in blobLengths)
        {
            declared += length;
        }
        if (declared != blobTotalSize)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: blob lengths sum to {declared} but the header declares " +
                $"blockTotalSize = {blobTotalSize}.");
        }

        byte[] payload;
        if (compressionMethod == 0)
        {
            RequireRange(block, blobRegionOffset, blobTotalSize, "blob payload (stored)");
            payload = block.Slice(blobRegionOffset, blobTotalSize).ToArray();
        }
        else
        {
            payload = new byte[blobTotalSize];
            int written = 0;
            int offset = blobRegionOffset;
            foreach (int frameSize in frameSizes)
            {
                RequireRange(block, offset, frameSize, "blob LZ4 frame");
                written = Lz4Block.DecodeInto(block.Slice(offset, frameSize), payload, written);
                offset += frameSize;
            }
            if (written != blobTotalSize)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: blob frames decompressed to {written} bytes, expected " +
                    $"{blobTotalSize}.");
            }
        }

        var blobs = new byte[blobLengths.Length][];
        int position = 0;
        for (int i = 0; i < blobLengths.Length; i++)
        {
            blobs[i] = payload.AsSpan(position, blobLengths[i]).ToArray();
            position += blobLengths[i];
        }
        return blobs;
    }

    // -----------------------------------------------------------------------------------
    // tree walk
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Walks the tree, for every supported version. Every buffer is consumed strictly in tree
    /// order and each cursor is bounded by its DECLARED end, so a desynchronised walk fails at
    /// the tag that caused it. The only version-dependent behaviour is where OBJECT gets its
    /// member count and whether the segment-0 auxiliary buffers exist.
    /// </summary>
    private sealed class Walker
    {
        private readonly int _version;
        private readonly byte[] _main;
        private readonly byte[] _aux;
        private readonly string[] _strings;
        private readonly byte[][] _blobs;
        private readonly int _blobCount;
        private readonly bool _hasObjectCountBuffer;
        private readonly bool _hasAuxiliaryBuffers;

        // auxiliary cursors (v5 segment 0; empty everywhere else)
        private readonly int _auxIntBase;
        private readonly int _auxIntEnd;
        private readonly int _auxEightBase;
        private readonly int _auxEightEnd;
        private int _auxIntPos;
        private int _auxEightPos;

        // main cursors (v5 segment 1; the whole payload in v3/v4)
        private readonly int _objectBase;
        private readonly int _objectEnd;
        private readonly int _byteBase;
        private readonly int _byteEnd;
        private readonly int _intBase;
        private readonly int _intEnd;
        private readonly int _eightBase;
        private readonly int _eightEnd;
        private readonly int _typesBase;
        private readonly int _typesEnd;
        private int _objectPos;
        private int _bytePos;
        private int _intPos;
        private int _eightPos;
        private int _typesPos;

        private int _blobIndex;

        public Walker(
            int version,
            byte[] main,
            byte[] auxiliary,
            string[] strings,
            byte[][] blobs,
            int blobCount,
            bool hasObjectCountBuffer,
            int objectBase,
            int objectEnd,
            int byteBase,
            int byteEnd,
            int intBase,
            int intEnd,
            int eightBase,
            int eightEnd,
            int typesBase,
            int typesEnd,
            bool hasAuxiliaryBuffers,
            int auxIntBase,
            int auxIntEnd,
            int auxEightBase,
            int auxEightEnd)
        {
            _version = version;
            _main = main;
            _aux = auxiliary;
            _strings = strings;
            _blobs = blobs;
            _blobCount = blobCount;
            _hasObjectCountBuffer = hasObjectCountBuffer;
            _hasAuxiliaryBuffers = hasAuxiliaryBuffers;

            _objectBase = _objectPos = objectBase;
            _objectEnd = objectEnd;
            _byteBase = _bytePos = byteBase;
            _byteEnd = byteEnd;
            _intBase = _intPos = intBase;
            _intEnd = intEnd;
            _eightBase = _eightPos = eightBase;
            _eightEnd = eightEnd;
            _typesBase = _typesPos = typesBase;
            _typesEnd = typesEnd;

            _auxIntBase = _auxIntPos = auxIntBase;
            _auxIntEnd = auxIntEnd;
            _auxEightBase = _auxEightPos = auxEightBase;
            _auxEightEnd = auxEightEnd;

            // Every declared end must be inside the buffer that holds it: a garbage header is
            // caught here, by name, instead of at the first read that runs off the end.
            RequireEnd(_main, _objectEnd, "object-count buffer");
            RequireEnd(_main, _byteEnd, "byte buffer");
            RequireEnd(_main, _intEnd, "4-byte buffer");
            RequireEnd(_main, _eightEnd, "8-byte buffer");
            RequireEnd(_main, _typesEnd, "types buffer");
            RequireEnd(_aux, _auxIntEnd, "aux 4-byte buffer");
            RequireEnd(_aux, _auxEightEnd, "aux 8-byte buffer");
        }

        private void RequireEnd(byte[] buffer, int end, string what)
        {
            if (end < 0 || end > buffer.Length)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: KV3 v{_version} {what} ends at {end}, outside the " +
                    $"{buffer.Length}-byte buffer that holds it.");
            }
        }

        /// <summary>Read the single tagged value at the root of the tree.</summary>
        public Value ReadRoot()
        {
            (int type, _) = ReadTag();
            return ReadValue(type, 0);
        }

        /// <summary>
        /// Assert that every cursor landed exactly on its declared end. A correct decode always
        /// does; anything else means the walk desynchronised somewhere upstream.
        /// </summary>
        public void RequireFullyConsumed()
        {
            var problems = new List<string>();
            Check(problems, "object counts", _objectPos - _objectBase, _objectEnd - _objectBase, 4);
            Check(problems, "byte buffer", _bytePos - _byteBase, _byteEnd - _byteBase, 1);
            Check(problems, "4-byte buffer", _intPos - _intBase, _intEnd - _intBase, 4);
            Check(problems, "8-byte buffer", _eightPos - _eightBase, _eightEnd - _eightBase, 8);
            Check(problems, "types buffer", _typesPos - _typesBase, _typesEnd - _typesBase, 1);
            Check(problems, "aux 4-byte buffer",
                _auxIntPos - _auxIntBase, _auxIntEnd - _auxIntBase, 4);
            Check(problems, "aux 8-byte buffer",
                _auxEightPos - _auxEightBase, _auxEightEnd - _auxEightBase, 8);
            Check(problems, "binary blobs", _blobIndex, _blobCount, 1);

            if (problems.Count > 0)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: KV3 v{_version} buffers were not fully consumed — the tree " +
                    "walk desynchronised: " + string.Join("; ", problems) + ".");
            }
        }

        private static void Check(List<string> problems, string what, int consumed, int declared, int unit)
        {
            if (consumed != declared)
            {
                problems.Add($"{what} consumed {consumed / unit} of {declared / unit}");
            }
        }

        // -- buffer cursors --------------------------------------------------------------

        private byte NextByte()
        {
            Advance(ref _bytePos, 1, _byteEnd, "byte buffer");
            return _main[_bytePos - 1];
        }

        private int NextInt32()
        {
            Advance(ref _intPos, 4, _intEnd, "4-byte buffer");
            return BinaryPrimitives.ReadInt32LittleEndian(_main.AsSpan(_intPos - 4, 4));
        }

        private uint NextUInt32()
        {
            Advance(ref _intPos, 4, _intEnd, "4-byte buffer");
            return BinaryPrimitives.ReadUInt32LittleEndian(_main.AsSpan(_intPos - 4, 4));
        }

        private float NextSingle()
        {
            Advance(ref _intPos, 4, _intEnd, "4-byte buffer");
            return BinaryPrimitives.ReadSingleLittleEndian(_main.AsSpan(_intPos - 4, 4));
        }

        private double NextDouble()
        {
            Advance(ref _eightPos, 8, _eightEnd, "8-byte buffer");
            return BinaryPrimitives.ReadDoubleLittleEndian(_main.AsSpan(_eightPos - 8, 8));
        }

        private long NextInt64()
        {
            Advance(ref _eightPos, 8, _eightEnd, "8-byte buffer");
            return BinaryPrimitives.ReadInt64LittleEndian(_main.AsSpan(_eightPos - 8, 8));
        }

        private ulong NextUInt64()
        {
            Advance(ref _eightPos, 8, _eightEnd, "8-byte buffer");
            return BinaryPrimitives.ReadUInt64LittleEndian(_main.AsSpan(_eightPos - 8, 8));
        }

        private int NextAuxInt32()
        {
            Advance(ref _auxIntPos, 4, _auxIntEnd, "aux 4-byte buffer");
            return BinaryPrimitives.ReadInt32LittleEndian(_aux.AsSpan(_auxIntPos - 4, 4));
        }

        private uint NextAuxUInt32()
        {
            Advance(ref _auxIntPos, 4, _auxIntEnd, "aux 4-byte buffer");
            return BinaryPrimitives.ReadUInt32LittleEndian(_aux.AsSpan(_auxIntPos - 4, 4));
        }

        private float NextAuxSingle()
        {
            Advance(ref _auxIntPos, 4, _auxIntEnd, "aux 4-byte buffer");
            return BinaryPrimitives.ReadSingleLittleEndian(_aux.AsSpan(_auxIntPos - 4, 4));
        }

        private double NextAuxDouble()
        {
            Advance(ref _auxEightPos, 8, _auxEightEnd, "aux 8-byte buffer");
            return BinaryPrimitives.ReadDoubleLittleEndian(_aux.AsSpan(_auxEightPos - 8, 8));
        }

        private long NextAuxInt64()
        {
            Advance(ref _auxEightPos, 8, _auxEightEnd, "aux 8-byte buffer");
            return BinaryPrimitives.ReadInt64LittleEndian(_aux.AsSpan(_auxEightPos - 8, 8));
        }

        private ulong NextAuxUInt64()
        {
            Advance(ref _auxEightPos, 8, _auxEightEnd, "aux 8-byte buffer");
            return BinaryPrimitives.ReadUInt64LittleEndian(_aux.AsSpan(_auxEightPos - 8, 8));
        }

        /// <summary>
        /// The member count of the next OBJECT. v5 keeps these in a dedicated u32 buffer at the
        /// start of segment 1; v3/v4 have no such buffer and read the count from the general
        /// 4-byte buffer, exactly like ARRAY.
        /// </summary>
        private int NextObjectMemberCount()
        {
            if (!_hasObjectCountBuffer)
            {
                return NextCount("OBJECT member count");
            }

            Advance(ref _objectPos, 4, _objectEnd, "object-count buffer");
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_main.AsSpan(_objectPos - 4, 4));
            if (value > int.MaxValue)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: OBJECT member count is {value}, outside the addressable " +
                    $"range (types offset {_typesPos - _typesBase - 1}).");
            }
            return (int)value;
        }

        /// <summary>
        /// Move one cursor forward, refusing to run past the buffer's DECLARED end. Bounding by
        /// the declared end rather than the array length is what turns a desynchronised walk
        /// into an immediate, localised failure.
        /// </summary>
        private static void Advance(ref int position, int count, int end, string what)
        {
            if ((long)position + count > end)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: {what} exhausted — wanted {count} byte(s) at offset " +
                    $"{position} but the buffer ends at {end}.");
            }
            position += count;
        }

        /// <summary>
        /// Read a u32 element count from the 4-byte buffer and range-check it into an int, so a
        /// corrupt count fails by name instead of as an OverflowException.
        /// </summary>
        private int NextCount(string what)
        {
            uint value = NextUInt32();
            if (value > int.MaxValue)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: {what} is {value}, outside the addressable range (types " +
                    $"offset {_typesPos - _typesBase - 1}).");
            }
            return (int)value;
        }

        private string StringById(int id)
        {
            if (id == -1)
            {
                return string.Empty;
            }
            if (id < 0 || id >= _strings.Length)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: string id {id} is out of range ({_strings.Length} strings " +
                    $"in the table).");
            }
            return _strings[id];
        }

        /// <summary>
        /// Read one type tag. The high bit means a FLAGS byte follows; flags annotate strings
        /// only, never change how many bytes a value consumes, and are dropped here.
        /// </summary>
        private (int Type, byte Flags) ReadTag()
        {
            Advance(ref _typesPos, 1, _typesEnd, "types buffer");
            byte b = _main[_typesPos - 1];
            if ((b & 0x80) == 0)
            {
                return (b, 0);
            }
            Advance(ref _typesPos, 1, _typesEnd, "types buffer (flags byte)");
            return (b & 0x7F, _main[_typesPos - 1]);
        }

        // -- the walk --------------------------------------------------------------------

        private Value ReadValue(int type, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: KV3 tree nests deeper than {MaxDepth} levels at types " +
                    $"offset {_typesPos - _typesBase}; refusing to recurse further.");
            }

            switch (type)
            {
                case TypeNull:
                    return Value.ForNull();

                case TypeBoolean:
                    return Value.ForBool(NextByte() != 0);

                case TypeBooleanTrue:
                    return Value.ForBool(true);

                case TypeBooleanFalse:
                    return Value.ForBool(false);

                case TypeInt64:
                    return Value.ForNumber(NextInt64());

                case TypeUInt64:
                    return Value.ForNumber(NextUInt64());

                case TypeInt64Zero:
                    return Value.ForNumber(0d);

                case TypeInt64One:
                    return Value.ForNumber(1d);

                case TypeDouble:
                    return Value.ForNumber(NextDouble());

                case TypeDoubleZero:
                    return Value.ForNumber(0d);

                case TypeDoubleOne:
                    return Value.ForNumber(1d);

                case TypeInt32:
                    return Value.ForNumber(NextInt32());

                case TypeUInt32:
                    return Value.ForNumber(NextUInt32());

                case TypeInt32AsByte:
                    return Value.ForNumber(NextByte());

                case TypeFloat:
                    return Value.ForNumber(NextSingle());

                case TypeString:
                case TypeStringMulti:
                    return Value.ForString(StringById(NextInt32()));

                case TypeBinaryBlob:
                    return Value.ForString(Convert.ToBase64String(NextBlob()));

                case TypeObject:
                    return ReadObject(depth);

                case TypeArray:
                    return ReadArray(NextCount("ARRAY element count"), depth);

                case TypeArrayTyped:
                    return ReadTypedArray(NextCount("ARRAY_TYPED element count"), depth);

                case TypeArrayByteLength:
                    return ReadTypedArray(NextByte(), depth);

                case TypeArrayAuxiliaryBuffer:
                    if (!_hasAuxiliaryBuffers)
                    {
                        throw new Kv3BinaryException(
                            $"Kv3BinaryReader: type 25 (ARRAY_TYPE_AUXILIARY_BUFFER) at types " +
                            $"offset {_typesPos - _typesBase - 1} is a KV3 v5 tag, but this is a " +
                            $"v{_version} block, which has no auxiliary buffers for it to read.");
                    }
                    return ReadAuxiliaryArray(NextByte());

                default:
                    throw new Kv3BinaryException(
                        $"Kv3BinaryReader: unhandled KV3 type id {type} at types offset " +
                        $"{_typesPos - _typesBase - 1}.");
            }
        }

        private Value ReadObject(int depth)
        {
            int count = NextObjectMemberCount();
            var members = new Struct();
            for (int i = 0; i < count; i++)
            {
                string key = StringById(NextInt32());
                (int type, _) = ReadTag();
                // Last value wins on a duplicate key, matching EntitySchema/Kv3.cs — a Struct
                // cannot hold both, and no duplicate occurs in any measured block.
                members.Fields[key] = ReadValue(type, depth + 1);
            }
            return Value.ForStruct(members);
        }

        private Value ReadArray(int count, int depth)
        {
            var list = new ListValue();
            for (int i = 0; i < count; i++)
            {
                (int type, _) = ReadTag();
                list.Values.Add(ReadValue(type, depth + 1));
            }
            return new Value { ListValue = list };
        }

        private Value ReadTypedArray(int count, int depth)
        {
            (int elementType, _) = ReadTag();
            var list = new ListValue();
            for (int i = 0; i < count; i++)
            {
                list.Values.Add(ReadValue(elementType, depth + 1));
            }
            return new Value { ListValue = list };
        }

        /// <summary>
        /// ARRAY_TYPE_AUXILIARY_BUFFER: count from the main byte buffer, element tag from the
        /// types stream, but the ELEMENTS come from the segment-0 auxiliary buffers. v5 only.
        /// </summary>
        private Value ReadAuxiliaryArray(int count)
        {
            (int elementType, _) = ReadTag();
            var list = new ListValue();
            for (int i = 0; i < count; i++)
            {
                list.Values.Add(ReadAuxiliaryValue(elementType));
            }
            return new Value { ListValue = list };
        }

        private Value ReadAuxiliaryValue(int type) => type switch
        {
            TypeDouble => Value.ForNumber(NextAuxDouble()),
            TypeInt32 => Value.ForNumber(NextAuxInt32()),
            TypeUInt32 => Value.ForNumber(NextAuxUInt32()),
            TypeFloat => Value.ForNumber(NextAuxSingle()),
            TypeInt64 => Value.ForNumber(NextAuxInt64()),
            TypeUInt64 => Value.ForNumber(NextAuxUInt64()),
            TypeDoubleZero => Value.ForNumber(0d),
            TypeDoubleOne => Value.ForNumber(1d),
            TypeInt64Zero => Value.ForNumber(0d),
            TypeInt64One => Value.ForNumber(1d),
            TypeBooleanTrue => Value.ForBool(true),
            TypeBooleanFalse => Value.ForBool(false),
            _ => throw new Kv3BinaryException(
                $"Kv3BinaryReader: unsupported auxiliary-array element type id {type} at types " +
                $"offset {_typesPos - _typesBase - 1}."),
        };

        /// <summary>
        /// A BINARY_BLOB value: the next entry of the block area, or — when the header declares
        /// no blocks — an inline (u32 length, bytes) pair. The inline path is INFERRED; no
        /// measured block of any version takes it.
        /// </summary>
        private byte[] NextBlob()
        {
            if (_blobCount != 0)
            {
                if (_blobIndex >= _blobs.Length)
                {
                    throw new Kv3BinaryException(
                        $"Kv3BinaryReader: the tree asks for binary blob {_blobIndex} but the " +
                        $"header declares only {_blobs.Length}.");
                }
                return _blobs[_blobIndex++];
            }

            int length = NextCount("inline binary blob length");
            Advance(ref _bytePos, length, _byteEnd, "byte buffer (inline binary blob)");
            return _main.AsSpan(_bytePos - length, length).ToArray();
        }
    }
}
