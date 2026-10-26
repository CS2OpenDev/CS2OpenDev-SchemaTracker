// Entity I/O extraction (entity_io.json) from the Hammer FGD files.
//
// Pipeline: parse game/csgo/csgo.fgd (FgdParser) -> expand @include in place (game/csgo/ first,
// then game/core/) -> apply @exclude / class definitions / @OverrideClass in include-expanded order
// -> resolve inheritance per class -> canonical proto3 JSON -> atomic .tmp+rename.
//
// === resolution rules (see schemas/entity_io.proto for the consumer-facing statement) ===
//   - Include-once: a file already parsed in this run is not parsed again. CS2's include graph has
//     no repeats today; parsing a file twice would redefine every class in it.
//   - Class names compare case-insensitively (Hammer's rule). Defining a class whose name is
//     already live is a hard failure UNLESS it was @exclude'd first — exclude-then-redefine is how
//     csgo.fgd replaces engine classes, and an accidental duplicate must not silently win.
//   - @OverrideClass / @ExtendClass merge into the live class: new bases and helpers append, a
//     non-empty description replaces, keyvalues and inputs/outputs replace by name in place or
//     append. Overriding a class that is not live is a hard failure.
//   - Inheritance: bases (in order, recursively) then the class's own items; a same-named item later
//     in that order replaces the earlier one IN PLACE (so the UI order Hammer shows is kept), except
//     flags-typed keyvalues, whose flags merge per bit. A base that names an excluded class still
//     resolves against the excluded definition (exclusion removes a placeable class from the list,
//     it does not rewrite its descendants). An unknown base or a cycle is a hard failure.
//
// Fail-loud: missing root or include, any parse failure, the rules above, a flag value that is not
// a 32-bit unsigned integer, or zero classes — all throw BEFORE any output bytes.

using System.Globalization;

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.ToolsContent;
using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.EntityIo;

internal sealed class EntityIoEmitter
{
    public const string RootPath = "game/csgo/csgo.fgd";

    /// <summary>Directories an @include is resolved against, in order (Hammer: the mod, then core).</summary>
    public static readonly IReadOnlyList<string> IncludeSearchDirs = new[] { "game/csgo", "game/core" };

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public EntityIoEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>True iff the tools content tree ships the root FGD.</summary>
    public static bool HasSource(ToolsContentTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Exists(RootPath);
    }

    /// <summary>
    /// Emit entity_io.json. Returns the depot-relative paths of every FGD read (for
    /// provenance.inputs), Ordinal-sorted.
    /// </summary>
    public IReadOnlyList<string> Emit(ToolsContentTree tree, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var document = Build(tree);
        AtomicWrite.WriteCanonical(document, outputPath);
        return document.Sources.Select(s => s.Path).ToList();
    }

    internal Schemas.EntityIo Build(ToolsContentTree tree)
    {
        if (!HasSource(tree))
        {
            throw new InvalidDataException(
                $"EntityIoEmitter: '{RootPath}' is not present under '{tree.Root}' — refusing to write entity_io.json.");
        }

        var state = new LoadState(tree);
        try
        {
            state.LoadFile(RootPath);
        }
        catch (InvalidDataException ex) when (!ex.Message.StartsWith("EntityIoEmitter:", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"EntityIoEmitter: {ex.Message}", ex);
        }

        if (state.Live.Count == 0)
        {
            throw new InvalidDataException(
                $"EntityIoEmitter: '{RootPath}' and its includes define no classes — refusing to write an empty entity_io.json.");
        }

        var document = new Schemas.EntityIo
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
            Root = RootPath,
        };
        foreach (var (path, sha) in state.Sources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            document.Sources.Add(new FgdSource { Path = path, Sha256 = sha });
        }
        foreach (var (name, file) in state.Excluded
                     .Where(kv => !state.Live.ContainsKey(kv.Key))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            document.Excluded.Add(new FgdExclusion { Name = name, SourceFile = file });
        }

        var resolver = new Resolver(state);
        foreach (var cls in state.Live.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            document.Classes.Add(resolver.ToEntityClass(cls));
        }
        return document;
    }

    // ---- loading: includes, exclusions, overrides -------------------------------------------

    private sealed class ClassState
    {
        public required string Name { get; init; }
        public required string ClassType { get; init; }
        public required string SourceFile { get; init; }
        public string Description { get; set; } = "";
        public List<string> Bases { get; } = new();
        public List<string> Helpers { get; } = new();
        public List<FgdKeyValueDecl> KeyValues { get; } = new();
        public List<FgdIoDecl> Ports { get; } = new();
        public List<string> OverrideSources { get; } = new();
    }

    private sealed class LoadState
    {
        private readonly ToolsContentTree _tree;

        public LoadState(ToolsContentTree tree) => _tree = tree;

        public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);

        /// <summary>Live (placeable or base) classes, keyed case-insensitively.</summary>
        public Dictionary<string, ClassState> Live { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Definitions removed by @exclude — kept so descendants can still resolve them.</summary>
        public Dictionary<string, ClassState> Graveyard { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Excluded name -> the file of the (last) @exclude naming it.</summary>
        public Dictionary<string, string> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void LoadFile(string path)
        {
            if (Sources.ContainsKey(path))
                return;   // include-once.
            byte[] bytes = _tree.ReadAllBytes(path);
            Sources[path] = ToolsContentTree.Sha256Hex(bytes);
            var items = FgdParser.Parse(ToolsContentTree.DecodeText(bytes, path), path);
            foreach (var item in items)
            {
                switch (item)
                {
                    case FgdIncludeItem inc:
                        LoadFile(ResolveInclude(inc));
                        break;
                    case FgdExcludeItem ex:
                        if (Live.Remove(ex.ClassName, out var removed))
                            Graveyard[ex.ClassName] = removed;
                        Excluded[ex.ClassName] = ex.SourceFile;
                        break;
                    case FgdClassItem c:
                        Apply(c.Decl);
                        break;
                }
            }
        }

        private string ResolveInclude(FgdIncludeItem inc)
        {
            var target = inc.Target.Replace('\\', '/');
            foreach (var dir in IncludeSearchDirs)
            {
                var candidate = dir + "/" + target;
                if (_tree.Exists(candidate))
                    return candidate;
            }
            throw new InvalidDataException(
                $"EntityIoEmitter: {inc.SourceFile}:{inc.Line}: @include \"{inc.Target}\" not found under "
                + string.Join(" or ", IncludeSearchDirs.Select(d => d + "/")) + ".");
        }

        private void Apply(FgdClassDecl d)
        {
            bool isOverride = d.ClassType.Equals("OverrideClass", StringComparison.OrdinalIgnoreCase)
                || d.ClassType.Equals("ExtendClass", StringComparison.OrdinalIgnoreCase);
            if (isOverride)
            {
                if (!Live.TryGetValue(d.Name, out var target))
                {
                    throw new InvalidDataException(
                        $"EntityIoEmitter: {d.SourceFile}:{d.Line}: @{d.ClassType} '{d.Name}' names no class defined before it.");
                }
                foreach (var b in d.Bases)
                {
                    if (!target.Bases.Contains(b, StringComparer.OrdinalIgnoreCase))
                        target.Bases.Add(b);
                }
                target.Helpers.AddRange(d.Helpers);
                if (d.Description.Length > 0)
                    target.Description = d.Description;
                foreach (var kv in d.KeyValues)
                    ReplaceOrAppend(target.KeyValues, kv, k => k.Name);
                foreach (var p in d.Ports)
                    ReplaceOrAppend(target.Ports, p, x => (x.IsInput ? "i:" : "o:") + x.Name);
                target.OverrideSources.Add(d.SourceFile);
                return;
            }

            if (Live.TryGetValue(d.Name, out var existing))
            {
                throw new InvalidDataException(
                    $"EntityIoEmitter: {d.SourceFile}:{d.Line}: class '{d.Name}' is already defined in "
                    + $"{existing.SourceFile} and was not @exclude'd first.");
            }
            var cls = new ClassState
            {
                Name = d.Name,
                ClassType = d.ClassType,
                SourceFile = d.SourceFile,
                Description = d.Description,
            };
            cls.Bases.AddRange(d.Bases);
            cls.Helpers.AddRange(d.Helpers);
            cls.KeyValues.AddRange(d.KeyValues);
            cls.Ports.AddRange(d.Ports);
            Live[d.Name] = cls;
        }

        public ClassState? Lookup(string name)
            => Live.TryGetValue(name, out var c) ? c : Graveyard.TryGetValue(name, out var g) ? g : null;
    }

    private static void ReplaceOrAppend<T>(List<T> list, T item, Func<T, string> key)
    {
        var k = key(item);
        int idx = list.FindIndex(x => string.Equals(key(x), k, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            list[idx] = item;
        else
            list.Add(item);
    }

    // ---- inheritance ----------------------------------------------------------------------

    private sealed class Resolved
    {
        public List<EntityKeyValue> KeyValues { get; } = new();
        public List<EntityIoPort> Inputs { get; } = new();
        public List<EntityIoPort> Outputs { get; } = new();
    }

    private sealed class Resolver
    {
        private readonly LoadState _state;
        private readonly Dictionary<ClassState, Resolved> _memo = new();
        private readonly HashSet<ClassState> _visiting = new();

        public Resolver(LoadState state) => _state = state;

        public EntityClass ToEntityClass(ClassState c)
        {
            var r = Resolve(c, new List<string>());
            var ec = new EntityClass
            {
                Name = c.Name,
                ClassType = c.ClassType,
                IsBase = c.ClassType.Equals("BaseClass", StringComparison.OrdinalIgnoreCase),
                Description = c.Description,
                SourceFile = c.SourceFile,
            };
            ec.Bases.AddRange(c.Bases);
            ec.Helpers.AddRange(c.Helpers);
            ec.OverrideSources.AddRange(c.OverrideSources);
            ec.Keyvalues.AddRange(r.KeyValues.Select(k => k.Clone()));
            ec.Inputs.AddRange(r.Inputs.Select(p => p.Clone()));
            ec.Outputs.AddRange(r.Outputs.Select(p => p.Clone()));
            return ec;
        }

        private Resolved Resolve(ClassState c, List<string> chain)
        {
            if (_memo.TryGetValue(c, out var done))
                return done;
            if (!_visiting.Add(c))
            {
                throw new InvalidDataException(
                    $"EntityIoEmitter: inheritance cycle: {string.Join(" -> ", chain.Append(c.Name))}.");
            }
            chain.Add(c.Name);

            var r = new Resolved();
            foreach (var baseName in c.Bases)
            {
                var b = _state.Lookup(baseName) ?? throw new InvalidDataException(
                    $"EntityIoEmitter: {c.SourceFile}: class '{c.Name}' names unknown base class '{baseName}'.");
                var rb = Resolve(b, chain);
                foreach (var kv in rb.KeyValues)
                    MergeKeyValue(r.KeyValues, kv);
                foreach (var p in rb.Inputs)
                    MergePort(r.Inputs, p);
                foreach (var p in rb.Outputs)
                    MergePort(r.Outputs, p);
            }
            foreach (var decl in c.KeyValues)
                MergeKeyValue(r.KeyValues, ToKeyValue(decl, c));
            foreach (var decl in c.Ports)
            {
                var port = new EntityIoPort
                {
                    Name = decl.Name,
                    ParamType = decl.ParamType,
                    Description = decl.Description,
                    Metadata = decl.Metadata,
                    DeclaredIn = c.Name,
                };
                MergePort(decl.IsInput ? r.Inputs : r.Outputs, port);
            }

            chain.RemoveAt(chain.Count - 1);
            _visiting.Remove(c);
            _memo[c] = r;
            return r;
        }

        private static void MergePort(List<EntityIoPort> list, EntityIoPort port)
        {
            int idx = list.FindIndex(x => string.Equals(x.Name, port.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                list[idx] = port;
            else
                list.Add(port);
        }

        private static void MergeKeyValue(List<EntityKeyValue> list, EntityKeyValue kv)
        {
            int idx = list.FindIndex(x => string.Equals(x.Name, kv.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                list.Add(kv);
                return;
            }
            var prior = list[idx];
            if (prior.Flags.Count > 0 && IsFlagsType(kv.Type))
            {
                var merged = kv.Clone();
                merged.Flags.Clear();
                merged.Flags.AddRange(prior.Flags.Select(f => f.Clone()));
                foreach (var f in kv.Flags)
                {
                    int fi = merged.Flags.ToList().FindIndex(x => x.Value == f.Value);
                    if (fi >= 0)
                        merged.Flags[fi] = f.Clone();
                    else
                        merged.Flags.Add(f.Clone());
                }
                list[idx] = merged;
                return;
            }
            list[idx] = kv;
        }

        private static bool IsFlagsType(string type) => type.Equals("flags", StringComparison.OrdinalIgnoreCase);

        private static EntityKeyValue ToKeyValue(FgdKeyValueDecl d, ClassState owner)
        {
            var kv = new EntityKeyValue
            {
                Name = d.Name,
                Type = d.Type,
                DisplayName = d.DisplayName,
                Description = d.Description,
                Metadata = d.Metadata,
                DeclaredIn = owner.Name,
            };
            if (d.DefaultValue is not null)
                kv.DefaultValue = d.DefaultValue;

            bool flags = IsFlagsType(d.Type);
            foreach (var e in d.Entries)
            {
                if (flags)
                {
                    if (!uint.TryParse(e.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bit))
                    {
                        throw new InvalidDataException(
                            $"EntityIoEmitter: {owner.SourceFile}:{d.Line}: flag value '{e.Value}' in "
                            + $"'{owner.Name}.{d.Name}' is not a 32-bit unsigned integer.");
                    }
                    bool on = e.Third switch
                    {
                        null or "0" => false,
                        "1" => true,
                        _ => throw new InvalidDataException(
                            $"EntityIoEmitter: {owner.SourceFile}:{d.Line}: flag {e.Value} in "
                            + $"'{owner.Name}.{d.Name}' has default '{e.Third}' (expected 0 or 1)."),
                    };
                    kv.Flags.Add(new FgdFlag
                    {
                        Value = bit,
                        Label = e.Label,
                        Default = on,
                        DeclaredIn = owner.Name,
                        Description = e.Description,
                    });
                }
                else
                {
                    var choice = new FgdChoice { Value = e.Value, Label = e.Label, Description = e.Description };
                    if (e.Third is not null)
                        choice.DefaultValue = e.Third;
                    kv.Choices.Add(choice);
                }
            }
            return kv;
        }
    }
}
