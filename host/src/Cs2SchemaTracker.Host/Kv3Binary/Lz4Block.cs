// Raw LZ4 block decompressor — the minimum needed to unpack the compressed segments of a
// compiled Source 2 resource's binary-KV3 payload (see Kv3BinaryReader.cs).
//
// === Why hand-rolled ===
// Same rationale as VpkArchive / VpkTrimWriter: no ValveResourceFormat and no third-party
// codec package. host\Directory.Packages.props stays untouched — a raw LZ4 block decoder is
// ~120 lines of well-specified byte shuffling, and hand-rolling it keeps every fail-loud path
// in our own, unit-testable code. Only the RAW BLOCK layer is implemented; the LZ4 *frame*
// format (magic, block-size prefixes, xxhash checksums) is not used by KV3 and is
// deliberately absent.
//
// === Raw block layout ===
// A raw LZ4 block is a bare run of sequences. There is no header, no length prefix, no end
// marker and no checksum — the decoder is driven entirely by an output length the CONTAINER
// supplies out of band (for KV3 that is the uncompressedSize header field of the segment or
// payload being unpacked).
//
//   sequence := token         u8
//               litExt        0..n bytes    present only when (token >> 4)  == 15
//               literals      literalLength bytes, copied verbatim
//               matchOffset   u16 little-endian, distance back into the output produced so far
//               matchExt      0..n bytes    present only when (token & 0xF) == 15
//
//   literalLength = (token >> 4)       + sum(litExt)     ; an extension runs while the byte is 0xFF
//   matchLength   = (token & 0xF) + 4  + sum(matchExt)   ; the +4 is the format's minimum match
//
// The LAST sequence of a block carries literals ONLY: the input runs out immediately after the
// literal run, with no match offset following it. That is NORMAL termination, not truncation,
// and the decoder must accept it.
//
// Matches copy BYTE BY BYTE out of the output already produced. Overlapping copies are legal
// and load-bearing (matchOffset 1 with matchLength 40 is a run-length encoding), so the copy
// loop must not be collapsed into a bulk move.
//
// === Linked blocks ===
// DecodeInto appends into a caller-owned buffer and lets a match reach back into bytes an
// EARLIER call produced. Compiled-resource binary-blob ("block") payloads are stored as a run
// of small LZ4 frames in exactly that mode — decoding each frame into a fresh buffer fails,
// because frame n routinely matches against output produced by frame n-1. A KV3 block's main
// segments are, by contrast, plain independent blocks and go through Decode.
//
// === Determinism ===
// Pure function of the input bytes: the same source and the same expected length always yield
// a byte-identical buffer, with no allocation-order or environment dependence.
//
// === Fail-loud ===
// Every read and every write is bounds-checked against the source and destination lengths
// BEFORE it happens, and each failure throws Kv3BinaryException naming the offending offset and
// what was expected. A short or corrupt block therefore can never surface as an
// IndexOutOfRangeException, never silently yields a truncated buffer, and never reads outside
// the supplied spans. Decode additionally requires the produced length to equal the declared
// length exactly, so a block that decodes "successfully" but short is still an error.
//
// Two of those refusals exist for the CONTAINER's benefit rather than the block's. Decode
// checks the declared output length against the most the block could possibly expand to BEFORE
// allocating the destination, because that length is a KV3 header field that nothing else
// bounds and a value near int.MaxValue is past the CLR's ~0x7FFFFFC7 byte[] cap — an
// OutOfMemoryException, not a Kv3BinaryException. And a 0xFF length extension that runs past
// the bytes left for it is refused rather than accumulated, because the accumulator used to be
// an int with no cap: a run of ~8.4M of them wrapped it negative, the overrun comparison below
// wrapped with it and PASSED, and the copy then indexed the destination at a negative offset.
// Those two are exactly the escapes the paragraph above promises are impossible.

namespace Cs2SchemaTracker.Host.Kv3Binary;

internal static class Lz4Block
{
    /// <summary>
    /// The most output a raw LZ4 block of <paramref name="compressedLength"/> bytes can
    /// possibly produce. Literals cost one input byte each, so only matches expand: the
    /// shortest match sequence is 3 bytes (token + 2-byte offset) and yields at most 19 bytes,
    /// and every further input byte is at most one 0xFF length-extension byte worth 255 more.
    /// The bound is deliberately loose — the worst ratio in the measured corpus is 14.1x — and
    /// exists only so a container header that declares an impossible output length fails by
    /// name instead of as an OutOfMemoryException at the allocation it drives.
    /// </summary>
    public static long MaxExpandedLength(int compressedLength) =>
        ((long)compressedLength * 255) + 19;

    /// <summary>
    /// Decode one independent raw LZ4 block into a fresh buffer of exactly
    /// <paramref name="expectedLength"/> bytes. Throws <see cref="Kv3BinaryException"/> if the
    /// block is malformed or does not produce exactly that many bytes.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> source, int expectedLength)
    {
        if (expectedLength < 0)
        {
            throw new Kv3BinaryException(
                $"Lz4Block: negative expected output length {expectedLength}.");
        }

        // The caller's expected length comes from a container header field that is only
        // range-checked against int.MaxValue (Kv3BinaryReader.HeaderLength), and the
        // RequireRange beside each call site bounds only the COMPRESSED slice — so nothing
        // else relates this number to the block that has to produce it. A 92-byte crafted
        // resource declaring uncompressedPayloadSize 1,900,000,000 with compressedPayloadSize 0
        // reserved 1,812 MB here before the real problem was reported below.
        long ceiling = MaxExpandedLength(source.Length);
        if (expectedLength > ceiling)
        {
            throw new Kv3BinaryException(
                $"Lz4Block: declared output length {expectedLength} exceeds the {ceiling} bytes " +
                $"a {source.Length}-byte raw LZ4 block can possibly expand to.");
        }

        var destination = new byte[expectedLength];
        int produced = DecodeInto(source, destination, 0);
        if (produced != expectedLength)
        {
            throw new Kv3BinaryException(
                $"Lz4Block: block produced {produced} bytes, expected {expectedLength}.");
        }
        return destination;
    }

    /// <summary>
    /// Decode a raw LZ4 block into <paramref name="destination"/> starting at
    /// <paramref name="start"/>, returning the index one past the last byte written. Matches may
    /// reference any byte already present in <c>destination[0..start)</c> — that is LZ4
    /// linked-block mode, which the compiled-resource blob frames require. Decoding stops when
    /// the source is exhausted or the destination is full, whichever comes first.
    /// </summary>
    public static int DecodeInto(ReadOnlySpan<byte> source, byte[] destination, int start)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if ((uint)start > (uint)destination.Length)
        {
            throw new Kv3BinaryException(
                $"Lz4Block: start offset {start} is outside the {destination.Length}-byte destination.");
        }

        int src = 0;
        int srcEnd = source.Length;
        int outPos = start;
        int outEnd = destination.Length;

        while (src < srcEnd)
        {
            if (outPos >= outEnd)
            {
                break;
            }

            byte token = source[src++];

            int literalLength = token >> 4;
            if (literalLength == 15)
            {
                literalLength = ReadExtendedLength(
                    source, ref src, literalLength, srcEnd - src, "literal");
            }

            if (literalLength > 0)
            {
                if ((long)src + literalLength > srcEnd)
                {
                    throw new Kv3BinaryException(
                        $"Lz4Block: literal run of {literalLength} bytes at source offset {src} " +
                        $"overruns the {srcEnd}-byte block.");
                }
                if ((long)outPos + literalLength > outEnd)
                {
                    throw new Kv3BinaryException(
                        $"Lz4Block: literal run of {literalLength} bytes at output offset {outPos} " +
                        $"overruns the {outEnd}-byte output buffer.");
                }
                source.Slice(src, literalLength).CopyTo(destination.AsSpan(outPos, literalLength));
                src += literalLength;
                outPos += literalLength;
            }

            if (src == srcEnd)
            {
                // Normal end of block: the final sequence carries literals only.
                break;
            }
            if (src + 2 > srcEnd)
            {
                throw new Kv3BinaryException(
                    $"Lz4Block: truncated match offset at source offset {src} " +
                    $"(1 byte left in a {srcEnd}-byte block, expected 2).");
            }

            int matchOffset = source[src] | (source[src + 1] << 8);
            src += 2;

            int matchLength = token & 0xF;
            if (matchLength == 15)
            {
                matchLength = ReadExtendedLength(
                    source, ref src, matchLength, outEnd - outPos, "match");
            }
            matchLength += 4;

            if (matchOffset == 0 || matchOffset > outPos)
            {
                throw new Kv3BinaryException(
                    $"Lz4Block: match offset {matchOffset} at output offset {outPos} points " +
                    $"before the start of the output buffer.");
            }
            if ((long)outPos + matchLength > outEnd)
            {
                throw new Kv3BinaryException(
                    $"Lz4Block: match of {matchLength} bytes at output offset {outPos} " +
                    $"overruns the {outEnd}-byte output buffer.");
            }

            // Byte by byte on purpose: an LZ4 match may overlap the region it writes into
            // (matchOffset 1 encodes a run), so a bulk copy would produce different bytes.
            int copyFrom = outPos - matchOffset;
            for (int i = 0; i < matchLength; i++)
            {
                destination[outPos + i] = destination[copyFrom + i];
            }
            outPos += matchLength;
        }

        return outPos;
    }

    /// <summary>
    /// Continue a length nibble that saturated at 15 with its 0xFF-terminated extension bytes.
    /// Advances <paramref name="src"/> past the extension and returns the accumulated length.
    /// <paramref name="limit"/> is the most bytes the length could possibly be spent on — the
    /// source left for a literal run, the output left for a match — which a valid stream always
    /// satisfies and a runaway extension does not.
    /// </summary>
    private static int ReadExtendedLength(
        ReadOnlySpan<byte> source, ref int src, int length, int limit, string what)
    {
        // Accumulated in a long against that limit because the old int accumulator had no cap
        // at all: ~8.4M 0xFF bytes wrapped it negative, the plain int overrun comparison in the
        // caller wrapped with it and passed, the copy loop was skipped, and the next sequence
        // indexed the destination at a negative offset — an ArgumentOutOfRangeException, which
        // WeaponVDataEmitter's catch (Kv3BinaryException) does not catch. Reproduced with a
        // 9,021,667-byte crafted block.
        long total = length;
        while (true)
        {
            if (src >= source.Length)
            {
                throw new Kv3BinaryException(
                    $"Lz4Block: truncated {what}-length extension at source offset {src} " +
                    $"in a {source.Length}-byte block.");
            }
            byte b = source[src++];
            total += b;
            if (total > limit)
            {
                throw new Kv3BinaryException(
                    $"Lz4Block: {what} length extension at source offset {src} reached {total}, " +
                    $"past the {limit} bytes left for it in the block — a runaway 0xFF " +
                    $"extension run.");
            }
            if (b != 0xFF)
            {
                return (int)total;
            }
        }
    }
}
