// The whole-history roll-up (field_history + enum_history) — FACTS ONLY.
//
// Folded build-by-build in ASCENDING build order. It records, per (class_name, field): the first and
// last build in which the field is present, and its type_history (a new TypeAt is appended only when
// the rendered type differs from the field's last recorded type). It does NOT assemble an alias chain
// — linking a removed field to an added one is inference, and lives downstream.
//
// INCREMENTAL == FULL. The serialized artifact is a lossless snapshot of this accumulator, so a later
// refresh can Rehydrate the exact state a full walk holds at the prior latest build and then Fold only
// the newest snapshot — producing byte-identical output to a full backfill. This holds because:
//   - first_seen is set once (the ascending walk's first sighting) and never lowered;
//   - last_seen advances to the current build whenever the field is present, and is otherwise
//     preserved (a remove-then-readd is recorded purely as min/max presence, no inference);
//   - type_history's last entry always carries the field's CURRENT type, so Rehydrate reconstructs
//     the exact "last rendered type" the full walk held, and the next Fold appends identically.
// The invariant that makes this safe: accumulator state is a subset of what the artifact carries.

using Cs2SchemaTracker.Schemas;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.Evolution;

/// <summary>
/// Running, fact-only field/enum history roll-up. Fold snapshots in ascending build order (or seed +
/// fold), then emit <see cref="FieldHistory"/> / <see cref="EnumHistory"/>. Rehydrate from a prior
/// artifact to continue an incremental refresh. See file header.
/// </summary>
public sealed class FieldHistoryAccumulator
{
    private sealed class FieldState
    {
        public required string FirstSeen;
        public required string LastSeen;
        public required string LastRendered;
        public readonly List<TypeAt> TypeHistory = new();
    }

    private sealed class EnumState
    {
        public required string FirstSeen;
        public required string LastSeen;
    }

    // Keyed by (class_name "<module>/<name>", field name); tuple equality is Ordinal for strings.
    private readonly Dictionary<(string ClassName, string Field), FieldState> _fields = new();
    private readonly Dictionary<string, EnumState> _enums = new(StringComparer.Ordinal);

    /// <summary>Create an accumulator seeded with the floor snapshot at <paramref name="build"/>.</summary>
    public static FieldHistoryAccumulator Seed(Schemas.EntitySchema floor, string build)
    {
        var acc = new FieldHistoryAccumulator();
        acc.Fold(floor, build);
        return acc;
    }

    /// <summary>Reconstruct the accumulator state carried by a prior cumulative artifact.</summary>
    public static FieldHistoryAccumulator Rehydrate(
        IEnumerable<FieldHistory> fieldHistory, IEnumerable<EnumHistory> enumHistory)
    {
        ArgumentNullException.ThrowIfNull(fieldHistory);
        ArgumentNullException.ThrowIfNull(enumHistory);

        var acc = new FieldHistoryAccumulator();
        foreach (var fh in fieldHistory)
        {
            if (fh.TypeHistory.Count == 0)
            {
                throw new InvalidDataException(
                    $"FieldHistoryAccumulator: field_history '{fh.ClassName}::{fh.Field}' has an empty " +
                    "type_history (corrupt cumulative artifact).");
            }
            var state = new FieldState
            {
                FirstSeen = fh.FirstSeenBuild,
                LastSeen = fh.LastSeenBuild,
                LastRendered = SchemaTypeRenderer.Render(fh.TypeHistory[^1].Type),
            };
            foreach (var ta in fh.TypeHistory)
                state.TypeHistory.Add(new TypeAt { Build = ta.Build, Type = ta.Type.Clone() });
            _AddField(acc, fh.ClassName, fh.Field, state);
        }
        foreach (var eh in enumHistory)
        {
            if (!acc._enums.TryAdd(eh.EnumName,
                    new EnumState { FirstSeen = eh.FirstSeenBuild, LastSeen = eh.LastSeenBuild }))
            {
                throw new InvalidDataException(
                    $"FieldHistoryAccumulator: duplicate enum_history '{eh.EnumName}' (corrupt artifact).");
            }
        }
        return acc;
    }

    private static void _AddField(FieldHistoryAccumulator acc, string className, string field, FieldState state)
    {
        if (!acc._fields.TryAdd((className, field), state))
        {
            throw new InvalidDataException(
                $"FieldHistoryAccumulator: duplicate field_history '{className}::{field}' (corrupt artifact).");
        }
    }

    /// <summary>
    /// Fold one snapshot at <paramref name="build"/> into the roll-up. MUST be called in ascending
    /// build order (Seed handles the floor; each later build is folded once).
    /// </summary>
    public void Fold(Schemas.EntitySchema snapshot, string build)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(build);

        foreach (var c in snapshot.Classes)
        {
            var className = (c.Module ?? "") + "/" + c.Name;
            foreach (var f in c.Fields)
            {
                var type = f.Type ?? throw new InvalidDataException(
                    $"FieldHistoryAccumulator: class '{className}' field '{f.Name}' has no type (corrupt schema).");
                var rendered = SchemaTypeRenderer.Render(type);
                if (_fields.TryGetValue((className, f.Name), out var state))
                {
                    state.LastSeen = build;
                    if (!string.Equals(state.LastRendered, rendered, StringComparison.Ordinal))
                    {
                        state.TypeHistory.Add(new TypeAt { Build = build, Type = type.Clone() });
                        state.LastRendered = rendered;
                    }
                }
                else
                {
                    var fresh = new FieldState { FirstSeen = build, LastSeen = build, LastRendered = rendered };
                    fresh.TypeHistory.Add(new TypeAt { Build = build, Type = type.Clone() });
                    _fields[(className, f.Name)] = fresh;
                }
            }
        }

        foreach (var e in snapshot.Enums)
        {
            var enumName = (e.Module ?? "") + "/" + e.Name;
            if (_enums.TryGetValue(enumName, out var es))
                es.LastSeen = build;
            else
                _enums[enumName] = new EnumState { FirstSeen = build, LastSeen = build };
        }
    }

    /// <summary>Emit the field history, Ordinal-sorted by (class_name, field).</summary>
    public IEnumerable<FieldHistory> ToFieldHistory()
    {
        foreach (var kv in _fields
            .OrderBy(kv => kv.Key.ClassName, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Field, StringComparer.Ordinal))
        {
            var fh = new FieldHistory
            {
                ClassName = kv.Key.ClassName,
                Field = kv.Key.Field,
                FirstSeenBuild = kv.Value.FirstSeen,
                LastSeenBuild = kv.Value.LastSeen,
            };
            foreach (var ta in kv.Value.TypeHistory)
                fh.TypeHistory.Add(new TypeAt { Build = ta.Build, Type = ta.Type.Clone() });
            yield return fh;
        }
    }

    /// <summary>Emit the enum history, Ordinal-sorted by enum_name.</summary>
    public IEnumerable<EnumHistory> ToEnumHistory()
    {
        foreach (var kv in _enums.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return new EnumHistory
            {
                EnumName = kv.Key,
                FirstSeenBuild = kv.Value.FirstSeen,
                LastSeenBuild = kv.Value.LastSeen,
            };
        }
    }
}
