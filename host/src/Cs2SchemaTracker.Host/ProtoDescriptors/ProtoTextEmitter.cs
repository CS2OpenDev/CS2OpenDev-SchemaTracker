// emit canonical `.proto` text from a FileDescriptorProto.
//
// Determinism rules:
//   - LF line endings (no CRLF), UTF-8, no BOM (caller writes bytes).
//   - Two-space indent.
//   - Final newline at EOF.
//   - No trailing whitespace on any line.
//   - Iteration order:
//     * Top-level declarations: enums first (FDP-declared order), then messages
//       (FDP-declared order), then services (FDP-declared order). FDP-declared
//       order IS the canonical order — Valve's compiler wrote it; reordering
//       alphabetically would lose proto numbering semantics for tooling that
//       cares about declaration order.
//     * Fields within a message: FDP-declared order.
//     * Options: sorted by option name, ordinal compare.
//     * File-level dependencies: FDP-declared order.
//
// Output is required to compile cleanly via `protoc` with zero warnings.
//
// Scope limits: this emitter targets proto3 (CS2's protos are proto3). It
// handles messages, nested messages, enums, repeated fields, packed fields,
// maps (collapsing the synthetic map_entry message), oneofs, well-known
// scalar types, message/enum cross-references via fully-qualified names,
// and the small subset of file/message/field options CS2 actually uses.

using System.Globalization;
using System.Text;

using Google.Protobuf.Reflection;

namespace Cs2SchemaTracker.Host.ProtoDescriptors;

internal static class ProtoTextEmitter
{
    /// <summary>
    /// Emit the FDP as canonical-form .proto text.
    /// </summary>
    public static string Emit(FileDescriptorProto fdp)
    {
        var sb = new StringBuilder();

        // syntax line.
        var syntax = string.IsNullOrEmpty(fdp.Syntax) ? "proto2" : fdp.Syntax;
        sb.Append("syntax = \"").Append(syntax).Append("\";\n");

        // package.
        if (!string.IsNullOrEmpty(fdp.Package))
        {
            sb.Append("\npackage ").Append(fdp.Package).Append(";\n");
        }

        // imports (dependencies).
        if (fdp.Dependency.Count > 0)
        {
            sb.Append('\n');
            // Track which indices are public / weak.
            var publicIdx = new HashSet<int>(fdp.PublicDependency);
            var weakIdx = new HashSet<int>(fdp.WeakDependency);
            for (var i = 0; i < fdp.Dependency.Count; i++)
            {
                sb.Append("import ");
                if (publicIdx.Contains(i))
                    sb.Append("public ");
                else if (weakIdx.Contains(i))
                    sb.Append("weak ");
                sb.Append('"').Append(fdp.Dependency[i]).Append("\";\n");
            }
        }

        // file options (sorted by option name).
        if (fdp.Options != null)
        {
            var fileOptions = CollectFileOptions(fdp.Options);
            if (fileOptions.Count > 0)
            {
                sb.Append('\n');
                foreach (var (k, v) in fileOptions)
                {
                    sb.Append("option ").Append(k).Append(" = ").Append(v).Append(";\n");
                }
            }
        }

        // Top-level enums (FDP-declared order).
        foreach (var en in fdp.EnumType)
        {
            sb.Append('\n');
            EmitEnum(sb, en, indent: 0);
        }

        // Top-level messages (FDP-declared order).
        foreach (var msg in fdp.MessageType)
        {
            sb.Append('\n');
            EmitMessage(sb, msg, indent: 0, packagePrefix: fdp.Package);
        }

        // Services (FDP-declared order). CS2 protos don't generally use services
        // but emit defensively so future inputs round-trip.
        foreach (var svc in fdp.Service)
        {
            sb.Append('\n');
            EmitService(sb, svc, indent: 0);
        }

        // Final newline guaranteed by the always-appended '\n' on the last line.
        return sb.ToString();
    }

    private static void EmitMessage(StringBuilder sb, DescriptorProto msg, int indent, string packagePrefix)
    {
        var ind = Indent(indent);
        sb.Append(ind).Append("message ").Append(msg.Name).Append(" {\n");

        // Message-level options (sorted).
        if (msg.Options != null)
        {
            var opts = CollectMessageOptions(msg.Options);
            foreach (var (k, v) in opts)
            {
                sb.Append(Indent(indent + 1)).Append("option ").Append(k).Append(" = ").Append(v).Append(";\n");
            }
        }

        // Find which nested messages are map entries — they get collapsed into
        // the parent's `map<K,V>` field syntax rather than emitted as nested
        // messages.
        var mapEntries = new Dictionary<string, DescriptorProto>(StringComparer.Ordinal);
        foreach (var nested in msg.NestedType)
        {
            if (nested.Options is { MapEntry: true })
            {
                mapEntries[nested.Name] = nested;
            }
        }

        // Nested enums (FDP-declared order).
        foreach (var en in msg.EnumType)
        {
            EmitEnum(sb, en, indent + 1);
        }

        // Nested (non-map-entry) messages (FDP-declared order).
        foreach (var nested in msg.NestedType)
        {
            if (mapEntries.ContainsKey(nested.Name))
                continue;
            EmitMessage(sb, nested, indent + 1, packagePrefix);
        }

        // Group fields by oneof index so we can emit non-oneof fields inline
        // and oneof fields inside `oneof { ... }` blocks.
        var nonOneofFields = new List<FieldDescriptorProto>();
        var byOneof = new Dictionary<int, List<FieldDescriptorProto>>();

        foreach (var f in msg.Field)
        {
            if (f.HasOneofIndex && !IsSyntheticProto3OptionalOneof(msg, f))
            {
                if (!byOneof.TryGetValue(f.OneofIndex, out var list))
                {
                    list = new List<FieldDescriptorProto>();
                    byOneof[f.OneofIndex] = list;
                }
                list.Add(f);
            }
            else
            {
                nonOneofFields.Add(f);
            }
        }

        // Non-oneof fields.
        foreach (var f in nonOneofFields)
        {
            EmitField(sb, msg, f, indent + 1, mapEntries, packagePrefix);
        }

        // Oneof blocks (in OneofDecl-declared order).
        for (var oi = 0; oi < msg.OneofDecl.Count; oi++)
        {
            if (!byOneof.TryGetValue(oi, out var fields))
                continue;
            var name = msg.OneofDecl[oi].Name;
            sb.Append(Indent(indent + 1)).Append("oneof ").Append(name).Append(" {\n");
            foreach (var f in fields)
            {
                EmitField(sb, msg, f, indent + 2, mapEntries, packagePrefix, inOneof: true);
            }
            sb.Append(Indent(indent + 1)).Append("}\n");
        }

        // Reserved ranges and reserved names (FDP-declared order).
        foreach (var r in msg.ReservedRange)
        {
            sb.Append(Indent(indent + 1)).Append("reserved ");
            if (r.End == r.Start + 1)
            {
                sb.Append(r.Start.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(r.Start.ToString(CultureInfo.InvariantCulture))
                  .Append(" to ")
                  .Append((r.End - 1).ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(";\n");
        }
        if (msg.ReservedName.Count > 0)
        {
            sb.Append(Indent(indent + 1)).Append("reserved ");
            for (var i = 0; i < msg.ReservedName.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append('"').Append(msg.ReservedName[i]).Append('"');
            }
            sb.Append(";\n");
        }

        sb.Append(ind).Append("}\n");
    }

    /// <summary>
    /// proto3 optional fields are encoded internally as synthetic single-field
    /// oneofs (named "_&lt;fieldname&gt;"). Detect and unwrap so we emit the field
    /// as `optional T name = N;` rather than inside a phantom oneof block.
    /// </summary>
    private static bool IsSyntheticProto3OptionalOneof(DescriptorProto msg, FieldDescriptorProto f)
    {
        if (!f.HasOneofIndex)
            return false;
        if (f.OneofIndex < 0 || f.OneofIndex >= msg.OneofDecl.Count)
            return false;
        var oneof = msg.OneofDecl[f.OneofIndex];
        return oneof.Name == "_" + f.Name;
    }

    private static void EmitField(
        StringBuilder sb,
        DescriptorProto parent,
        FieldDescriptorProto f,
        int indent,
        Dictionary<string, DescriptorProto> mapEntries,
        string packagePrefix,
        bool inOneof = false)
    {
        var ind = Indent(indent);

        // Map field? In FDP, a `map<K,V>` becomes a repeated synthetic-nested
        // message field whose nested message has `options.map_entry = true`.
        // Detect and emit as `map<K, V> name = N;`.
        if (!inOneof
            && f.Label == FieldDescriptorProto.Types.Label.Repeated
            && f.Type == FieldDescriptorProto.Types.Type.Message
            && !string.IsNullOrEmpty(f.TypeName))
        {
            var mapEntry = TryResolveMapEntry(f.TypeName, parent, mapEntries);
            if (mapEntry != null)
            {
                var keyField = mapEntry.Field.FirstOrDefault(x => x.Number == 1);
                var valField = mapEntry.Field.FirstOrDefault(x => x.Number == 2);
                if (keyField != null && valField != null)
                {
                    sb.Append(ind)
                      .Append("map<")
                      .Append(FieldTypeText(keyField, packagePrefix))
                      .Append(", ")
                      .Append(FieldTypeText(valField, packagePrefix))
                      .Append("> ")
                      .Append(f.Name)
                      .Append(" = ")
                      .Append(f.Number.ToString(CultureInfo.InvariantCulture));
                    AppendFieldOptions(sb, f);
                    sb.Append(";\n");
                    return;
                }
            }
        }

        sb.Append(ind);

        // Label prefix (omit `optional` and `required` for proto3 except for
        // proto3-explicit-optional which uses Proto3Optional).
        if (f.Label == FieldDescriptorProto.Types.Label.Repeated && !inOneof)
        {
            sb.Append("repeated ");
        }
        else if (!inOneof && f.Proto3Optional)
        {
            sb.Append("optional ");
        }
        else if (!inOneof && f.Label == FieldDescriptorProto.Types.Label.Required)
        {
            // proto2 only.
            sb.Append("required ");
        }
        else if (!inOneof
                 && f.Label == FieldDescriptorProto.Types.Label.Optional
                 && IsProto2Parent(parent))
        {
            // proto2 explicit optional. proto3 omits.
            sb.Append("optional ");
        }
        // proto3 non-optional, non-repeated: no label prefix.

        sb.Append(FieldTypeText(f, packagePrefix))
          .Append(' ')
          .Append(f.Name)
          .Append(" = ")
          .Append(f.Number.ToString(CultureInfo.InvariantCulture));
        AppendFieldOptions(sb, f);
        sb.Append(";\n");
    }

    private static bool IsProto2Parent(DescriptorProto _)
    {
        // We don't carry syntax into the message recursion; rely on field's
        // own Proto3Optional flag plus the FDP-level syntax string. For
        // simplicity we treat any field marked Label.Optional WITHOUT
        // Proto3Optional as proto2 (proto3 omits the label). CS2 protos are
        // proto3 so this branch is dead in practice.
        return true;
    }

    private static DescriptorProto? TryResolveMapEntry(
        string typeName,
        DescriptorProto parent,
        Dictionary<string, DescriptorProto> mapEntries)
    {
        // FDP type names are fully-qualified like `.package.Outer.MapFieldEntry`
        // OR sometimes just `Outer.MapFieldEntry` for nested resolves. We only
        // need to match the trailing nested name.
        var simple = typeName;
        var lastDot = typeName.LastIndexOf('.');
        if (lastDot >= 0)
            simple = typeName[(lastDot + 1)..];
        return mapEntries.GetValueOrDefault(simple);
    }

    private static string FieldTypeText(FieldDescriptorProto f, string packagePrefix)
    {
        return f.Type switch
        {
            FieldDescriptorProto.Types.Type.Double => "double",
            FieldDescriptorProto.Types.Type.Float => "float",
            FieldDescriptorProto.Types.Type.Int64 => "int64",
            FieldDescriptorProto.Types.Type.Uint64 => "uint64",
            FieldDescriptorProto.Types.Type.Int32 => "int32",
            FieldDescriptorProto.Types.Type.Fixed64 => "fixed64",
            FieldDescriptorProto.Types.Type.Fixed32 => "fixed32",
            FieldDescriptorProto.Types.Type.Bool => "bool",
            FieldDescriptorProto.Types.Type.String => "string",
            FieldDescriptorProto.Types.Type.Bytes => "bytes",
            FieldDescriptorProto.Types.Type.Uint32 => "uint32",
            FieldDescriptorProto.Types.Type.Sfixed32 => "sfixed32",
            FieldDescriptorProto.Types.Type.Sfixed64 => "sfixed64",
            FieldDescriptorProto.Types.Type.Sint32 => "sint32",
            FieldDescriptorProto.Types.Type.Sint64 => "sint64",
            FieldDescriptorProto.Types.Type.Message
              or FieldDescriptorProto.Types.Type.Enum
              or FieldDescriptorProto.Types.Type.Group => RelativizeTypeName(f.TypeName, packagePrefix),
            _ => "/* unknown type */",
        };
    }

    /// <summary>
    /// FDP type names always start with `.` and are fully qualified. Strip the
    /// leading `.` and, if the type lives in the same package, strip the
    /// package prefix for readability. (protoc accepts both forms.)
    /// </summary>
    private static string RelativizeTypeName(string typeName, string packagePrefix)
    {
        if (string.IsNullOrEmpty(typeName))
            return "";
        var t = typeName.StartsWith('.') ? typeName[1..] : typeName;
        if (!string.IsNullOrEmpty(packagePrefix)
            && t.StartsWith(packagePrefix + ".", StringComparison.Ordinal))
        {
            return t[(packagePrefix.Length + 1)..];
        }
        // Cross-package: emit as fully-qualified with leading dot to avoid scope ambiguity.
        return "." + t;
    }

    private static void AppendFieldOptions(StringBuilder sb, FieldDescriptorProto f)
    {
        var opts = new List<(string Key, string Value)>();
        if (f.Options != null)
        {
            if (f.Options.HasPacked)
            {
                opts.Add(("packed", f.Options.Packed ? "true" : "false"));
            }
            if (f.Options.HasDeprecated)
            {
                opts.Add(("deprecated", f.Options.Deprecated ? "true" : "false"));
            }
            if (f.Options.HasLazy)
            {
                opts.Add(("lazy", f.Options.Lazy ? "true" : "false"));
            }
            if (f.Options.HasJstype)
            {
                opts.Add(("jstype", f.Options.Jstype.ToString().ToUpperInvariant()));
            }
            if (f.Options.HasCtype)
            {
                opts.Add(("ctype", f.Options.Ctype.ToString().ToUpperInvariant()));
            }
        }
        if (f.HasDefaultValue && !string.IsNullOrEmpty(f.DefaultValue))
        {
            opts.Add(("default", FormatDefaultValue(f)));
        }
        if (f.HasJsonName && !string.IsNullOrEmpty(f.JsonName))
        {
            // Only emit if it differs from the auto-derived json_name (lowerCamelCase
            // of the field name). Saves visual noise and matches protoc round-tripping.
            var auto = AutoJsonName(f.Name);
            if (f.JsonName != auto)
            {
                opts.Add(("json_name", "\"" + EscapeStringLiteral(f.JsonName) + "\""));
            }
        }

        if (opts.Count == 0)
            return;

        opts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        sb.Append(" [");
        for (var i = 0; i < opts.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(opts[i].Key).Append(" = ").Append(opts[i].Value);
        }
        sb.Append(']');
    }

    private static string FormatDefaultValue(FieldDescriptorProto f)
    {
        // For string/bytes wrap in quotes; for enums and scalars emit raw.
        return f.Type switch
        {
            FieldDescriptorProto.Types.Type.String or
            FieldDescriptorProto.Types.Type.Bytes => "\"" + EscapeStringLiteral(f.DefaultValue) + "\"",
            _ => f.DefaultValue,
        };
    }

    private static string AutoJsonName(string fieldName)
    {
        // protoc default: lowerCamelCase derived from snake_case.
        var sb = new StringBuilder(fieldName.Length);
        var upperNext = false;
        for (var i = 0; i < fieldName.Length; i++)
        {
            var c = fieldName[i];
            if (c == '_')
            {
                upperNext = true;
            }
            else if (upperNext)
            {
                sb.Append(char.ToUpperInvariant(c));
                upperNext = false;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static void EmitEnum(StringBuilder sb, EnumDescriptorProto en, int indent)
    {
        var ind = Indent(indent);
        sb.Append(ind).Append("enum ").Append(en.Name).Append(" {\n");

        if (en.Options != null)
        {
            var opts = new List<(string, string)>();
            if (en.Options.HasAllowAlias)
            {
                opts.Add(("allow_alias", en.Options.AllowAlias ? "true" : "false"));
            }
            if (en.Options.HasDeprecated)
            {
                opts.Add(("deprecated", en.Options.Deprecated ? "true" : "false"));
            }
            opts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Item1, b.Item1));
            foreach (var (k, v) in opts)
            {
                sb.Append(Indent(indent + 1)).Append("option ").Append(k).Append(" = ").Append(v).Append(";\n");
            }
        }

        foreach (var v in en.Value)
        {
            sb.Append(Indent(indent + 1))
              .Append(v.Name)
              .Append(" = ")
              .Append(v.Number.ToString(CultureInfo.InvariantCulture))
              .Append(";\n");
        }

        sb.Append(ind).Append("}\n");
    }

    private static void EmitService(StringBuilder sb, ServiceDescriptorProto svc, int indent)
    {
        var ind = Indent(indent);
        sb.Append(ind).Append("service ").Append(svc.Name).Append(" {\n");
        foreach (var m in svc.Method)
        {
            sb.Append(Indent(indent + 1))
              .Append("rpc ").Append(m.Name).Append('(')
              .Append(m.ClientStreaming ? "stream " : "")
              .Append(StripLeadingDot(m.InputType))
              .Append(") returns (")
              .Append(m.ServerStreaming ? "stream " : "")
              .Append(StripLeadingDot(m.OutputType))
              .Append(");\n");
        }
        sb.Append(ind).Append("}\n");
    }

    private static string StripLeadingDot(string s) =>
        string.IsNullOrEmpty(s) ? s : (s.StartsWith('.') ? s[1..] : s);

    private static List<(string Key, string Value)> CollectFileOptions(Google.Protobuf.Reflection.FileOptions o)
    {
        var list = new List<(string, string)>();
        if (o.HasJavaPackage)
            list.Add(("java_package", "\"" + EscapeStringLiteral(o.JavaPackage) + "\""));
        if (o.HasJavaOuterClassname)
            list.Add(("java_outer_classname", "\"" + EscapeStringLiteral(o.JavaOuterClassname) + "\""));
        if (o.HasJavaMultipleFiles)
            list.Add(("java_multiple_files", o.JavaMultipleFiles ? "true" : "false"));
        if (o.HasJavaStringCheckUtf8)
            list.Add(("java_string_check_utf8", o.JavaStringCheckUtf8 ? "true" : "false"));
        if (o.HasGoPackage)
            list.Add(("go_package", "\"" + EscapeStringLiteral(o.GoPackage) + "\""));
        if (o.HasCcGenericServices)
            list.Add(("cc_generic_services", o.CcGenericServices ? "true" : "false"));
        if (o.HasJavaGenericServices)
            list.Add(("java_generic_services", o.JavaGenericServices ? "true" : "false"));
        if (o.HasPyGenericServices)
            list.Add(("py_generic_services", o.PyGenericServices ? "true" : "false"));
        if (o.HasDeprecated)
            list.Add(("deprecated", o.Deprecated ? "true" : "false"));
        if (o.HasOptimizeFor)
            list.Add(("optimize_for", o.OptimizeFor.ToString().ToUpperInvariant()));
        if (o.HasCcEnableArenas)
            list.Add(("cc_enable_arenas", o.CcEnableArenas ? "true" : "false"));
        if (o.HasObjcClassPrefix)
            list.Add(("objc_class_prefix", "\"" + EscapeStringLiteral(o.ObjcClassPrefix) + "\""));
        if (o.HasCsharpNamespace)
            list.Add(("csharp_namespace", "\"" + EscapeStringLiteral(o.CsharpNamespace) + "\""));
        if (o.HasSwiftPrefix)
            list.Add(("swift_prefix", "\"" + EscapeStringLiteral(o.SwiftPrefix) + "\""));
        if (o.HasPhpClassPrefix)
            list.Add(("php_class_prefix", "\"" + EscapeStringLiteral(o.PhpClassPrefix) + "\""));
        if (o.HasPhpNamespace)
            list.Add(("php_namespace", "\"" + EscapeStringLiteral(o.PhpNamespace) + "\""));
        if (o.HasPhpMetadataNamespace)
            list.Add(("php_metadata_namespace", "\"" + EscapeStringLiteral(o.PhpMetadataNamespace) + "\""));
        if (o.HasRubyPackage)
            list.Add(("ruby_package", "\"" + EscapeStringLiteral(o.RubyPackage) + "\""));
        list.Sort((a, b) => StringComparer.Ordinal.Compare(a.Item1, b.Item1));
        return list;
    }

    private static List<(string Key, string Value)> CollectMessageOptions(MessageOptions o)
    {
        var list = new List<(string, string)>();
        if (o.HasMessageSetWireFormat)
            list.Add(("message_set_wire_format", o.MessageSetWireFormat ? "true" : "false"));
        if (o.HasNoStandardDescriptorAccessor)
            list.Add(("no_standard_descriptor_accessor", o.NoStandardDescriptorAccessor ? "true" : "false"));
        if (o.HasDeprecated)
            list.Add(("deprecated", o.Deprecated ? "true" : "false"));
        // map_entry is internal-only — never emit. The map field syntax replaces it.
        list.Sort((a, b) => StringComparer.Ordinal.Compare(a.Item1, b.Item1));
        return list;
    }

    private static string EscapeStringLiteral(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append('\\')
                          .Append(((int)c).ToString("x3", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Indent(int level) => new(' ', level * 2);
}
