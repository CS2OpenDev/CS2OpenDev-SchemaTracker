// scan a binary blob for embedded serialized FileDescriptorProto bytes.
//
// Strategy:
//   1. Walk the byte stream looking for an "anchor" — the wire-encoded `name`
//      field of a FileDescriptorProto: tag 0x0a (field 1, length-delimited)
//      followed by a varint length followed by ASCII bytes ending in `.proto`.
//   2. From each anchor, walk wire fields forward using a tiny hand-rolled
//      reader that tracks exactly where each field ends. The "FDP boundary"
//      is the byte offset at which we stop accepting fields whose tags belong
//      to FileDescriptorProto's known field set.
//   3. ParseFrom the resulting slice to get a real FDP. Verify by re-serializing
//      and comparing byte-for-byte; if the slice round-trips, we have a real
//      FDP and not a truncated one.
//
// Scan-time parse failures and anchor-noise are EXPECTED — random byte patterns
// happen to match the heuristic — and are discarded silently. This is NOT a
// violation of. Real input failures (path doesn't exist, can't open)
// bubble up.

using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Cs2SchemaTracker.Host.ProtoDescriptors;

internal sealed class DescriptorScanner
{
    // FileDescriptorProto.name = 1, wire type 2 (length-delimited) ⇒ tag byte 0x0a.
    private const byte NameFieldTag = 0x0a;

    // Cap per-FDP scan length; real CS2 protos are tens-of-KB at most.
    private const int MaxFdpBytes = 4 * 1024 * 1024;

    // Cap the varint-decoded `name` field length so a garbage varint can't make
    // us read megabytes searching for a non-existent name.
    private const int MaxNameLength = 1024;

    // Known top-level field numbers of FileDescriptorProto (descriptor.proto in
    // the protobuf source tree). Any tag whose field number falls outside this
    // set terminates the FDP boundary scan.
    //   1  name
    //   2  package
    //   3  dependency
    //   4  message_type
    //   5  enum_type
    //   6  service
    //   7  extension
    //   8  options
    //   9  source_code_info
    //  10  public_dependency
    //  11  weak_dependency
    //  12  syntax
    //  13  edition  (proto Editions; present in newer descriptor.proto revisions)
    private static readonly HashSet<int> FdpFieldNumbers = new() { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };

    /// <summary>
    /// Read <paramref name="binaryPath"/> and return every embedded
    /// FileDescriptorProto found in it, in scan order (first byte of the parsed
    /// message ascending). Duplicates by name are NOT removed here — that's the
    /// orchestrator's job.
    /// </summary>
    /// <exception cref="FileNotFoundException">Path does not exist (input failure).</exception>
    /// <exception cref="UnauthorizedAccessException">Path can't be opened (input failure).</exception>
    public static IReadOnlyList<FileDescriptorProto> Scan(string binaryPath)
    {
        if (!File.Exists(binaryPath))
        {
            throw new FileNotFoundException(
                $"DescriptorScanner: input binary not found: '{binaryPath}'.", binaryPath);
        }

        var fileInfo = new FileInfo(binaryPath);
        if (fileInfo.Length == 0)
        {
            return Array.Empty<FileDescriptorProto>();
        }
        if (fileInfo.Length > int.MaxValue)
        {
            // CS2 DLLs are well under 2 GiB; if this ever trips it means we got
            // handed something we weren't expecting. Fail loud rather than silently
            // truncating.
            throw new NotSupportedException(
                $"DescriptorScanner: input '{binaryPath}' is {fileInfo.Length} bytes > 2 GiB; " +
                "scan over > int.MaxValue not implemented.");
        }

        var data = File.ReadAllBytes(binaryPath);
        return Scan(data);
    }

    /// <summary>
    /// Scan a byte buffer for embedded FileDescriptorProtos. See <see cref="Scan(string)"/>.
    /// </summary>
    public static IReadOnlyList<FileDescriptorProto> Scan(ReadOnlySpan<byte> data)
    {
        var results = new List<FileDescriptorProto>();
        var consumedThrough = 0;   // exclusive upper bound of bytes already inside an accepted FDP

        for (var i = 0; i < data.Length - 1; i++)
        {
            if (i < consumedThrough)
                continue;
            if (data[i] != NameFieldTag)
                continue;
            if (!LooksLikeAnchor(data, i))
                continue;

            if (TryExtractFdpAt(data, i, out var fdp, out var consumed))
            {
                results.Add(fdp);
                consumedThrough = i + consumed;
            }
            // else: anchor was noise, keep scanning.
        }

        return results;
    }

    /// <summary>
    /// At <paramref name="offset"/>, do we see a plausible FDP name field —
    /// tag 0x0a, varint length, ASCII filename ending in `.proto`?
    /// </summary>
    private static bool LooksLikeAnchor(ReadOnlySpan<byte> data, int offset)
    {
        // data[offset] is 0x0a by construction.
        if (!TryReadVarint(data, offset + 1, out var nameLen, out var afterLen))
            return false;
        if (nameLen <= 0 || nameLen > MaxNameLength)
            return false;
        if (afterLen + (long)nameLen > data.Length)
            return false;
        return LooksLikeProtoName(data.Slice(afterLen, (int)nameLen));
    }

    /// <summary>
    /// Walk wire fields from <paramref name="offset"/> forward to find the FDP
    /// boundary, then parse and verify by round-trip.
    /// </summary>
    private static bool TryExtractFdpAt(ReadOnlySpan<byte> data, int offset, out FileDescriptorProto fdp, out int consumed)
    {
        fdp = null!;
        consumed = 0;

        var available = data.Length - offset;
        if (available <= 0)
            return false;
        var window = Math.Min(available, MaxFdpBytes);

        // Walk fields forward; record the position after each successfully-parsed
        // field whose tag belongs to FileDescriptorProto's known field set.
        var boundary = WalkFdpBoundary(data.Slice(offset, window));
        if (boundary <= 0)
            return false;

        // Round-trip: parse the slice [offset .. offset+boundary], serialize back,
        // require byte-equality. This guards against (a) truncated parses (we
        // stopped early at a malformed byte that LOOKED like a future-FDP tag),
        // and (b) over-reads (we accepted bytes that the official parser
        // wouldn't have).
        var slice = data.Slice(offset, boundary).ToArray();
        FileDescriptorProto candidate;
        try
        {
            candidate = FileDescriptorProto.Parser.ParseFrom(slice);
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (candidate.CalculateSize() != boundary)
            return false;
        var roundTripped = candidate.ToByteArray();
        if (roundTripped.Length != boundary)
            return false;
        if (!slice.AsSpan().SequenceEqual(roundTripped))
            return false;

        // Acceptance: name must end in `.proto`.
        if (string.IsNullOrEmpty(candidate.Name))
            return false;
        if (!candidate.Name.EndsWith(".proto", StringComparison.Ordinal))
            return false;

        fdp = candidate;
        consumed = boundary;
        return true;
    }

    /// <summary>
    /// Walk proto wire fields starting at offset 0 of <paramref name="window"/>;
    /// return the number of bytes consumed by successfully-parsed fields whose
    /// field numbers all belong to <see cref="FdpFieldNumbers"/>. Stops at the
    /// first malformed byte or first unknown field number.
    /// </summary>
    private static int WalkFdpBoundary(ReadOnlySpan<byte> window)
    {
        var pos = 0;
        while (pos < window.Length)
        {
            // Read tag varint.
            if (!TryReadVarint(window, pos, out var tagRaw, out var afterTag))
                break;
            if (tagRaw <= 0 || tagRaw > uint.MaxValue)
                break;
            var tag = (uint)tagRaw;
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if (fieldNumber <= 0)
                break;
            if (!FdpFieldNumbers.Contains(fieldNumber))
                break;

            int payloadEnd;
            switch (wireType)
            {
                case 0:   // varint
                    if (!TryReadVarint(window, afterTag, out _, out var afterVarint))
                        return pos;
                    payloadEnd = afterVarint;
                    break;
                case 1:   // 64-bit fixed
                    if (afterTag + 8 > window.Length)
                        return pos;
                    payloadEnd = afterTag + 8;
                    break;
                case 2:   // length-delimited
                    if (!TryReadVarint(window, afterTag, out var len, out var afterLen))
                        return pos;
                    if (len < 0 || len > window.Length)
                        return pos;
                    if (afterLen + (long)len > window.Length)
                        return pos;
                    payloadEnd = afterLen + (int)len;
                    break;
                case 5:   // 32-bit fixed
                    if (afterTag + 4 > window.Length)
                        return pos;
                    payloadEnd = afterTag + 4;
                    break;
                case 3:   // start-group (deprecated, FDP doesn't use)
                case 4:   // end-group   (deprecated, FDP doesn't use)
                default:
                    return pos;
            }
            pos = payloadEnd;
        }
        return pos;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, int offset, out long value, out int nextOffset)
    {
        value = 0;
        nextOffset = offset;
        var shift = 0;
        while (offset < data.Length)
        {
            var b = data[offset];
            value |= (long)(b & 0x7f) << shift;
            offset++;
            if ((b & 0x80) == 0)
            {
                nextOffset = offset;
                return true;
            }
            shift += 7;
            if (shift > 63)
                return false;
        }
        return false;
    }

    private static bool LooksLikeProtoName(ReadOnlySpan<byte> name)
    {
        var suffix = ".proto"u8;
        if (name.Length < suffix.Length)
            return false;
        if (!name.Slice(name.Length - suffix.Length).SequenceEqual(suffix))
            return false;

        // ASCII printable, restricted to characters that appear in real proto
        // file paths. Reject control bytes, high-bit bytes, spaces, quotes.
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var ok = c is (>= (byte)'a' and <= (byte)'z')
                       or (>= (byte)'A' and <= (byte)'Z')
                       or (>= (byte)'0' and <= (byte)'9')
                       or (byte)'/' or (byte)'_' or (byte)'-' or (byte)'.' or (byte)'+';
            if (!ok)
                return false;
        }
        return true;
    }
}
