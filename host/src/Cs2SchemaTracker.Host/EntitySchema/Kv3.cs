// Minimal KV3 (Valve KeyValues v3, text form) parser for the MGetKV3ClassDefaults metadata payload.
//
//
// === Why hand-rolled ===
// The KV3 default-value payload Valve emits for MGetKV3ClassDefaults is a small, regular subset of
// the KV3 text grammar: a single top-level map of name = value pairs, where a value is a number,
// bool, quoted string, array, or nested map. A focused parser keeps the dependency surface clean
// (no ValveResourceFormat / ValveKeyValue runtime dep) and keeps the structural mapping to
// google.protobuf.Value in our own deterministic code.
//
// === Output shape ===
// Parses into a structural google.protobuf.Value (entity_schema.proto's SchemaMetadata.value_parsed
// field):
//   - map      -> Value.StructValue   (object; keys preserved verbatim)
//   - array    -> Value.ListValue
//   - number   -> Value.NumberValue   (KV3 ints + floats are both proto double)
//   - bool     -> Value.BoolValue
//   - string   -> Value.StringValue
//   - null     -> Value.NullValue
// A leading optional KV3 header (`<!-- kv3 ... -->`) is tolerated and skipped.
//
// === Fail behavior (degrade, not fail-loud) ===
// A class whose KV3 string fails to parse keeps the RAW string only with a parse-failure note — the
// overall extract MUST still succeed. So this parser THROWS Kv3ParseException on malformed input and
// the CALLER (EntitySchemaEmitter) catches it, leaves value_parsed unset, and records the note. This
// is the one documented place the host catches rather than propagates — it is the parity behavior
// and does not conflict with the fail-loud rule (which governs INPUT-binary failures, not optional
// metadata the spec says to degrade gracefully).
//
// Determinism: map keys are emitted in source order into the Struct; the CanonicalJson layer the
// emitter runs sorts object keys for byte-stable output.

using Google.Protobuf.WellKnownTypes;

namespace Cs2SchemaTracker.Host.EntitySchema;

/// <summary>Thrown on malformed KV3; caught by the caller to record a parse-failure note.</summary>
internal sealed class Kv3ParseException : Exception
{
    public Kv3ParseException(string message) : base(message) { }
}

/// <summary>
/// Minimal recursive-descent KV3 text parser producing a structural
/// <see cref="Value"/>. Throws <see cref="Kv3ParseException"/> on malformed input.
/// </summary>
internal static class Kv3
{
    /// <summary>
    /// Parse a KV3 default-value payload into a structural <see cref="Value"/>. The CS2
    /// MGetKV3ClassDefaults payload is a top-level map, but the parser accepts any single
    /// KV3 value at the top level. Throws <see cref="Kv3ParseException"/> on any malformed
    /// input or trailing junk.
    /// </summary>
    public static Value Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var p = new Parser(text);
        p.SkipHeaderAndTrivia();
        Value v = p.ParseValue();
        p.SkipTrivia();
        if (!p.AtEnd)
        {
            throw new Kv3ParseException(
                $"KV3 trailing junk after top-level value at offset {p.Position}.");
        }
        return v;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _pos;

        public Parser(string s) => _s = s;

        public int Position => _pos;
        public bool AtEnd => _pos >= _s.Length;

        public Value ParseValue()
        {
            SkipTrivia();
            if (AtEnd)
            {
                throw new Kv3ParseException("KV3 unexpected end of input where a value was expected.");
            }

            char c = _s[_pos];
            return c switch
            {
                '{' => ParseMap(),
                '[' => ParseArray(),
                '"' => Value.ForString(ParseQuoted()),
                _ => ParseTypedOrBareValue(),
            };
        }

        // KV3 text carries TYPED values as `type:value`, e.g. a resource reference
        // `resource:"particles/impact_fx/impact_metal.vpcf"` or `subclass:"…"`. The type
        // annotation is a bare identifier run immediately followed by ':' and then a value
        // (string / map / array / scalar). We UNWRAP to the inner value (the type tag is not
        // part of the structural payload) so callers see the underlying string/struct/list.
        //
        // Backward-compatible: this only changes behavior for input of the exact shape
        // `<bareident>:<value>` — which the PRIOR parser REJECTED (it read the whole
        // `type:value` run as one bare token via IsBareValueChar, which includes ':', and then
        // failed on the following quote/brace). A plain bare scalar (number / bool / null /
        // unquoted token with no ':<value>' suffix) still parses exactly as before, because we
        // rewind and fall through to ParseBareValue when the typed shape is not matched.
        private Value ParseTypedOrBareValue()
        {
            int save = _pos;
            int start = _pos;
            while (_pos < _s.Length && IsBareValueChar(_s[_pos]) && _s[_pos] != ':')
            {
                _pos++;
            }
            if (_pos > start && _pos < _s.Length && _s[_pos] == ':')
            {
                // Only `<ident>:` whose inner value is a real KV3 value form (quoted string /
                // map / array — `resource:"…"`, `subclass:{…}`, `:[…]`) is a TYPED value. A bare
                // token that merely CONTAINS a colon (e.g. `12:30`, `c:\path`, `foo:bar`) is NOT a
                // type tag — keep it whole as a bare scalar so its prefix is not silently dropped.
                int peek = _pos + 1;
                while (peek < _s.Length && char.IsWhiteSpace(_s[peek]))
                {
                    peek++;
                }
                if (peek < _s.Length && (_s[peek] == '"' || _s[peek] == '[' || _s[peek] == '{'))
                {
                    _pos++; // consume ':'; ParseValue() skips trivia + unwraps the inner value.
                    return ParseValue();
                }
            }
            // Not a typed value — rewind and parse as a normal bare scalar.
            _pos = save;
            return ParseBareValue();
        }

        private Value ParseMap()
        {
            Expect('{');
            var s = new Struct();
            while (true)
            {
                SkipTrivia();
                if (AtEnd)
                {
                    throw new Kv3ParseException("KV3 unterminated map: end of input before '}'.");
                }
                if (_s[_pos] == '}')
                {
                    _pos++;
                    return Value.ForStruct(s);
                }

                string key = ParseKey();
                SkipTrivia();
                Expect('=');
                Value v = ParseValue();
                // Last value wins on a duplicate key — KV3 maps are not expected to repeat
                // keys, and a Struct cannot hold both; this keeps parsing deterministic.
                s.Fields[key] = v;
                SkipOptionalSeparator();
            }
        }

        private Value ParseArray()
        {
            Expect('[');
            var list = new ListValue();
            while (true)
            {
                SkipTrivia();
                if (AtEnd)
                {
                    throw new Kv3ParseException("KV3 unterminated array: end of input before ']'.");
                }
                if (_s[_pos] == ']')
                {
                    _pos++;
                    // Wrap the accumulated ListValue in a Value. Value.ForList takes params
                    // Value[]; pass the collected elements.
                    return new Value { ListValue = list };
                }
                list.Values.Add(ParseValue());
                SkipOptionalSeparator();
            }
        }

        // A map key is either a quoted string or a bare identifier run.
        private string ParseKey()
        {
            SkipTrivia();
            if (AtEnd)
            {
                throw new Kv3ParseException("KV3 expected a map key, got end of input.");
            }
            if (_s[_pos] == '"')
            {
                return ParseQuoted();
            }
            int start = _pos;
            while (_pos < _s.Length && IsBareKeyChar(_s[_pos]))
            {
                _pos++;
            }
            if (_pos == start)
            {
                throw new Kv3ParseException($"KV3 expected a map key at offset {_pos}, got '{_s[_pos]}'.");
            }
            return _s.Substring(start, _pos - start);
        }

        // A bare (unquoted) scalar value: number, true/false, null, or an unquoted token
        // (e.g. a resource subtype / enum literal Valve sometimes emits). Numbers and bools
        // map to typed Values; anything else maps to a string Value (lossless round-trip).
        private Value ParseBareValue()
        {
            int start = _pos;
            while (_pos < _s.Length && IsBareValueChar(_s[_pos]))
            {
                _pos++;
            }
            if (_pos == start)
            {
                throw new Kv3ParseException(
                    $"KV3 unexpected character '{_s[_pos]}' at offset {_pos} where a value was expected.");
            }
            string tok = _s.Substring(start, _pos - start);

            if (tok == "true")
                return Value.ForBool(true);
            if (tok == "false")
                return Value.ForBool(false);
            if (tok == "null")
                return Value.ForNull();

            if (TryParseNumber(tok, out double number))
            {
                return Value.ForNumber(number);
            }
            // Unquoted non-numeric token: keep verbatim as a string (no guessing).
            return Value.ForString(tok);
        }

        private static bool TryParseNumber(string tok, out double number)
        {
            // KV3 numbers are decimal ints or floats; also tolerate a leading sign and a
            // trailing 'f' suffix Valve occasionally emits on floats.
            string t = tok.EndsWith('f') || tok.EndsWith('F') ? tok[..^1] : tok;
            return double.TryParse(
                t,
                System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out number);
        }

        private string ParseQuoted()
        {
            Expect('"');
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new Kv3ParseException("KV3 unterminated quoted string.");
                }
                char c = _s[_pos];
                if (c == '"')
                {
                    _pos++;
                    return sb.ToString();
                }
                if (c == '\\' && _pos + 1 < _s.Length)
                {
                    char n = _s[_pos + 1];
                    sb.Append(n switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        _ => n,
                    });
                    _pos += 2;
                    continue;
                }
                sb.Append(c);
                _pos++;
            }
        }

        private void Expect(char c)
        {
            SkipTrivia();
            if (AtEnd || _s[_pos] != c)
            {
                string got = AtEnd ? "end of input" : $"'{_s[_pos]}'";
                throw new Kv3ParseException($"KV3 expected '{c}' at offset {_pos}, got {got}.");
            }
            _pos++;
        }

        // KV3 separates entries with whitespace and/or commas; both are optional/equivalent.
        private void SkipOptionalSeparator()
        {
            SkipTrivia();
            if (!AtEnd && _s[_pos] == ',')
            {
                _pos++;
            }
        }

        public void SkipHeaderAndTrivia()
        {
            SkipTrivia();
            // Optional KV3 text header: <!-- kv3 ... --> — skip it wholesale if present.
            if (_pos + 4 <= _s.Length && _s.AsSpan(_pos, 4).SequenceEqual("<!--"))
            {
                int end = _s.IndexOf("-->", _pos, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new Kv3ParseException("KV3 unterminated '<!-- ... -->' header.");
                }
                _pos = end + 3;
                SkipTrivia();
            }
        }

        public void SkipTrivia()
        {
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (char.IsWhiteSpace(c))
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '/')
                {
                    while (_pos < _s.Length && _s[_pos] != '\n')
                    {
                        _pos++;
                    }
                }
                else
                {
                    return;
                }
            }
        }

        private static bool IsBareKeyChar(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c == '.';

        private static bool IsBareValueChar(char c) =>
            !char.IsWhiteSpace(c) && c != ',' && c != '}' && c != ']' && c != '=' && c != '"';
    }
}
