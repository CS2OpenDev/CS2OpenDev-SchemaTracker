// Shared Itanium C++ ABI RTTI template-constant decoder (linux-x86_64).
//
// The Itanium-ABI counterpart of MsvcRttiTemplateDecoder. Same job — decode the compile-time
// template constants of the CS2 message-binding templates directly out of a shipped binary's
// read-only data — but for the GCC/Clang Itanium type_info name mangling that linux-x86_64
// `.so` binaries carry, instead of the MSVC `?$Name@...` form:
// - CNetMessagePB<id, Type, SignonGroup_t, NetChannelBufType_t, bool> (network_messages)
//   - CDemoMessagePB<EDemoCommands id, Type>                              (demo_messages)
// - CUserMessagePB<id, Type, NetChannelBufType_t> (cross-validator)
//
// the registered ids are IDENTICAL across platforms — only the demangle differs — so the
// (id -> proto type) tables this decoder yields are byte-for-byte the same set the MSVC decoder
// yields for the same build. Validated: over build 23773332's Linux `.so` set this reproduces the
// committed windows-x86_64 network_messages.json exactly (194 msgs / 12 channels, zero linux-only /
// windows-only) and the 19-entry demo table, with zero CUserMessagePB cross-validation divergence.
//
// Itanium type_info-name grammar this decoder needs (a small, fixed subset — anything unexpected
// throws and the tail is discarded, mirroring the MSVC side):
//   - length-prefixed name:      <len><chars>                 e.g. 13CNetMessagePB, 9CDemoStop
//   - template instantiation:    <name>I<arg>...E             e.g. 17CBaseCmdKeyValuesI...E
//   - int literal:               Li<value>E  / Lin<value>E    (the CNet/CUser id, negatives via 'n')
//   - bool literal:              Lb0E / Lb1E
//   - enum literal:              L<len><EnumName><value>E      e.g. L13EDemoCommands13E (the demo id),
//                                                              L13SignonGroup_t9E, L19NetChannelBufType_t1E
// The message Type is the SECOND template arg. When it is itself a template (the CBaseCmdKeyValues
// wrapper), the proto name is the INNERMOST class arg — matching the MSVC decoder's unwrap so the
// two produce identical type spellings. The surplus trailing literals (group/reliable/flag) are
// decoded for fidelity; callers ignore them.
//
// Invariants:
// Determinism: ScanMarker is a pure function of (data, marker); it walks the bytes
//          front-to-back and yields in positional order. Callers dedupe/sort.
// Fail-loud: a malformed tail decodes to null and is skipped HERE; a structurally empty
//          result is a caller-level fail-loud condition (a real CS2 binary set always carries the
//          relevant descriptors), not silently swallowed downstream.

using System.Text;

namespace Cs2SchemaTracker.Host.NetworkMessages;

/// <summary>
/// The shared Itanium-ABI RTTI template-constant decoder (CNetMessagePB / CDemoMessagePB /
/// CUserMessagePB on linux-x86_64). <see cref="ScanMarker"/> finds every descriptor for a given
/// length-prefixed marker and yields the raw decoded constants; callers normalise/filter/dedupe.
/// The decoded record shape matches <see cref="MsvcRttiTemplateDecoder.Decoded"/> so the net/demo/
/// user scans consume both ABIs through one code path and cannot drift.
/// </summary>
internal static class ItaniumRttiTemplateDecoder
{
    // Cap the printable run we read after a marker. Real descriptors are well under this; the bound
    // just stops a non-terminated run (Itanium type_info names are not NUL-guaranteed adjacent) from
    // over-reading. The decoder stops at the structural template close regardless, so trailing bytes
    // from the next symbol are harmless.
    private const int MaxTailLength = 200;
    private const int MinTailLength = 3;

    /// <summary>
    /// Decoded constants of one template instantiation. <see cref="ProtoMessageType"/> is the RAW
    /// C++ binding class (pre-normalisation). Group/Reliable/Flag are the surplus trailing literals
    /// (present on CNetMessagePB; fewer on CDemo/CUser) — callers ignore them.
    /// </summary>
    internal readonly record struct Decoded(int Id, string ProtoMessageType, int? Group, int? Reliable, int? Flag);

    /// <summary>
    /// Yield every decodable template instantiation for <paramref name="marker"/> (the
    /// length-prefixed Itanium name + <c>I</c>, e.g. <c>13CNetMessagePBI</c>) found in
    /// <paramref name="data"/>, in positional order. Malformed tails are skipped. Results are RAW
    /// (no id sign filter, no type normalisation/acceptance) — the caller applies those.
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

            // The same mangled name recurs (typeinfo, vtable, pointer forms) and any decode of it
            // yields the identical (id, type); stepping one byte past the marker start finds every
            // occurrence and the caller's (id,type) union dedupes the repeats.
            pos = start + 1;

            if (runLen < MinTailLength)
                continue;

            string tail = Latin1(data, runStart, runLen);
            if (Decode(tail) is { } d)
            {
                results.Add(d);
            }
        }
        return results;
    }

    // ------------------------------------------------------------------------------------------
    // Itanium template-constant decode. Any structural surprise throws DecodeException, which
    // Decode() turns into a discarded null (mirroring the MSVC side).
    // ------------------------------------------------------------------------------------------

    private sealed class DecodeException : Exception { }

    /// <summary>
    /// Decode one Itanium template-arg-list tail (the substring right after the
    /// <c>&lt;len&gt;&lt;Name&gt;I</c> marker) into its template constants, or null if it does not
    /// parse. RAW type name (pre-normalise). The first arg is the id literal (an int literal for
    /// CNet/CUser, an EDemoCommands enum literal for CDemo — both carry the id as their value); the
    /// second arg is the message Type; any further trailing literals are group/reliable/flag.
    /// </summary>
    internal static Decoded? Decode(string tail)
    {
        ArgumentNullException.ThrowIfNull(tail);
        if (tail.Length == 0 || tail[0] != 'L')
            return null;   // must open with the id literal.
        try
        {
            var (id, i) = ParseLiteral(tail, 0);          // arg 1: id (int or enum literal).
            var (type, afterType) = ReadType(tail, i);    // arg 2: the message Type.
            i = afterType;

            // Surplus trailing literals: group, reliable, flag (as many as present). Stop at the
            // template-closing 'E' or end of run.
            int? group = null, reliable = null, flag = null;
            if (i < tail.Length && tail[i] == 'L')
            { var (g, ni) = ParseLiteral(tail, i); group = g; i = ni; }
            if (group is not null && i < tail.Length && tail[i] == 'L')
            { var (r, ni) = ParseLiteral(tail, i); reliable = r; i = ni; }
            if (reliable is not null && i < tail.Length && tail[i] == 'L')
            { var (f, ni) = ParseLiteral(tail, i); flag = f; }

            return new Decoded(id, type, group, reliable, flag);
        }
        catch (DecodeException)
        {
            return null;
        }
    }

    // Parse an Itanium literal `L<type><value>E`:
    //   int  : Li<digits>E   (negative: Lin<digits>E)
    //   bool : Lb<0|1>E
    //   enum : L<len><EnumName><digits>E   (negative value: ...n<digits>E)
    // Returns the numeric VALUE (the id for arg1, the enum ordinal for group/reliable/flag).
    private static (int Value, int Next) ParseLiteral(string s, int i)
    {
        if (At(s, i) != 'L')
            throw new DecodeException();
        i++;
        char c = At(s, i);
        if (c is >= 'a' and <= 'z')
        {
            i++;                    // builtin type char (i=int, b=bool, j=unsigned, ...).
        }
        else if (c is >= '0' and <= '9')
        {
            (_, i) = ReadName(s, i); // enum type: a length-prefixed name we discard.
        }
        else
        {
            throw new DecodeException();
        }

        bool neg = false;
        if (At(s, i) == 'n')
        { neg = true; i++; }
        var (mag, afterMag) = ReadNumber(s, i);
        i = afterMag;
        if (At(s, i) != 'E')
            throw new DecodeException();
        return (neg ? -mag : mag, i + 1);
    }

    // Read a message (proto) Type arg:
    //   simple   : <len><Class>
    //   templated: <len><Wrapper>I<arg>...E   -> proto name == the innermost class arg (matches the
    //              MSVC decoder's unwrap of V?$Wrapper@VInner@@@@ to Inner).
    private static (string Name, int Next) ReadType(string s, int i)
    {
        var (name, afterName) = ReadName(s, i);
        int k = afterName;
        if (k < s.Length && s[k] == 'I')
        {
            k++;                            // enter the template arg list.
            string innermost = name;
            while (At(s, k) != 'E')
            {
                char c = s[k];
                if (c == 'L')
                {
                    (_, k) = ParseLiteral(s, k);          // a literal template arg — skip it.
                }
                else if (c is >= '0' and <= '9')
                {
                    (innermost, k) = ReadType(s, k);      // a type arg — its innermost class wins.
                }
                else
                {
                    throw new DecodeException();
                }
            }
            return (innermost, k + 1);      // consume the closing 'E'.
        }
        return (name, k);
    }

    // Read a length-prefixed name `<digits><that-many-chars>`.
    private static (string Name, int Next) ReadName(string s, int i)
    {
        var (len, afterLen) = ReadNumber(s, i);
        if (len <= 0 || afterLen + len > s.Length)
            throw new DecodeException();
        return (s.Substring(afterLen, len), afterLen + len);
    }

    // Read a run of decimal digits (at least one).
    private static (int Value, int Next) ReadNumber(string s, int i)
    {
        int j = i;
        while (j < s.Length && s[j] is >= '0' and <= '9')
            j++;
        if (j == i)
            throw new DecodeException();
        return (int.Parse(s.AsSpan(i, j - i), System.Globalization.CultureInfo.InvariantCulture), j);
    }

    private static char At(string s, int i)
    {
        if (i < 0 || i >= s.Length)
            throw new DecodeException();
        return s[i];
    }

    private static string Latin1(byte[] data, int start, int length)
    {
        var chars = new char[length];
        for (int k = 0; k < length; k++)
        {
            chars[k] = (char)data[start + k];
        }
        return new string(chars);
    }
}
