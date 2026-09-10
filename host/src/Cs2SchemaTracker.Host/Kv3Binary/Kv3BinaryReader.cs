// Binary KV3 (Valve KeyValues v3, COMPILED form) reader for Source 2 `*_c` resources.
//
// === Why this exists ===
// EntitySchema/Kv3.cs parses the KV3 *text* grammar Valve emits into schema metadata. The
// content depot ships a different thing: compiled resources such as `scripts/weapons.vdata_c`
// whose DATA block is the same key/value tree encoded as a compressed, column-oriented binary
// blob. Nothing in the text parser reads it. This file is the binary side, hand-rolled for the
// same reasons VpkArchive is (independence — no ValveResourceFormat runtime dependency; all
// fail-loud paths in our own code; host\Directory.Packages.props unchanged — see Lz4Block.cs).
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
// CONFIRMED against real bytes. Shared by every Source 2 `*_c` file:
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
// Only DATA matters here; it holds the KV3 blob. weapons.vdata_c (32,538 bytes) carries
// RED2 (3,658), DATA (17,984) and FLCI (10,826).
//
// === KV3 v5 block header (offsets relative to the start of DATA) ===
// CONFIRMED. Values in the right column are weapons.vdata_c's.
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
// === Segment 0: string table + auxiliary element buffers ===
//   0                    string blob, stringBlobSize bytes: NUL-terminated UTF-8 strings,
//                        no count prefix, no offset table; referenced by ZERO-BASED index
//   align(4)
//   +0   u32             stringCount   (this is the FIRST of the 4-byte slots, hence the
//                                       header's slot count being one MORE than usable)
//   +4   (slots-1) x u32 auxiliary 4-byte buffer  (INT32 / UINT32 / FLOAT elements)
//   align(8)             ONLY when the 8-byte buffer is non-empty
//   +    count x 8       auxiliary 8-byte buffer  (DOUBLE / INT64 / UINT64 elements)
//
// === Segment 1: the tree ===
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
// The five segment-1 buffers plus the two segment-0 auxiliary buffers are seven INDEPENDENT,
// monotonically advancing cursors. The type tag stream drives the walk and says which cursor
// to pull from next; nothing ever seeks backwards. A correct decode lands every cursor on its
// exact declared end, and this reader ASSERTS that (see Fail-loud).
//
// The align(8) is materialised only when the buffer that follows it is non-empty. Getting
// that wrong shifts a segment end by 4 bytes and destroys the trailer check.
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
//    9  OBJECT                        u32 count from the OBJECT-COUNT buffer,
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
//                                     segment-0 AUXILIARY buffers              CONFIRMED
//   20, 21, 22                        unused / reserved -> hard error
//
// Types 9, 24 and 25 are what make v5 different from v1-v4: the dedicated object-count buffer
// and the auxiliary element split. Type 25 exists purely for compression — it hoists the
// engine's small fixed-size numeric structs (CFiringModeFloat = float32[2], colours, vectors)
// into one contiguous, near-identical run that LZ4s far better than it would interleaved.
// Reals are stored as binary64 even where the schema type is float32, so a value the game
// treats as 0.0006f decodes to the double 0.0006.
//
// === CONFIRMED vs INFERRED ===
// "CONFIRMED" above means the byte layout was verified numerically by the reference work:
// a decoder consuming every buffer to its exact declared end across 2,581 distinct KV3_V5
// blocks sampled from a shipped pak01 (vdata_c, vmat_c, vmdl_c, vcompmat_c, vnmclip_c,
// vnmgraph_c, vnmskel_c, vpcf_c, vsndevts_c, vpulse_c, vrr_c, vsnap_c), zero mismatches,
// zero parse errors. An off-by-one in any rule desynchronises the walk immediately.
// "INFERRED" means the rule is carried over from the v1-v4 table and is consistent with the
// format, but no file in that corpus exercised it. Types 0, 2, 3, 19 and 23 are in that
// bucket, as is the inline BINARY_BLOB path. They are implemented, not trusted.
//
// weapons.vdata_c itself exercises types 5, 6, 9, 11, 13, 14, 15, 16, 17, 18 and 25.
//
// === Deliberately NOT implemented ===
//   * KV3 v1, v2, v3 and v4, and the legacy uncompressed VKV3 (v0). Their headers are laid
//     out differently (one payload area instead of two, string table at the END, no dedicated
//     object-count buffer, no type 25), and reading them with v5 offsets yields nonsense
//     sizes rather than a clean failure. Older CS2 builds do ship v3/v4 blocks, so this WILL
//     need adding — once measured, not guessed. Until then the reader names the exact version
//     it found and throws.
//   * compressionMethod 2 (zstd). Absent from every block measured in this build. Adding it
//     would mean adding a package dependency, which is out of scope here.
// Both are hard errors on purpose: a silently wrong tree is far worse than a loud refusal.
//
// === Binary blobs ===
// A BINARY_BLOB value becomes a base64 Value.StringValue. google.protobuf.Value has no bytes
// case, and the alternatives (a list of byte numbers; a lossy UTF-8 decode) are respectively
// enormous and destructive. weapons.vdata_c has blockCount == 0 and contains NO BINARY_BLOB
// value, so this choice affects no current caller; it is recorded here so that whoever first
// feeds a blob-bearing resource through this reader knows what they will get.
//
// === Determinism ===
// Pure function of the input bytes. Object members are inserted into the Struct in source
// order (the CanonicalJson layer sorts keys later, exactly as the text parser's output is
// treated); array order is the file's order; no timestamp, path, locale or environment state
// enters the result. Decoding the same bytes twice yields an identical tree.
//
// === Fail-loud ===
// This reads INPUT BINARY, so it never degrades — every failure throws Kv3BinaryException:
//   * every container, header, segment and buffer read is bounds-checked BEFORE it happens
//     and reports the offset and what was expected, so a truncated or corrupt file can never
//     surface as IndexOutOfRangeException / OverflowException;
//   * each of the seven cursors is bounded by its DECLARED end, not merely by the array
//     length, so a desynchronised walk fails at the exact tag that caused it;
//   * segment 0's and segment 1's computed layouts must match their actual lengths, and the
//     00 DD EE FF trailer must be where the layout says;
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

    /// <summary>Offset of the KV3 v5 payload, relative to the start of the DATA block.</summary>
    private const int V5PayloadOffset = 120;

    /// <summary>Minimum byte length of a KV3 v5 block header.</summary>
    private const int V5HeaderSize = 120;

    /// <summary>The u32 that terminates segment 1's buffer area: bytes 00 DD EE FF.</summary>
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
        var header = Kv3V5Header.Parse(block);
        (byte[] segment0, byte[] segment1, int blobRegionOffset) = DecompressSegments(header, block);

        long total = (long)segment0.Length + segment1.Length;
        if (total != header.UncompressedTotal)
        {
            throw new Kv3BinaryException(
                $"Kv3BinaryReader: segments decompressed to {total} bytes but the header " +
                $"declares uncompressedTotal = {header.UncompressedTotal}.");
        }

        var walker = new Walker(header, segment0, segment1, block, blobRegionOffset);
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
    // header + segments
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
            StringBlobSize = Length(block, 28, "SEG0 string blob size");
            AuxSlotCount = Length(block, 32, "SEG0 4-byte slot count");
            AuxEightByteCount = Length(block, 36, "SEG0 8-byte value count");
            TypesSize = Length(block, 40, "SEG1 types buffer size");
            UncompressedTotal = Length(block, 48, "total uncompressed size");
            BlobCount = Length(block, 56, "blob count");
            BlobTotalSize = Length(block, 60, "blob total size");
            BlobFrameTableSize = Length(block, 68, "blob frame table size");
            Segment0Uncompressed = Length(block, 72, "SEG0 uncompressed size");
            Segment0Compressed = Length(block, 76, "SEG0 compressed size");
            Segment1Uncompressed = Length(block, 80, "SEG1 uncompressed size");
            Segment1Compressed = Length(block, 84, "SEG1 compressed size");
            ByteCount = Length(block, 88, "SEG1 byte buffer size");
            IntCount = Length(block, 96, "SEG1 4-byte value count");
            EightByteCount = Length(block, 100, "SEG1 8-byte value count");
            ObjectCount = Length(block, 108, "SEG1 object count");
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

        /// <summary>
        /// Identify the block by its 4-byte magic and parse it as v5. Every other KV3 version
        /// and the legacy VKV3 form are named and refused — see "Deliberately NOT implemented".
        /// </summary>
        public static Kv3V5Header Parse(ReadOnlySpan<byte> block)
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
                    $"Kv3BinaryReader: unsupported KV3 version 0 (legacy uncompressed VKV3; only " +
                    $"v5 is implemented); source=DATA block magic {magicHex}.");
            }

            // Versioned magics are "\xNN3VK" -> NN 33 56 4B.
            if (block[1] != 0x33 || block[2] != 0x56 || block[3] != 0x4B || block[0] is 0 or > 5)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: DATA block is not binary KV3; source=DATA block magic " +
                    $"{magicHex}.");
            }

            int version = block[0];
            if (version != 5)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: unsupported KV3 version {version} (only v5 is " +
                    $"implemented); source=DATA block magic {magicHex}.");
            }

            if (block.Length < V5HeaderSize)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: KV3 v5 DATA block is {block.Length} bytes, shorter than " +
                    $"its {V5HeaderSize}-byte header.");
            }

            return new Kv3V5Header(block);
        }

        /// <summary>
        /// Read a u32 size/count field and range-check it into an int, so a garbage header
        /// fails here by name rather than as an OverflowException deeper in the walk.
        /// </summary>
        private static int Length(ReadOnlySpan<byte> block, int offset, string what)
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
        int pos = V5PayloadOffset;

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

    // -----------------------------------------------------------------------------------
    // tree walk
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Walks the v5 tree. Every buffer is consumed strictly in tree order and each cursor is
    /// bounded by its DECLARED end, so a desynchronised walk fails at the tag that caused it.
    /// </summary>
    private sealed class Walker
    {
        private readonly Kv3V5Header _header;
        private readonly byte[] _s0;
        private readonly byte[] _s1;
        private readonly string[] _strings;
        private readonly byte[][] _blobs;

        // segment 0 cursors
        private readonly int _auxIntBase;
        private readonly int _auxIntEnd;
        private readonly int _auxEightBase;
        private readonly int _auxEightEnd;
        private int _auxIntPos;
        private int _auxEightPos;

        // segment 1 cursors
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
            Kv3V5Header header,
            byte[] segment0,
            byte[] segment1,
            ReadOnlySpan<byte> block,
            int blobRegionOffset)
        {
            _header = header;
            _s0 = segment0;
            _s1 = segment1;

            // ---- segment 0: string blob, then the two auxiliary buffers ----
            RequireSegment(segment0, 0, header.StringBlobSize, 0, "SEG0 string blob");
            _strings = SplitStrings(segment0.AsSpan(0, header.StringBlobSize));

            int p = Align(header.StringBlobSize, 4);
            RequireSegment(segment0, p, 4, 0, "SEG0 string count");
            int declaredStringCount = ReadInt32Checked(
                segment0, p, "SEG0 string count", allowNegative: false);
            if (declaredStringCount != _strings.Length)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: SEG0 declares {declaredStringCount} strings but the blob " +
                    $"holds {_strings.Length}.");
            }

            _auxIntBase = p + 4;
            int auxIntCount = Math.Max(header.AuxSlotCount - 1, 0);
            _auxIntEnd = _auxIntBase + (auxIntCount * 4);

            // The align(8) before the auxiliary 8-byte buffer is materialised ONLY when that
            // buffer is non-empty; nothing follows it, so an empty one leaves no padding.
            int q = _auxIntEnd;
            int segment0End = q;
            if (header.AuxEightByteCount != 0)
            {
                q = Align(q, 8);
                segment0End = q + (header.AuxEightByteCount * 8);
            }
            _auxEightBase = q;
            _auxEightEnd = _auxEightBase + (header.AuxEightByteCount * 8);
            if (segment0End != segment0.Length)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: SEG0 layout computes an end of {segment0End} but the " +
                    $"segment is {segment0.Length} bytes.");
            }
            _auxIntPos = _auxIntBase;
            _auxEightPos = _auxEightBase;

            // ---- segment 1: object counts, bytes, ints, 8-byte values, types ----
            _objectBase = 0;
            _objectEnd = _objectBase + (header.ObjectCount * 4);
            _byteBase = _objectEnd;
            _byteEnd = _byteBase + header.ByteCount;
            _intBase = Align(_byteEnd, 4);
            _intEnd = _intBase + (header.IntCount * 4);
            int r = _intEnd;
            if (header.EightByteCount != 0)
            {
                r = Align(r, 8);
            }
            _eightBase = r;
            _eightEnd = _eightBase + (header.EightByteCount * 8);
            _typesBase = _eightEnd;
            _typesEnd = _typesBase + header.TypesSize;

            int cursor = _typesEnd;
            RequireSegment(segment1, cursor, 0, 1, "SEG1 buffer area");

            // ---- optional blob area: blockCount u32 lengths, the trailer, the frame table ----
            var blobLengths = new int[header.BlobCount];
            for (int i = 0; i < header.BlobCount; i++)
            {
                RequireSegment(segment1, cursor, 4, 1, "SEG1 blob length");
                blobLengths[i] = ReadInt32Checked(
                    segment1, cursor, "SEG1 blob length", allowNegative: false);
                cursor += 4;
            }

            int tail = segment1.Length - cursor;
            if (tail >= 4 &&
                BinaryPrimitives.ReadUInt32LittleEndian(segment1.AsSpan(cursor, 4)) == SegmentTrailer)
            {
                cursor += 4;
                int frameCount = header.BlobFrameTableSize / 2;
                var frameSizes = new int[frameCount];
                for (int i = 0; i < frameCount; i++)
                {
                    RequireSegment(segment1, cursor + (i * 2), 2, 1, "SEG1 blob frame size");
                    frameSizes[i] = BinaryPrimitives.ReadUInt16LittleEndian(
                        segment1.AsSpan(cursor + (i * 2), 2));
                }
                cursor += header.BlobFrameTableSize;
                if (cursor != segment1.Length)
                {
                    throw new Kv3BinaryException(
                        $"Kv3BinaryReader: SEG1 has {segment1.Length - cursor} unexplained bytes " +
                        $"after the blob frame table (offset {cursor} of {segment1.Length}).");
                }
                _blobs = header.BlobCount == 0
                    ? []
                    : SliceBlobs(header, block, blobRegionOffset, blobLengths, frameSizes);
            }
            else if (tail == 0)
            {
                // Trailer omitted. Only seen with an empty 8-byte buffer, where the missing
                // align(8) would otherwise be mistaken for a missing trailer.
                _blobs = [];
            }
            else
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: SEG1 layout mismatch at offset {cursor}: {tail} trailing " +
                    $"bytes and no 00 DD EE FF trailer (an apparently absent trailer usually means " +
                    $"the 8-byte buffer padding was applied when it should not have been).");
            }

            _objectPos = _objectBase;
            _bytePos = _byteBase;
            _intPos = _intBase;
            _eightPos = _eightBase;
            _typesPos = _typesBase;
        }

        /// <summary>Read the single tagged value at the root of the tree.</summary>
        public Value ReadRoot()
        {
            (int type, _) = ReadTag();
            return ReadValue(type, 0);
        }

        /// <summary>
        /// Assert that every cursor landed exactly on its declared end. A correct v5 decode
        /// always does; anything else means the walk desynchronised somewhere upstream.
        /// </summary>
        public void RequireFullyConsumed()
        {
            var problems = new List<string>();
            Check(problems, "object counts", (_objectPos - _objectBase) / 4, _header.ObjectCount);
            Check(problems, "byte buffer", _bytePos - _byteBase, _header.ByteCount);
            Check(problems, "4-byte buffer", (_intPos - _intBase) / 4, _header.IntCount);
            Check(problems, "8-byte buffer", (_eightPos - _eightBase) / 8, _header.EightByteCount);
            Check(problems, "types buffer", _typesPos - _typesBase, _header.TypesSize);
            Check(problems, "aux 4-byte buffer",
                (_auxIntPos - _auxIntBase) / 4, Math.Max(_header.AuxSlotCount - 1, 0));
            Check(problems, "aux 8-byte buffer",
                (_auxEightPos - _auxEightBase) / 8, _header.AuxEightByteCount);
            Check(problems, "binary blobs", _blobIndex, _header.BlobCount);

            if (problems.Count > 0)
            {
                throw new Kv3BinaryException(
                    "Kv3BinaryReader: KV3 v5 buffers were not fully consumed — the tree walk " +
                    "desynchronised: " + string.Join("; ", problems) + ".");
            }
        }

        private static void Check(List<string> problems, string what, int consumed, int declared)
        {
            if (consumed != declared)
            {
                problems.Add($"{what} consumed {consumed} of {declared}");
            }
        }

        // -- layout helpers --------------------------------------------------------------

        private static int Align(int value, int alignment) =>
            (value + (alignment - 1)) & ~(alignment - 1);

        private static void RequireSegment(byte[] segment, int offset, int length, int index, string what)
        {
            if (offset < 0 || length < 0 || (long)offset + length > segment.Length)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: {what} spans [{offset}, {(long)offset + length}) which is " +
                    $"outside the {segment.Length}-byte segment {index}.");
            }
        }

        private static int ReadInt32Checked(byte[] segment, int offset, string what, bool allowNegative)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(segment.AsSpan(offset, 4));
            if (!allowNegative && value < 0)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: {what} at offset {offset} is {value}, which cannot be negative.");
            }
            return value;
        }

        /// <summary>
        /// Split the string blob on NUL. The blob ends with the terminator of its last string,
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
        /// Recover the individual binary blobs. The frame payloads are NOT in segment 1: they
        /// sit in the DATA block immediately after the two stored segments, and (under LZ4)
        /// they are LINKED — a match in frame n may reference output produced by frame n-1 — so
        /// they must be decoded into one continuous buffer, then sliced by the u32 lengths.
        /// </summary>
        private static byte[][] SliceBlobs(
            Kv3V5Header header,
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
            if (declared != header.BlobTotalSize)
            {
                throw new Kv3BinaryException(
                    $"Kv3BinaryReader: blob lengths sum to {declared} but the header declares " +
                    $"blockTotalSize = {header.BlobTotalSize}.");
            }

            byte[] payload;
            if (header.CompressionMethod == 0)
            {
                RequireRange(block, blobRegionOffset, header.BlobTotalSize, "blob payload (stored)");
                payload = block.Slice(blobRegionOffset, header.BlobTotalSize).ToArray();
            }
            else
            {
                payload = new byte[header.BlobTotalSize];
                int written = 0;
                int offset = blobRegionOffset;
                foreach (int frameSize in frameSizes)
                {
                    RequireRange(block, offset, frameSize, "blob LZ4 frame");
                    written = Lz4Block.DecodeInto(block.Slice(offset, frameSize), payload, written);
                    offset += frameSize;
                }
                if (written != header.BlobTotalSize)
                {
                    throw new Kv3BinaryException(
                        $"Kv3BinaryReader: blob frames decompressed to {written} bytes, expected " +
                        $"{header.BlobTotalSize}.");
                }
            }

            var blobs = new byte[blobLengths.Length][];
            int cursor = 0;
            for (int i = 0; i < blobLengths.Length; i++)
            {
                blobs[i] = payload.AsSpan(cursor, blobLengths[i]).ToArray();
                cursor += blobLengths[i];
            }
            return blobs;
        }

        // -- buffer cursors --------------------------------------------------------------

        private uint NextObjectMemberCount()
        {
            Advance(ref _objectPos, 4, _objectEnd, "object-count buffer");
            return BinaryPrimitives.ReadUInt32LittleEndian(_s1.AsSpan(_objectPos - 4, 4));
        }

        private byte NextByte()
        {
            Advance(ref _bytePos, 1, _byteEnd, "byte buffer");
            return _s1[_bytePos - 1];
        }

        private int NextInt32()
        {
            Advance(ref _intPos, 4, _intEnd, "4-byte buffer");
            return BinaryPrimitives.ReadInt32LittleEndian(_s1.AsSpan(_intPos - 4, 4));
        }

        private uint NextUInt32()
        {
            Advance(ref _intPos, 4, _intEnd, "4-byte buffer");
            return BinaryPrimitives.ReadUInt32LittleEndian(_s1.AsSpan(_intPos - 4, 4));
        }

        private float NextSingle()
        {
            Advance(ref _intPos, 4, _intEnd, "4-byte buffer");
            return BinaryPrimitives.ReadSingleLittleEndian(_s1.AsSpan(_intPos - 4, 4));
        }

        private double NextDouble()
        {
            Advance(ref _eightPos, 8, _eightEnd, "8-byte buffer");
            return BinaryPrimitives.ReadDoubleLittleEndian(_s1.AsSpan(_eightPos - 8, 8));
        }

        private long NextInt64()
        {
            Advance(ref _eightPos, 8, _eightEnd, "8-byte buffer");
            return BinaryPrimitives.ReadInt64LittleEndian(_s1.AsSpan(_eightPos - 8, 8));
        }

        private ulong NextUInt64()
        {
            Advance(ref _eightPos, 8, _eightEnd, "8-byte buffer");
            return BinaryPrimitives.ReadUInt64LittleEndian(_s1.AsSpan(_eightPos - 8, 8));
        }

        private int NextAuxInt32()
        {
            Advance(ref _auxIntPos, 4, _auxIntEnd, "aux 4-byte buffer");
            return BinaryPrimitives.ReadInt32LittleEndian(_s0.AsSpan(_auxIntPos - 4, 4));
        }

        private uint NextAuxUInt32()
        {
            Advance(ref _auxIntPos, 4, _auxIntEnd, "aux 4-byte buffer");
            return BinaryPrimitives.ReadUInt32LittleEndian(_s0.AsSpan(_auxIntPos - 4, 4));
        }

        private float NextAuxSingle()
        {
            Advance(ref _auxIntPos, 4, _auxIntEnd, "aux 4-byte buffer");
            return BinaryPrimitives.ReadSingleLittleEndian(_s0.AsSpan(_auxIntPos - 4, 4));
        }

        private double NextAuxDouble()
        {
            Advance(ref _auxEightPos, 8, _auxEightEnd, "aux 8-byte buffer");
            return BinaryPrimitives.ReadDoubleLittleEndian(_s0.AsSpan(_auxEightPos - 8, 8));
        }

        private long NextAuxInt64()
        {
            Advance(ref _auxEightPos, 8, _auxEightEnd, "aux 8-byte buffer");
            return BinaryPrimitives.ReadInt64LittleEndian(_s0.AsSpan(_auxEightPos - 8, 8));
        }

        private ulong NextAuxUInt64()
        {
            Advance(ref _auxEightPos, 8, _auxEightEnd, "aux 8-byte buffer");
            return BinaryPrimitives.ReadUInt64LittleEndian(_s0.AsSpan(_auxEightPos - 8, 8));
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
                    $"in the SEG0 table).");
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
            byte b = _s1[_typesPos - 1];
            if ((b & 0x80) == 0)
            {
                return (b, 0);
            }
            Advance(ref _typesPos, 1, _typesEnd, "types buffer (flags byte)");
            return (b & 0x7F, _s1[_typesPos - 1]);
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
                    return ReadAuxiliaryArray(NextByte());

                default:
                    throw new Kv3BinaryException(
                        $"Kv3BinaryReader: unhandled KV3 type id {type} at types offset " +
                        $"{_typesPos - _typesBase - 1}.");
            }
        }

        private Value ReadObject(int depth)
        {
            uint count = NextObjectMemberCount();
            var members = new Struct();
            for (uint i = 0; i < count; i++)
            {
                string key = StringById(NextInt32());
                (int type, _) = ReadTag();
                // Last value wins on a duplicate key, matching EntitySchema/Kv3.cs — a Struct
                // cannot hold both, and no duplicate occurs in any measured v5 block.
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
        /// ARRAY_TYPE_AUXILIARY_BUFFER: count from segment 1's byte buffer, element tag from the
        /// types stream, but the ELEMENTS come from the segment-0 auxiliary buffers.
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
        /// no blocks — an inline (u32 length, bytes) pair. The inline path is INFERRED from
        /// v1-v4 behaviour; no measured v5 block takes it.
        /// </summary>
        private byte[] NextBlob()
        {
            if (_header.BlobCount != 0)
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
            return _s1.AsSpan(_bytePos - length, length).ToArray();
        }
    }
}
