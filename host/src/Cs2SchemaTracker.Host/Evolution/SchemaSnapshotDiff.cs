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

        foreach (var name in MatchedKeys(fromMap, toMap))
        {
            var delta = DiffClass(name, fromMap[name], toMap[name]);
            if (delta is not null)
                transition.ClassChanged.Add(delta);
        }
    }

    private static ClassDelta? DiffClass(string name, SchemaClass oldC, SchemaClass newC)
    {
        var delta = new ClassDelta { Name = name };

        var oldFields = IndexBy(oldC.Fields, f => f.Name, "field");
        var newFields = IndexBy(newC.Fields, f => f.Name, "field");

        var ops = new List<FieldOp>();

        // Added / removed fields.
        foreach (var fname in AddedKeys(oldFields, newFields))
            ops.Add(AddOp(newFields[fname]));
        foreach (var fname in RemovedKeys(oldFields, newFields))
            ops.Add(RemoveOp(oldFields[fname]));

        // Matched fields: type / offset / metadata are INDEPENDENT ops (a field may emit several).
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

            var oldMeta = RenderMeta(of);
            var newMeta = RenderMeta(nf);
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
        delta.FieldOps.AddRange(ops);

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

        // Neutral paired evidence over the removed/added field sets — NOT a rename claim.
        foreach (var pair in PairEvidence(oldFields, newFields))
            delta.PairedEvidence.Add(pair);

        var changed = delta.FieldOps.Count > 0
            || delta.Reparent is not null
            || delta.Resize is not null
            || delta.Realign is not null
            || delta.Flags is not null
            || delta.PairedEvidence.Count > 0;
        return changed ? delta : null;
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

        var changed = delta.MemberOps.Count > 0
            || delta.Resize is not null
            || delta.Realign is not null
            || delta.Flags is not null;
        return changed ? delta : null;
    }

    // ---- helpers --------------------------------------------------------------------------

    internal static BuiltinWidth? WidthOf(SchemaType type)
        => type.Category == SchemaType.Types.Category.Builtin
           && BuiltinBytes.TryGetValue(type.Name ?? "", out var bytes)
            ? new BuiltinWidth { Bytes = bytes }
            : null;

    private static string RenderMeta(SchemaField f)
        => string.Join(";", f.Metadata
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
