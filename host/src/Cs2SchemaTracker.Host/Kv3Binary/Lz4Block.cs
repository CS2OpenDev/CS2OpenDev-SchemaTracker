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
// supplies out of band (for KV3 v5 that is the per-segment uncompressedSize header field).
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
// because frame n routinely matches against output produced by frame n-1. The two main KV3 v5
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

namespace Cs2SchemaTracker.Host.Kv3Binary;

internal static class Lz4Block
{
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
                literalLength = ReadExtendedLength(source, ref src, literalLength, "literal");
            }

            if (literalLength > 0)
            {
                if (src + literalLength > srcEnd)
                {
                    throw new Kv3BinaryException(
                        $"Lz4Block: literal run of {literalLength} bytes at source offset {src} " +
                        $"overruns the {srcEnd}-byte block.");
                }
                if (outPos + literalLength > outEnd)
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
                matchLength = ReadExtendedLength(source, ref src, matchLength, "match");
            }
            matchLength += 4;

            if (matchOffset == 0 || matchOffset > outPos)
            {
                throw new Kv3BinaryException(
                    $"Lz4Block: match offset {matchOffset} at output offset {outPos} points " +
                    $"before the start of the output buffer.");
            }
            if (outPos + matchLength > outEnd)
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
    /// </summary>
    private static int ReadExtendedLength(ReadOnlySpan<byte> source, ref int src, int length, string what)
    {
        while (true)
        {
            if (src >= source.Length)
            {
                throw new Kv3BinaryException(
                    $"Lz4Block: truncated {what}-length extension at source offset {src} " +
                    $"in a {source.Length}-byte block.");
            }
            byte b = source[src++];
            length += b;
            if (b != 0xFF)
            {
                return length;
            }
        }
    }
}
