// Reduce one descriptor in a set to the closure the rest of the set actually references,
// and drop the imports the surviving closure no longer needs.
//
// The one production target is cstrike15_gcmessages.proto: cstrike15_usermessages references
// six of its 160+ top-level types, and the file's imports (steammessages, engine_gcmessages,
// gcsdk_gcmessages) exist only to satisfy the types nothing in the set uses — Steam
// matchmaking/inventory/item-schema traffic with no demo-wire relevance. The referenced
// closure is 17 types and needs none of the three imports, so every consumer compiling the
// set carries ~74k lines of generated code for nothing. See issue #3.
//
// The prune is DERIVED on every extract, never a committed stub. A hand-maintained stub
// dies silently: the next upstream refresh overwrites it, the full file comes back, and the
// build still succeeds, so nothing reports the regression. Deriving per run also means a new
// referencing field anywhere in the set re-grows the closure automatically.
//
// Conservative by construction:
//   - Roots come from every other file in the set, never from a hardcoded importer list, so
//     a consumer this code has never heard of still protects the types it references.
//   - A target nothing references is returned unchanged (null outcome). An empty closure
//     almost certainly means reference detection broke, not that the file is dead.
//   - Pruning is a top-level decision only; nested types always travel with their parent.
//   - Declared order is preserved — the text emitter treats FDP order as canonical.
//   - An import is dropped only when no surviving reference resolves into it; an unused
//     import is a protoc warning, a missing one is a compile error.
//   - google/protobuf/descriptor.proto survives on name: custom options hang off the options
//     messages, not off type references, so no reference scan can prove it necessary.
//   - public_dependency / weak_dependency are index lists into dependency and are remapped,
//     not copied — CS2 uses neither today, but a stale index would repoint silently.

using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace Cs2SchemaTracker.Host.ProtoDescriptors;

internal static class ReferencedClosurePruner
{
    private const string DescriptorProtoName = "google/protobuf/descriptor.proto";

    /// <summary>
    /// The pruned copy plus the numbers the caller logs. <see cref="Pruned"/> is a fresh
    /// clone; the input set is never mutated.
    /// </summary>
    internal sealed record Outcome(
        FileDescriptorProto Pruned,
        int KeptTopLevel,
        int RemovedTopLevel,
        IReadOnlyList<string> DroppedImports);

    /// <summary>
    /// Compute the referenced-closure prune of <paramref name="targetName"/> within
    /// <paramref name="set"/>. Returns null when there is nothing to do: target absent,
    /// target referenced by nothing (conservative no-op), or every top-level declaration
    /// already in the closure.
    /// </summary>
    public static Outcome? TryPrune(IReadOnlyList<FileDescriptorProto> set, string targetName)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrEmpty(targetName);

        var target = set.FirstOrDefault(f => string.Equals(f.Name, targetName, StringComparison.Ordinal));
        if (target is null)
        {
            return null;
        }

        // Every fully-qualified name the target declares (nested types and enums included),
        // mapped to the TOP-LEVEL declaration that owns it. Keep/remove is decided per
        // top-level declaration; a nested type cannot outlive its parent.
        var declaredToTopLevel = new Dictionary<string, string>(StringComparer.Ordinal);
        IndexDeclarations(target, (fqn, topLevel) => declaredToTopLevel[fqn] = topLevel);
        if (declaredToTopLevel.Count == 0)
        {
            return null;
        }

        // Roots: every target-declared type some OTHER file references.
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        foreach (var file in set)
        {
            if (ReferenceEquals(file, target))
            {
                continue;
            }
            foreach (var reference in FileReferences(file))
            {
                if (declaredToTopLevel.TryGetValue(reference, out var topLevel) && keep.Add(topLevel))
                {
                    pending.Enqueue(topLevel);
                }
            }
        }

        if (keep.Count == 0)
        {
            // Unreferenced target: leave it whole rather than emptying it — see the header.
            return null;
        }

        // Transitive closure within the target. Only messages carry outgoing references.
        var topLevelMessages = target.MessageType.ToDictionary(m => m.Name, StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            if (!topLevelMessages.TryGetValue(pending.Dequeue(), out var message))
            {
                continue; // an enum, or (defensively) a name the target no longer declares
            }
            foreach (var reference in MessageReferences(message))
            {
                if (declaredToTopLevel.TryGetValue(reference, out var topLevel) && keep.Add(topLevel))
                {
                    pending.Enqueue(topLevel);
                }
            }
        }

        var totalTopLevel = target.MessageType.Count + target.EnumType.Count;
        if (keep.Count >= totalTopLevel)
        {
            return null; // full coverage: rebuilding an identical FDP is pure churn
        }

        var pruned = target.Clone();
        pruned.MessageType.Clear();
        pruned.EnumType.Clear();
        foreach (var message in target.MessageType.Where(m => keep.Contains(m.Name)))
        {
            pruned.MessageType.Add(message.Clone());
        }
        foreach (var enumType in target.EnumType.Where(e => keep.Contains(e.Name)))
        {
            pruned.EnumType.Add(enumType.Clone());
        }

        // Re-derive the import list from what survived. Ownership of every fully-qualified
        // name in the set decides which files the surviving references still resolve into;
        // duplicate declarations across files (a real property of CS2's collision domains)
        // resolve first-declarer-wins, which is deterministic because the set order is.
        var owningFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in set)
        {
            IndexDeclarations(file, (fqn, _) => owningFile.TryAdd(fqn, file.Name));
        }

        var neededImports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in FileReferences(pruned))
        {
            if (owningFile.TryGetValue(reference, out var owner)
                && !string.Equals(owner, target.Name, StringComparison.Ordinal))
            {
                neededImports.Add(owner);
            }
        }

        var dropped = new List<string>();
        var indexMap = new Dictionary<int, int>();
        pruned.Dependency.Clear();
        for (var i = 0; i < target.Dependency.Count; i++)
        {
            var dependency = target.Dependency[i];
            if (neededImports.Contains(dependency)
                || string.Equals(dependency, DescriptorProtoName, StringComparison.Ordinal))
            {
                indexMap[i] = pruned.Dependency.Count;
                pruned.Dependency.Add(dependency);
            }
            else
            {
                dropped.Add(dependency);
            }
        }
        RemapIndices(target.PublicDependency, pruned.PublicDependency, indexMap);
        RemapIndices(target.WeakDependency, pruned.WeakDependency, indexMap);

        return new Outcome(pruned, keep.Count, totalTopLevel - keep.Count, dropped);
    }

    private static void RemapIndices(
        RepeatedField<int> source, RepeatedField<int> destination, Dictionary<int, int> map)
    {
        destination.Clear();
        foreach (var oldIndex in source)
        {
            if (map.TryGetValue(oldIndex, out var newIndex))
            {
                destination.Add(newIndex);
            }
        }
    }

    /// <summary>
    /// Report every declared fully-qualified type name in <paramref name="file"/> (top-level
    /// and nested messages, top-level and nested enums) with the top-level declaration name
    /// that owns it.
    /// </summary>
    private static void IndexDeclarations(FileDescriptorProto file, Action<string, string> report)
    {
        foreach (var message in file.MessageType)
        {
            IndexMessage(file.Package, message, message.Name, message.Name, report);
        }
        foreach (var enumType in file.EnumType)
        {
            report(Qualify(file.Package, enumType.Name), enumType.Name);
        }
    }

    private static void IndexMessage(
        string package, DescriptorProto message, string path, string topLevel, Action<string, string> report)
    {
        report(Qualify(package, path), topLevel);
        foreach (var nested in message.NestedType)
        {
            IndexMessage(package, nested, path + "." + nested.Name, topLevel, report);
        }
        foreach (var nestedEnum in message.EnumType)
        {
            report(Qualify(package, path + "." + nestedEnum.Name), topLevel);
        }
    }

    private static string Qualify(string package, string path) =>
        string.IsNullOrEmpty(package) ? "." + path : "." + package + "." + path;

    /// <summary>
    /// Every fully-qualified type reference a file makes: field and extension types,
    /// extension targets, and service method signatures, through all nesting.
    /// </summary>
    private static IEnumerable<string> FileReferences(FileDescriptorProto file)
    {
        foreach (var message in file.MessageType)
        {
            foreach (var reference in MessageReferences(message))
            {
                yield return reference;
            }
        }
        foreach (var extension in file.Extension)
        {
            foreach (var reference in FieldReferences(extension))
            {
                yield return reference;
            }
        }
        foreach (var method in file.Service.SelectMany(s => s.Method))
        {
            if (!string.IsNullOrEmpty(method.InputType))
            {
                yield return method.InputType;
            }
            if (!string.IsNullOrEmpty(method.OutputType))
            {
                yield return method.OutputType;
            }
        }
    }

    private static IEnumerable<string> MessageReferences(DescriptorProto message)
    {
        foreach (var field in message.Field.Concat(message.Extension))
        {
            foreach (var reference in FieldReferences(field))
            {
                yield return reference;
            }
        }
        foreach (var nested in message.NestedType)
        {
            foreach (var reference in MessageReferences(nested))
            {
                yield return reference;
            }
        }
    }

    private static IEnumerable<string> FieldReferences(FieldDescriptorProto field)
    {
        // type_name is set for message/enum/group fields, empty for scalars.
        if (!string.IsNullOrEmpty(field.TypeName))
        {
            yield return field.TypeName;
        }
        // An extension field's target type may live in another file.
        if (!string.IsNullOrEmpty(field.Extendee))
        {
            yield return field.Extendee;
        }
    }
}
