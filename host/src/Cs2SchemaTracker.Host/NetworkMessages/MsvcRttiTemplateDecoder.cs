// Shared MSVC RTTI template-constant decoder.
//
// The single, validated MSVC mangle decoder used by every offline RTTI id->type scan:
// - CNetMessagePB<id, type, group, reliable, flag> (network_messages.json)
//   - CDemoMessagePB<id, type>                          (demo_messages.json)
// - CUserMessagePB<id, type, bool> (cross-validator, not emitted)
//
// It was lifted VERBATIM out of NetworkMessageRttiScanner (the faithful C# port of the validated
// Python prototype) so the three scans cannot drift in how they decode `$0...` integers, the
// `V<Class>@@` / `V?$<Wrapper>@V<Inner>@@@@` type forms, the negative-magnitude sentinels, or the
// printable-run bounds. ScanMarker yields the RAW decoded constants (id may be negative, type
// un-normalised); each caller applies its own filtering.
//
// Invariants:
// Determinism: ScanMarker is a pure function of (data, marker); it walks the bytes
//          front-to-back and yields in positional order. Callers dedupe/sort.
// Fail-loud: malformed tails decode to null and are skipped HERE; a structurally empty
//          result is a caller-level fail-loud condition (a real CS2 binary set always carries
//          the relevant descriptors), not silently swallowed downstream.

using System.Text;

namespace Cs2SchemaTracker.Host.NetworkMessages;

/// <summary>
/// The shared MSVC RTTI template-constant decoder (CNetMessagePB / CDemoMessagePB /
/// CUserMessagePB). <see cref="ScanMarker"/> finds every descriptor for a given marker and
/// yields the raw decoded constants; callers normalise/filter/dedupe.
/// </summary>
internal static class MsvcRttiTemplateDecoder
{
    // Cap the printable run we read after a marker. Real descriptors are well under this; the
    // bound just stops a non-terminated run from over-reading (mirrors the Python {4,200}).
    private const int MaxTailLength = 200;
    private const int MinTailLength = 4;

    /// <summary>
    /// Decoded constants of one template instantiation. <see cref="ProtoMessageType"/> is the RAW
    /// C++ binding class (pre-normalisation). Group/Reliable/Flag are decoded for fidelity + tests
    /// (CNetMessagePB carries them; CDemoMessagePB / CUserMessagePB leave the surplus slots null
    /// or fold the trailing template args into them harmlessly — callers ignore them).
    /// </summary>
    internal readonly record struct Decoded(int Id, string ProtoMessageType, int? Group, int? Reliable, int? Flag);

    /// <summary>
    /// Yield every decodable template instantiation for <paramref name="marker"/> (e.g.
    /// <c>?$CNetMessagePB@</c>) found in <paramref name="data"/>, in positional order. Malformed
    /// tails are skipped. Results are RAW (no id sign filter, no type normalisation/acceptance) —
    /// the caller applies those.
    /// </summary>
    public static IEnumerable<Decoded> ScanMarker(byte[] data, string marker)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(marker);

        byte[] markerBytes = Encoding.ASCII.GetBytes(marker);
        var span = new ReadOnlySpan<byte>(data);

        var results = new List<Decoded>();
        int pos = 0;
        while (pos < data.Length)
        {
            int rel = span.Slice(pos).IndexOf(markerBytes);
            if (rel < 0)
                break;
            int start = pos + rel;
            int runStart = start + markerBytes.Length;

            // Read the printable (0x20..0x7e) run after the marker, bounded by MaxTailLength.
            int j = runStart;
            while (j < data.Length && data[j] >= 0x20 && data[j] <= 0x7e && (j - runStart) < MaxTailLength)
            {
                j++;
            }
            int runLen = j - runStart;

            // Distinct descriptors never nest, so stepping one byte past the marker start finds
            // every occurrence; the caller's (id,type) union dedupes any repeat.
            pos = start + 1;

            if (runLen < MinTailLength)
                continue;

            // Each byte -> one char (latin1), matching the Python `.decode('latin1')`.
            string tail = Latin1(data, runStart, runLen);
            if (Decode(tail) is { } d)
            {
                results.Add(d);
            }
        }
        return results;
    }

    // ------------------------------------------------------------------------------------------
    // MSVC template-constant decode. Faithful port of the validated Python prototype. Any
    // structural surprise throws DecodeException, which Decode() turns into a discarded null —
    // mirroring the Python parse() returning None on ValueError/IndexError/AssertionError.
    // ------------------------------------------------------------------------------------------

    private sealed class DecodeException : Exception { }

    /// <summary>
    /// Decode one mangled tail (the substring right after the <c>?$...@</c> marker) into its
    /// template constants, or null if it does not parse. RAW type name (pre-normalise).
    /// </summary>
    internal static Decoded? Decode(string tail)
    {
        ArgumentNullException.ThrowIfNull(tail);
        if (!tail.StartsWith("$0", StringComparison.Ordinal))
            return null;
        try
        {
            var (id, i) = DecodeInt(tail, 2);
            if (At(tail, i) != 'V')
                return null;
            var (type, afterType) = ReadType(tail, i);
            i = afterType;

            int? group = null, reliable = null, flag = null;
            if (Has0(tail, i))
            {
                try
                { var (g, ni) = DecodeInt(tail, i + 2); group = g; i = ni; }
                catch (DecodeException) { /* optional — leave unset, do not advance */ }
            }
            if (group is not null && Has0(tail, i))
            {
                try
                { var (r, ni) = DecodeInt(tail, i + 2); reliable = r; i = ni; }
                catch (DecodeException) { /* optional */ }
            }
            if (reliable is not null && Has0(tail, i))
            {
                try
                { var (f, ni) = DecodeInt(tail, i + 2); flag = f; i = ni; }
                catch (DecodeException) { /* optional */ }
            }

            return new Decoded(id, type, group, reliable, flag);
        }
        catch (DecodeException)
        {
            return null;
        }
    }

    // Decode an MSVC mangled magnitude starting at index i:
    //   short nonneg : single digit '0'..'9'        -> digit + 1   (encodes 1..10)
    //   long  nonneg : '[A-P]+@'  (A=0..P=15, hex)  -> base-16     (encodes 0 and 11+)
    private static (int Value, int Next) DecodeMag(string s, int i)
    {
        char c = At(s, i);
        if (c >= 'A' && c <= 'P')
        {
            int v = 0;
            while (At(s, i) != '@')
            {
                v = v * 16 + (At(s, i) - 'A');
                i++;
            }
            return (v, i + 1);
        }
        if (c >= '0' && c <= '9')
        {
            return ((c - '0') + 1, i + 1);
        }
        throw new DecodeException();
    }

    // Decode a signed MSVC integer: s[i..] begins right after the leading '$0'. A leading '?'
    // marks a negative magnitude (e.g. the -1 connectionless / negative-group sentinels).
    private static (int Value, int Next) DecodeInt(string s, int i)
    {
        if (At(s, i) == '?')
        {
            var (v, j) = DecodeMag(s, i + 1);
            return (-v, j);
        }
        return DecodeMag(s, i);
    }

    // Read the message (proto) type from the type arg:
    //   simple  : V<Class>@@
    //   wrapped : V?$<Wrapper>@V<Inner>@@@@   (proto name == the innermost class)
    private static (string Name, int Next) ReadType(string s, int i)
    {
        if (At(s, i) != 'V')
            throw new DecodeException();
        i++;
        if (At(s, i) == '?' && At(s, i + 1) == '$')
        {
            int j = Find(s, "@V", i) + 1;   // jump to the inner 'V'
            if (At(s, j) != 'V')
                throw new DecodeException();
            j++;
            int end = Find(s, "@@", j);
            string inner = s.Substring(j, end - j);
            int next = Find(s, "@@@@", end) + 4;
            return (inner, next);
        }
        int simpleEnd = Find(s, "@@", i);
        return (s.Substring(i, simpleEnd - i), simpleEnd + 2);
    }

    // ------------------------------------------------------------------------------------------
    // Small helpers. Out-of-range index access and missing-substring searches both surface as a
    // DecodeException so Decode() can discard a malformed tail (Python ValueError/IndexError).
    // ------------------------------------------------------------------------------------------

    private static char At(string s, int i)
    {
        if (i < 0 || i >= s.Length)
            throw new DecodeException();
        return s[i];
    }

    private static int Find(string s, string sub, int start)
    {
        int idx = s.IndexOf(sub, start, StringComparison.Ordinal);
        if (idx < 0)
            throw new DecodeException();
        return idx;
    }

    // Python's `tail[i:].startswith('$0')`: needs two readable chars at i, i+1.
    private static bool Has0(string s, int i)
        => i >= 0 && i + 1 < s.Length && s[i] == '$' && s[i + 1] == '0';

    private static string Latin1(byte[] data, int start, int length)
    {
        var chars = new char[length];
        for (int k = 0; k < length; k++)
        {
            chars[k] = (char)data[start + k];
        }
        return new string(chars);
    }

    /// <summary>A real proto message class: starts with 'C', then word chars only (^C[A-Za-z0-9_]+$).</summary>
    internal static bool IsProtoClassName(string type)
    {
        if (type.Length < 2 || type[0] != 'C')
            return false;
        foreach (char c in type)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';
            if (!ok)
                return false;
        }
        return true;
    }
}
