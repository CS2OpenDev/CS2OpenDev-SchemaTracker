// The structural diff over two entity_schema.json snapshots -> one Transition (schema_evolution.proto).
//
// FACTS ONLY. Every field of the produced Transition is derivable from the two SchemaClass/SchemaEnum
// graphs and nothing else. There is no rename claim (only neutral PairedEvidence), no safety verdict,
// and no inference. The type-equality decisions ride entirely on SchemaTypeRenderer (the pinned
// oracle).
//
// This mirrors BuildChangelogEmitter's idioms BY PATTERN (qualified "<module>/<name>" keys, ByName
// fail-loud on duplicate keys, Ordinal sort, culture-invariant scalars) rather than sharing a core:
// evolution is the only field-level consumer, so a shared abstraction would be shaped entirely by it.
// The changelog's DiffClasses is left untouched.
//
// Determinism: class_added/removed, enum_added/removed, class_changed, enum_changed are Ordinal by
// name; field_ops Ordinal by (field, kind); member_ops Ordinal by (member, kind); paired_evidence
// Ordinal by (from, to). All scalar rendering is InvariantCulture.

using System.Globalization;

using Cs2SchemaTracker.Schemas;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.Evolution;

/// <summary>
/// Diffs two <see cref="EntitySchema"/> snapshots into a single <see cref="Transition"/>. Pure of
/// I/O; a deterministic function of the two graphs. See file header.
/// </summary>
public static class SchemaSnapshotDiff
{
    // Fixed name -> byte width table for the closed set of BUILTIN primitives observed in committed
    // schemas (void has no value width and is deliberately absent). A builtin's width is definitional
    // (the meaning of the type name), so emitting it is a fact, not a consumer-impact verdict. A
    // primitive NOT in this table simply gets no width (absent) — never a guessed one.
    private static readonly Dictionary<string, ulong> BuiltinBytes = new(StringComparer.Ordinal)
    {
        ["bool"] = 1,
        ["char"] = 1,
        ["int8"] = 1,
        ["uint8"] = 1,
        ["int16"] = 2,
        ["uint16"] = 2,
        ["int32"] = 4,
        ["uint32"] = 4,
        ["float32"] = 4,
        ["int64"] = 8,
        ["uint64"] = 8,
        ["float64"] = 8,
    };

    /// <summary>
    /// Diff <paramref name="from"/> -> <paramref name="to"/> into a <see cref="Transition"/> stamped
    /// with the two build ids.
    /// </summary>
    public static Transition Diff(Schemas.EntitySchema from, Schemas.EntitySchema to, string fromBuild, string toBuild)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrEmpty(fromBuild);
        ArgumentException.ThrowIfNullOrEmpty(toBuild);

        var transition = new Transition { FromBuild = fromBuild, ToBuild = toBuild };
        DiffClasses(from, to, transition);
        DiffEnums(from, to, transition);
        return transition;
    }

    // ---- classes --------------------------------------------------------------------------

    private static void DiffClasses(Schemas.EntitySchema from, Schemas.EntitySchema to, Transition transition)
    {
        var fromMap = IndexBy(from.Classes, c => Qualify(c.Module, c.Name), "class");
        var toMap = IndexBy(to.Classes, c => Qualify(c.Module, c.Name), "class");

        foreach (var name in AddedKeys(fromMap, toMap))
            transition.ClassAdded.Add(name);
        foreach (var name in RemovedKeys(fromMap, toMap))
            transition.ClassRemoved.Add(name);

        // Removed/added field pools across the MATCHED classes, feeding the transition-level
        // field-move candidates. The rendered type rides along so each field is rendered once.
        var removedPool = new List<(string Cls, string Field, string RenderedType)>();
        var addedPool = new List<(string Cls, string Field, string RenderedType)>();

        foreach (var name in MatchedKeys(fromMap, toMap))
        {
            var delta = DiffClass(name, fromMap[name], toMap[name], removedPool, addedPool);
            if (delta is not null)
                transition.ClassChanged.Add(delta);
        }

        foreach (var pair in ClassPairCandidates(transition, fromMap, toMap))
            transition.ClassPairCandidates.Add(pair);
        foreach (var move in FieldMoveCandidates(removedPool, addedPool, toMap))
            transition.FieldMoveCandidates.Add(move);
    }

    private static ClassDelta? DiffClass(
        string name, SchemaClass oldC, SchemaClass newC,
        List<(string Cls, string Field, string RenderedType)> removedPool,
        List<(string Cls, string Field, string RenderedType)> addedPool)
    {
        var delta = new ClassDelta { Name = name };

        var oldFields = IndexBy(oldC.Fields, f => f.Name, "field");
        var newFields = IndexBy(newC.Fields, f => f.Name, "field");

        delta.FieldOps.AddRange(ComputeFieldOps(oldC, newC, oldFields, newFields));

        // Pool this class's removed/added instance fields for the transition-level field-move
        // candidates. Statics stay out of every pairing surface by design.
        foreach (var fname in AddedKeys(oldFields, newFields))
            addedPool.Add((name, fname, SchemaTypeRenderer.Render(newFields[fname].Type!)));
        foreach (var fname in RemovedKeys(oldFields, newFields))
            removedPool.Add((name, fname, SchemaTypeRenderer.Render(oldFields[fname].Type!)));

        // Static fields: the same op vocabulary over SchemaClass.static_fields (issue #7 item 3).
        var oldStatics = IndexBy(oldC.StaticFields, f => f.Name, "static field");
        var newStatics = IndexBy(newC.StaticFields, f => f.Name, "static field");
        delta.StaticFieldOps.AddRange(ComputeFieldOps(oldC, newC, oldStatics, newStatics));

        // Class-level scalar changes.
        var oldParents = Parents(oldC);
        var newParents = Parents(newC);
        if (!oldParents.SequenceEqual(newParents, StringComparer.Ordinal))
        {
            var reparent = new Reparent();
            reparent.From.AddRange(oldParents);
            reparent.To.AddRange(newParents);
            delta.Reparent = reparent;
        }
        if (oldC.Size != newC.Size)
            delta.Resize = new ScalarChange { From = Inv(oldC.Size), To = Inv(newC.Size) };
        if (oldC.Alignment != newC.Alignment)
            delta.Realign = new ScalarChange { From = Inv(oldC.Alignment), To = Inv(newC.Alignment) };
        if (oldC.Flags != newC.Flags)
            delta.Flags = new ScalarChange { From = Inv(oldC.Flags), To = Inv(newC.Flags) };

        // The attribute coverage issue #7 item 3 asked for (previously silent in this record).
        if (oldC.Flags2 != newC.Flags2)
            delta.Flags2 = new ScalarChange { From = Inv(oldC.Flags2), To = Inv(newC.Flags2) };
        var oldClassMeta = RenderMeta(oldC.Metadata);
        var newClassMeta = RenderMeta(newC.Metadata);
        if (!string.Equals(oldClassMeta, newClassMeta, StringComparison.Ordinal))
            delta.Meta = new ScalarChange { From = oldClassMeta, To = newClassMeta };
        if (!string.Equals(oldC.CppName, newC.CppName, StringComparison.Ordinal))
            delta.CppName = new ScalarChange { From = oldC.CppName, To = newC.CppName };
        if (!string.Equals(oldC.ProjectName, newC.ProjectName, StringComparison.Ordinal))
            delta.ProjectName = new ScalarChange { From = oldC.ProjectName, To = newC.ProjectName };
        if (oldC.SingleInheritanceDepth != newC.SingleInheritanceDepth)
        {
            delta.SingleInheritanceDepth = new ScalarChange
            { From = Inv(oldC.SingleInheritanceDepth), To = Inv(newC.SingleInheritanceDepth) };
        }
        if (oldC.MultipleInheritanceDepth != newC.MultipleInheritanceDepth)
        {
            delta.MultipleInheritanceDepth = new ScalarChange
            { From = Inv(oldC.MultipleInheritanceDepth), To = Inv(newC.MultipleInheritanceDepth) };
        }

        // Neutral paired evidence over the removed/added field sets — NOT a rename claim.
        foreach (var pair in PairEvidence(oldFields, newFields))
            delta.PairedEvidence.Add(pair);

        // The wider, unselected candidate surface over the same sets (see PairCandidates).
        foreach (var candidate in PairCandidates(oldFields, newFields))
            delta.PairCandidates.Add(candidate);

        var changed = delta.FieldOps.Count > 0
            || delta.StaticFieldOps.Count > 0
            || delta.Reparent is not null
            || delta.Resize is not null
            || delta.Realign is not null
            || delta.Flags is not null
            || delta.Flags2 is not null
            || delta.Meta is not null
            || delta.CppName is not null
            || delta.ProjectName is not null
            || delta.SingleInheritanceDepth is not null
            || delta.MultipleInheritanceDepth is not null
            || delta.PairedEvidence.Count > 0;
        return changed ? delta : null;
    }

    /// <summary>
    /// The full op vocabulary (ADD / REMOVE / TYPE_CHANGE / OFFSET_CHANGE / META_CHANGE) over one
    /// keyed field set — instance fields and static fields share this exactly. Matched fields emit
    /// INDEPENDENT ops (one field may emit several). Sorted Ordinal by (field, kind).
    /// <paramref name="oldC"/>/<paramref name="newC"/> are only for fail-loud messages.
    /// </summary>
    private static List<FieldOp> ComputeFieldOps(
        SchemaClass oldC, SchemaClass newC,
        Dictionary<string, SchemaField> oldFields, Dictionary<string, SchemaField> newFields)
    {
        var ops = new List<FieldOp>();

        foreach (var fname in AddedKeys(oldFields, newFields))
            ops.Add(AddOp(newFields[fname]));
        foreach (var fname in RemovedKeys(oldFields, newFields))
            ops.Add(RemoveOp(oldFields[fname]));

        foreach (var fname in MatchedKeys(oldFields, newFields))
        {
            var of = oldFields[fname];
            var nf = newFields[fname];

            var oldType = of.Type ?? throw Corrupt(oldC, of, "field has no type");
            var newType = nf.Type ?? throw Corrupt(newC, nf, "field has no type");
            if (!string.Equals(SchemaTypeRenderer.Render(oldType), SchemaTypeRenderer.Render(newType),
                    StringComparison.Ordinal))
            {
                ops.Add(new FieldOp
                {
                    Kind = FieldOp.Types.Kind.TypeChange,
                    Field = fname,
                    FromType = oldType.Clone(),
                    ToType = newType.Clone(),
                    FromWidth = WidthOf(oldType),
                    ToWidth = WidthOf(newType),
                });
            }

            if (of.Offset != nf.Offset)
            {
                ops.Add(new FieldOp
                {
                    Kind = FieldOp.Types.Kind.OffsetChange,
                    Field = fname,
                    FromOffset = Inv(of.Offset),
                    ToOffset = Inv(nf.Offset),
                });
            }

            var oldMeta = RenderMeta(of.Metadata);
            var newMeta = RenderMeta(nf.Metadata);
            if (!string.Equals(oldMeta, newMeta, StringComparison.Ordinal))
            {
                ops.Add(new FieldOp
                {
                    Kind = FieldOp.Types.Kind.MetaChange,
                    Field = fname,
                    FromMeta = oldMeta,
                    ToMeta = newMeta,
                });
            }
        }

        ops.Sort(static (a, b) =>
        {
            var byField = string.CompareOrdinal(a.Field, b.Field);
            return byField != 0 ? byField : ((int)a.Kind).CompareTo((int)b.Kind);
        });
        return ops;
    }

    private static FieldOp AddOp(SchemaField f)
    {
        var t = f.Type ?? throw new InvalidDataException(
            $"SchemaSnapshotDiff: added field '{f.Name}' has no type (corrupt schema).");
        return new FieldOp
        {
            Kind = FieldOp.Types.Kind.Add,
            Field = f.Name,
            ToType = t.Clone(),
            ToOffset = Inv(f.Offset),
            ToWidth = WidthOf(t),
        };
    }

    private static FieldOp RemoveOp(SchemaField f)
    {
        var t = f.Type ?? throw new InvalidDataException(
            $"SchemaSnapshotDiff: removed field '{f.Name}' has no type (corrupt schema).");
        return new FieldOp
        {
            Kind = FieldOp.Types.Kind.Remove,
            Field = f.Name,
            FromType = t.Clone(),
            FromOffset = Inv(f.Offset),
            FromWidth = WidthOf(t),
        };
    }

    /// <summary>
    /// Neutral remove/add pairings within a class: a removed field and an added field with the SAME
    /// rendered type at the SAME offset — two hard facts. NO fuzzy offset threshold, NO confidence,
    /// NO rename claim (the raw ADD/REMOVE ops are always ALSO emitted). Greedy 1:1 ordered by
    /// (Ordinal from, Ordinal to) so the result is a pure function of the two snapshots (robust even
    /// if a union ever shares an offset).
    /// </summary>
    private static IEnumerable<PairedEvidence> PairEvidence(
        Dictionary<string, SchemaField> oldFields,
        Dictionary<string, SchemaField> newFields)
    {
        var removed = RemovedKeys(oldFields, newFields).Select(k => oldFields[k]).ToList();
        var added = new HashSet<string>(AddedKeys(oldFields, newFields), StringComparer.Ordinal);
        if (removed.Count == 0 || added.Count == 0)
            yield break;

        var usedAdded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in removed) // removed is already Ordinal-sorted by RemovedKeys
        {
            var rType = SchemaTypeRenderer.Render(r.Type!);
            SchemaField? match = null;
            foreach (var aName in added) // deterministic pick: smallest Ordinal name that matches
            {
                if (usedAdded.Contains(aName))
                    continue;
                var a = newFields[aName];
                if (a.Offset == r.Offset
                    && string.Equals(SchemaTypeRenderer.Render(a.Type!), rType, StringComparison.Ordinal)
                    && (match is null || string.CompareOrdinal(aName, match.Name) < 0))
                {
                    match = a;
                }
            }
            if (match is null)
                continue;
            usedAdded.Add(match.Name);
            var evidence = new PairedEvidence { From = r.Name, To = match.Name };
            evidence.Signals.Add("offsetExact");
            evidence.Signals.Add("typeMatch");
            yield return evidence;
        }
    }

    /// <summary>
    /// The wider, UNSELECTED candidate surface (issue #7): every removed/added pair whose rendered
    /// types are equal OR whose offsets are equal — N:M by design. PairEvidence's greedy 1:1 pick is
    /// sound only because its two-signal bar makes candidates near-unique; under this wider floor
    /// ties are common and any 1:1 selection among them would be an inference, so every qualifying
    /// pair is emitted and carries exactly the signals that hold. "offsetAdjacent" is never emitted:
    /// adjacency needs a distance threshold, which is consumer policy, not a fact. Ordinal by
    /// (from, to).
    /// </summary>
    private static IEnumerable<PairCandidate> PairCandidates(
        Dictionary<string, SchemaField> oldFields,
        Dictionary<string, SchemaField> newFields)
    {
        var removed = RemovedKeys(oldFields, newFields).Select(k => oldFields[k]).ToList();
        var added = AddedKeys(oldFields, newFields).Select(k => newFields[k]).ToList();
        if (removed.Count == 0 || added.Count == 0)
            yield break;

        foreach (var r in removed) // both lists are already Ordinal-sorted -> (from, to) order
        {
            var rType = SchemaTypeRenderer.Render(r.Type!);
            var rWidth = WidthOf(r.Type!);
            foreach (var a in added)
            {
                var typeMatch = string.Equals(
                    SchemaTypeRenderer.Render(a.Type!), rType, StringComparison.Ordinal);
                var offsetExact = a.Offset == r.Offset;
                if (!typeMatch && !offsetExact)
                    continue;

                var candidate = new PairCandidate { From = r.Name, To = a.Name };
                // Signals appended in Ordinal order: offsetExact < sizeMatch < typeMatch.
                if (offsetExact)
                    candidate.Signals.Add("offsetExact");
                var aWidth = WidthOf(a.Type!);
                if (rWidth is not null && aWidth is not null && rWidth.Bytes == aWidth.Bytes)
                    candidate.Signals.Add("sizeMatch");
                if (typeMatch)
                    candidate.Signals.Add("typeMatch");
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// Removed/added CLASS pairs sharing a bare (module-stripped) name — the cross-module move the
    /// qualified key cannot see. N:M, no selection. Signals: "bareNameMatch" (the floor),
    /// "sizeMatch" (class sizes equal), "fieldSetMatch" (Ordinal field-name sets identical).
    /// Ordinal by (from, to).
    /// </summary>
    private static IEnumerable<ClassPairCandidate> ClassPairCandidates(
        Transition transition,
        Dictionary<string, SchemaClass> fromMap,
        Dictionary<string, SchemaClass> toMap)
    {
        var removedByBare = transition.ClassRemoved
            .GroupBy(n => fromMap[n].Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (removedByBare.Count == 0)
            yield break;

        var pairs = new List<ClassPairCandidate>();
        foreach (var addedName in transition.ClassAdded)
        {
            if (!removedByBare.TryGetValue(toMap[addedName].Name, out var removedNames))
                continue;
            foreach (var removedName in removedNames)
            {
                var oldC = fromMap[removedName];
                var newC = toMap[addedName];
                var candidate = new ClassPairCandidate { From = removedName, To = addedName };
                // Signals appended in Ordinal order: bareNameMatch < fieldSetMatch < sizeMatch.
                candidate.Signals.Add("bareNameMatch");
                if (oldC.Fields.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal)
                    .SequenceEqual(
                        newC.Fields.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal),
                        StringComparer.Ordinal))
                {
                    candidate.Signals.Add("fieldSetMatch");
                }
                if (oldC.Size == newC.Size)
                    candidate.Signals.Add("sizeMatch");
                pairs.Add(candidate);
            }
        }

        pairs.Sort(static (a, b) =>
        {
            var byFrom = string.CompareOrdinal(a.From, b.From);
            return byFrom != 0 ? byFrom : string.CompareOrdinal(a.To, b.To);
        });
        foreach (var pair in pairs)
            yield return pair;
    }

    /// <summary>
    /// Cross-class field-move candidates over the pooled removed/added fields of the MATCHED
    /// classes: same field name AND same rendered type, different class (a hoist to a parent, a
    /// push-down, or a sideways move between surviving classes). "parentChainUp"/"parentChainDown"
    /// are emitted when the destination class is an ancestor/descendant of the source class in the
    /// TO snapshot — both classes exist there, so the relation is provable. Ordinal by
    /// (from_class, field, to_class).
    /// </summary>
    private static IEnumerable<FieldMoveCandidate> FieldMoveCandidates(
        List<(string Cls, string Field, string RenderedType)> removedPool,
        List<(string Cls, string Field, string RenderedType)> addedPool,
        Dictionary<string, SchemaClass> toMap)
    {
        if (removedPool.Count == 0 || addedPool.Count == 0)
            yield break;

        var addedByField = addedPool
            .GroupBy(a => a.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var moves = new List<FieldMoveCandidate>();
        foreach (var r in removedPool)
        {
            if (!addedByField.TryGetValue(r.Field, out var adds))
                continue;
            foreach (var a in adds)
            {
                if (string.Equals(a.Cls, r.Cls, StringComparison.Ordinal)
                    || !string.Equals(a.RenderedType, r.RenderedType, StringComparison.Ordinal))
                {
                    continue;
                }

                var move = new FieldMoveCandidate { FromClass = r.Cls, ToClass = a.Cls, Field = r.Field };
                // Signals appended in Ordinal order:
                // fieldNameMatch < parentChainDown < parentChainUp < typeMatch.
                move.Signals.Add("fieldNameMatch");
                if (IsAncestor(toMap, ancestorOf: a.Cls, candidate: r.Cls))
                    move.Signals.Add("parentChainDown");
                if (IsAncestor(toMap, ancestorOf: r.Cls, candidate: a.Cls))
                    move.Signals.Add("parentChainUp");
                move.Signals.Add("typeMatch");
                moves.Add(move);
            }
        }

        moves.Sort(static (a, b) =>
        {
            var byFrom = string.CompareOrdinal(a.FromClass, b.FromClass);
            if (byFrom != 0)
                return byFrom;
            var byField = string.CompareOrdinal(a.Field, b.Field);
            return byField != 0 ? byField : string.CompareOrdinal(a.ToClass, b.ToClass);
        });
        foreach (var move in moves)
            yield return move;
    }

    /// <summary>
    /// Is <paramref name="candidate"/> in the transitive parent chain of <paramref name="ancestorOf"/>
    /// within the snapshot indexed by <paramref name="classes"/>? Cycle-safe; a parent whose class
    /// record is not in the snapshot terminates that branch (nothing is inferred about it).
    /// </summary>
    private static bool IsAncestor(
        Dictionary<string, SchemaClass> classes, string ancestorOf, string candidate)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(ancestorOf);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current) || !classes.TryGetValue(current, out var c))
                continue;
            foreach (var parent in Parents(c))
            {
                if (string.Equals(parent, candidate, StringComparison.Ordinal))
                    return true;
                pending.Push(parent);
            }
        }
        return false;
    }

    // ---- enums ----------------------------------------------------------------------------

    private static void DiffEnums(Schemas.EntitySchema from, Schemas.EntitySchema to, Transition transition)
    {
        var fromMap = IndexBy(from.Enums, e => Qualify(e.Module, e.Name), "enum");
        var toMap = IndexBy(to.Enums, e => Qualify(e.Module, e.Name), "enum");

        foreach (var name in AddedKeys(fromMap, toMap))
            transition.EnumAdded.Add(name);
        foreach (var name in RemovedKeys(fromMap, toMap))
            transition.EnumRemoved.Add(name);

        foreach (var name in MatchedKeys(fromMap, toMap))
        {
            var delta = DiffEnum(name, fromMap[name], toMap[name]);
            if (delta is not null)
                transition.EnumChanged.Add(delta);
        }
    }

    private static EnumDelta? DiffEnum(string name, SchemaEnum oldE, SchemaEnum newE)
    {
        var delta = new EnumDelta { Name = name };

        var oldMembers = MembersByName(oldE);
        var newMembers = MembersByName(newE);
        var ops = new List<EnumMemberOp>();

        foreach (var m in AddedKeys(oldMembers, newMembers))
            ops.Add(new EnumMemberOp { Kind = EnumMemberOp.Types.Kind.AddMember, Member = m, ToValue = Inv(newMembers[m]) });
        foreach (var m in RemovedKeys(oldMembers, newMembers))
            ops.Add(new EnumMemberOp { Kind = EnumMemberOp.Types.Kind.RemoveMember, Member = m, FromValue = Inv(oldMembers[m]) });
        foreach (var m in MatchedKeys(oldMembers, newMembers))
        {
            if (oldMembers[m] != newMembers[m])
            {
                ops.Add(new EnumMemberOp
                {
                    Kind = EnumMemberOp.Types.Kind.ChangeMemberValue,
                    Member = m,
                    FromValue = Inv(oldMembers[m]),
                    ToValue = Inv(newMembers[m]),
                });
            }
        }

        ops.Sort(static (a, b) =>
        {
            var byMember = string.CompareOrdinal(a.Member, b.Member);
            return byMember != 0 ? byMember : ((int)a.Kind).CompareTo((int)b.Kind);
        });
        delta.MemberOps.AddRange(ops);

        if (oldE.Size != newE.Size)
            delta.Resize = new ScalarChange { From = Inv(oldE.Size), To = Inv(newE.Size) };
        if (!string.Equals(oldE.Alignment ?? "", newE.Alignment ?? "", StringComparison.Ordinal))
            delta.Realign = new ScalarChange { From = oldE.Alignment ?? "", To = newE.Alignment ?? "" };
        if (oldE.Flags != newE.Flags)
            delta.Flags = new ScalarChange { From = Inv(oldE.Flags), To = Inv(newE.Flags) };
        if (!string.Equals(oldE.ProjectName, newE.ProjectName, StringComparison.Ordinal))
            delta.ProjectName = new ScalarChange { From = oldE.ProjectName, To = newE.ProjectName };

        var changed = delta.MemberOps.Count > 0
            || delta.Resize is not null
            || delta.Realign is not null
            || delta.Flags is not null
            || delta.ProjectName is not null;
        return changed ? delta : null;
    }

    // ---- helpers --------------------------------------------------------------------------

    internal static BuiltinWidth? WidthOf(SchemaType type)
        => type.Category == SchemaType.Types.Category.Builtin
           && BuiltinBytes.TryGetValue(type.Name ?? "", out var bytes)
            ? new BuiltinWidth { Bytes = bytes }
            : null;

    private static string RenderMeta(IEnumerable<SchemaMetadata> metadata)
        => string.Join(";", metadata
            .Select(m => (m.Name ?? "") + "=" + (m.Value ?? ""))
            .OrderBy(s => s, StringComparer.Ordinal));

    private static List<string> Parents(SchemaClass c)
        => c.Parents.Select(p => Qualify(p.Module, p.Name)).ToList();

    private static Dictionary<string, long> MembersByName(SchemaEnum e)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var m in e.Members)
        {
            if (string.IsNullOrEmpty(m.Name))
                continue;
            map[m.Name] = m.Value; // last-wins; enums are small + well-formed (matches changelog emitter)
        }
        return map;
    }

    private static string Qualify(string? qualifier, string name) => (qualifier ?? "") + "/" + name;

    private static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Inv(ulong v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Inv(uint v) => v.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<string> AddedKeys<T>(
        IReadOnlyDictionary<string, T> from, IReadOnlyDictionary<string, T> to)
        => to.Keys.Where(k => !from.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal);

    private static IEnumerable<string> RemovedKeys<T>(
        IReadOnlyDictionary<string, T> from, IReadOnlyDictionary<string, T> to)
        => from.Keys.Where(k => !to.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal);

    private static IEnumerable<string> MatchedKeys<T>(
        IReadOnlyDictionary<string, T> from, IReadOnlyDictionary<string, T> to)
        => from.Keys.Where(to.ContainsKey).OrderBy(k => k, StringComparer.Ordinal);

    private static Dictionary<string, T> IndexBy<T>(IEnumerable<T> items, Func<T, string> key, string what)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var k = key(item);
            if (string.IsNullOrEmpty(k))
            {
                throw new InvalidDataException(
                    $"SchemaSnapshotDiff: a {what} record has an empty key (corrupt source schema).");
            }
            if (!map.TryAdd(k, item))
            {
                throw new InvalidDataException(
                    $"SchemaSnapshotDiff: duplicate {what} key '{k}' in a source schema " +
                    "(the qualified key must be unique to diff).");
            }
        }
        return map;
    }

    private static InvalidDataException Corrupt(SchemaClass c, SchemaField f, string why)
        => new($"SchemaSnapshotDiff: class '{c.Module}/{c.Name}' field '{f.Name}': {why}.");
}
