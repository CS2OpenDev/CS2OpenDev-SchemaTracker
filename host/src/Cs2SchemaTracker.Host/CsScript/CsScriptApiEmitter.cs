// cs_script API extraction (cs_script_api.json).
//
// Pipeline: read Valve's point_script.d.ts from the Workshop Tools content tree -> parse the
// declaration subset (TsDeclarationParser) -> lift JSDoc tags into typed fields, resolve
// referenced_types against the module's own declarations, number overloads -> read the ES target
// from the tsconfig.json shipped beside it -> canonical proto3 JSON -> atomic .tmp+rename.
//
// === source ===
// content/csgo_addons/cs_script_demo/maps/scripts/point_script.d.ts in the Workshop Tools depot
// (2347779). Valve's header names this copy as the maintained one ("These two files will be
// maintained as the cs_script API changes"). A second copy under content/csgo/maps/editor/zoo/
// scripts/ is a one-line stub pointing here and is not read.
//
// === mapping decisions ===
//   - JSDoc: description -> doc; @deprecated -> deprecated + deprecation_note; @experimental ->
//     experimental + experimental_note; @param -> param_docs; @returns/@return -> returns_doc;
//     @example -> examples; @default -> default_value. Anything else (and any tag a given record
//     has no field for) -> other_tags, so no tag Valve writes is dropped.
//   - referenced_types keeps only names this module declares, Ordinal-sorted and de-duplicated.
//   - overload_index counts per member name within a type, in declaration order.
//   - types/enums sorted by name Ordinal; exports, members, params and enum members keep
//     declaration order (see schemas/cs_script_api.proto).
//
// Fail-loud: a missing declaration file, a parse failure, a duplicate declared name, zero
// declarations, or a tsconfig.json that does not parse all throw BEFORE any bytes are written. The
// tsconfig.json is optional (es_target is then ""), the declaration file is not.

using System.Text.Json;

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.ToolsContent;
using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.CsScript;

internal sealed class CsScriptApiEmitter
{
    public const string DeclarationPath = "content/csgo_addons/cs_script_demo/maps/scripts/point_script.d.ts";
    public const string TsconfigPath = "content/csgo_addons/cs_script_demo/maps/scripts/tsconfig.json";

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public CsScriptApiEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>True iff the tools content tree ships the declaration file.</summary>
    public static bool HasSource(ToolsContentTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Exists(DeclarationPath);
    }

    /// <summary>
    /// Emit cs_script_api.json. Returns the depot-relative paths of every source file read (for
    /// provenance.inputs), Ordinal-sorted.
    /// </summary>
    public IReadOnlyList<string> Emit(ToolsContentTree tree, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var document = Build(tree, out var sourcesRead);
        AtomicWrite.WriteCanonical(document, outputPath);
        return sourcesRead;
    }

    internal CsScriptApi Build(ToolsContentTree tree, out IReadOnlyList<string> sourcesRead)
    {
        if (!HasSource(tree))
        {
            throw new InvalidDataException(
                $"CsScriptApiEmitter: '{DeclarationPath}' is not present under '{tree.Root}' — refusing to "
                + "write cs_script_api.json.");
        }
        var read = new List<string> { DeclarationPath };
        byte[] dtsBytes = tree.ReadAllBytes(DeclarationPath);
        string dts = ToolsContentTree.DecodeText(dtsBytes, DeclarationPath);

        ParsedModule module;
        try
        {
            module = TsDeclarationParser.Parse(dts, DeclarationPath);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"CsScriptApiEmitter: {ex.Message}", ex);
        }

        var document = new CsScriptApi
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
            SourcePath = DeclarationPath,
            SourceSha256 = ToolsContentTree.Sha256Hex(dtsBytes),
            Module = module.Name,
            ModuleDoc = module.ModuleDoc.Description,
            FileDoc = module.FileDoc.Description,
        };

        if (tree.Exists(TsconfigPath))
        {
            read.Add(TsconfigPath);
            byte[] cfgBytes = tree.ReadAllBytes(TsconfigPath);
            document.TsconfigPath = TsconfigPath;
            document.TsconfigSha256 = ToolsContentTree.Sha256Hex(cfgBytes);
            document.EsTarget = ReadEsTarget(ToolsContentTree.DecodeText(cfgBytes, TsconfigPath));
        }

        // Every name the module declares; duplicates across types and enums are a hard error
        // (declaration merging is outside the supported subset).
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in module.Types.Select(t => t.Name).Concat(module.Enums.Select(e => e.Name)))
        {
            if (!declared.Add(name))
            {
                throw new InvalidDataException(
                    $"CsScriptApiEmitter: '{DeclarationPath}' declares '{name}' more than once "
                    + "(declaration merging is not supported).");
            }
        }
        if (declared.Count == 0)
        {
            throw new InvalidDataException(
                $"CsScriptApiEmitter: '{DeclarationPath}' declares no types or enums — refusing to "
                + "write an empty cs_script_api.json.");
        }

        foreach (var ex in module.Exports)
        {
            var export = new CsScriptExport
            {
                Name = ex.Name,
                Type = ex.Type.Render(),
                Doc = ex.Doc.Description,
            };
            export.ReferencedTypes.AddRange(FilterRefs(ex.Refs, declared));
            export.OtherTags.AddRange(ex.Doc.Tags.Select(ToTag));
            document.Exports.Add(export);
        }

        foreach (var t in module.Types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            document.Types_.Add(ToType(t, declared));
        }
        foreach (var e in module.Enums.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            document.Enums.Add(ToEnum(e));
        }

        read.Sort(StringComparer.Ordinal);
        sourcesRead = read;
        return document;
    }

    private static string ReadEsTarget(string tsconfigText)
    {
        try
        {
            using var doc = JsonDocument.Parse(tsconfigText, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("compilerOptions", out var opts)
                && opts.ValueKind == JsonValueKind.Object
                && opts.TryGetProperty("target", out var target))
            {
                if (target.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException(
                        $"CsScriptApiEmitter: '{TsconfigPath}' compilerOptions.target is not a string.");
                }
                return target.GetString() ?? "";
            }
            return "";
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"CsScriptApiEmitter: '{TsconfigPath}' is not valid JSON: {ex.Message}", ex);
        }
    }

    private static IEnumerable<string> FilterRefs(IEnumerable<string> refs, HashSet<string> declared)
        => refs.Where(declared.Contains).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal);

    private static CsScriptTag ToTag(JsDocTag t) => new() { Tag = t.Name, Text = t.Text };

    private static CsScriptType ToType(ParsedType t, HashSet<string> declared)
    {
        var type = new CsScriptType
        {
            Name = t.Name,
            Kind = t.Kind,
            Exported = t.Exported,
            Doc = t.Doc.Description,
        };
        type.Extends.AddRange(t.Extends);
        type.Implements.AddRange(t.Implements);
        type.TypeParams.AddRange(t.TypeParams);
        foreach (var tag in t.Doc.Tags)
        {
            switch (tag.Name)
            {
                case "deprecated":
                    type.Deprecated = true;
                    type.DeprecationNote = tag.Text;
                    break;
                case "experimental":
                    type.Experimental = true;
                    type.ExperimentalNote = tag.Text;
                    break;
                case "example":
                    type.Examples.Add(tag.Text);
                    break;
                default:
                    type.OtherTags.Add(ToTag(tag));
                    break;
            }
        }
        if (t.AliasType is not null)
        {
            type.TypeText = t.AliasType.Render();
            type.ReferencedTypes.AddRange(FilterRefs(t.AliasRefs, declared));
        }

        var overloads = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var m in t.Members)
        {
            type.Members.Add(ToMember(m, declared, overloads));
        }
        return type;
    }

    private static CsScriptMember ToMember(ParsedMember m, HashSet<string> declared, Dictionary<string, uint> overloads)
    {
        var sig = m.Sig;
        overloads.TryGetValue(sig.Name, out var index);
        overloads[sig.Name] = index + 1;

        var member = new CsScriptMember
        {
            Name = sig.Name,
            Kind = sig.IsMethod ? "method" : "property",
            OverloadIndex = index,
            SignatureText = sig.Render(),
            Optional = sig.Optional,
            Doc = m.Doc.Description,
        };
        member.Modifiers.AddRange(sig.Modifiers);
        if (sig.IsMethod)
        {
            member.TypeParams.AddRange(sig.TypeParams);
            foreach (var p in sig.Params!)
            {
                member.Params.Add(new CsScriptParam
                {
                    Name = p.Name,
                    TypeText = p.Type?.Render() ?? "",
                    Optional = p.Optional,
                    Rest = p.Rest,
                });
            }
            member.ReturnTypeText = sig.ReturnType?.Render() ?? "";
        }
        else
        {
            member.TypeText = sig.PropertyType?.Render() ?? "";
        }

        foreach (var tag in m.Doc.Tags)
        {
            switch (tag.Name)
            {
                case "deprecated":
                    member.Deprecated = true;
                    member.DeprecationNote = tag.Text;
                    break;
                case "experimental":
                    member.Experimental = true;
                    member.ExperimentalNote = tag.Text;
                    break;
                case "example":
                    member.Examples.Add(tag.Text);
                    break;
                case "default" when member.DefaultValue.Length == 0:
                    member.DefaultValue = tag.Text;
                    break;
                case "returns" or "return" when member.ReturnsDoc.Length == 0:
                    member.ReturnsDoc = tag.Text;
                    break;
                case "param":
                    if (TrySplitParamTag(tag.Text, out var pname, out var ptext) && !member.ParamDocs.ContainsKey(pname))
                        member.ParamDocs[pname] = ptext;
                    else
                        member.OtherTags.Add(ToTag(tag));
                    break;
                default:
                    member.OtherTags.Add(ToTag(tag));
                    break;
            }
        }
        member.ReferencedTypes.AddRange(FilterRefs(m.Refs, declared));
        return member;
    }

    /// <summary>
    /// "@param name text", "@param {Type} name text", "@param [name] text". The text may be empty
    /// (Valve writes "@param callback" and puts the explanation on the next line).
    /// </summary>
    private static bool TrySplitParamTag(string text, out string name, out string rest)
    {
        name = "";
        rest = "";
        var s = text.TrimStart();
        if (s.StartsWith('{'))
        {
            int close = s.IndexOf('}', StringComparison.Ordinal);
            if (close < 0)
                return false;
            s = s[(close + 1)..].TrimStart();
        }
        int end = 0;
        while (end < s.Length && !char.IsWhiteSpace(s[end]))
            end++;
        name = s[..end].Trim('[', ']');
        int eq = name.IndexOf('=', StringComparison.Ordinal);
        if (eq >= 0)
            name = name[..eq];
        if (name.Length == 0)
            return false;
        rest = s[end..].Trim();
        return true;
    }

    private static CsScriptEnum ToEnum(ParsedEnum e)
    {
        var en = new CsScriptEnum
        {
            Name = e.Name,
            Exported = e.Exported,
            Doc = e.Doc.Description,
        };
        foreach (var tag in e.Doc.Tags)
        {
            switch (tag.Name)
            {
                case "deprecated":
                    en.Deprecated = true;
                    en.DeprecationNote = tag.Text;
                    break;
                case "experimental":
                    en.Experimental = true;
                    en.ExperimentalNote = tag.Text;
                    break;
                default:
                    en.OtherTags.Add(ToTag(tag));
                    break;
            }
        }
        foreach (var m in e.Members)
        {
            var member = new CsScriptEnumMember
            {
                Name = m.Name,
                Value = m.Value,
                Doc = m.Doc.Description,
                InitializerText = m.InitializerText,
            };
            foreach (var tag in m.Doc.Tags)
            {
                if (tag.Name == "deprecated" && !member.Deprecated)
                {
                    member.Deprecated = true;
                    member.DeprecationNote = tag.Text;
                }
                else
                {
                    member.OtherTags.Add(ToTag(tag));
                }
            }
            en.Members.Add(member);
        }
        return en;
    }
}
