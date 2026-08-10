// Prune a descriptor down to the closure its dependents actually reference, and drop the imports
// that closure no longer needs.
//
// WHY THIS EXISTS: cstrike15_usermessages.proto references six types out of
// cstrike15_gcmessages.proto, and cstrike15_gcmessages in turn imports steammessages,
// engine_gcmessages and gcsdk_gcmessages — Steam matchmaking, inventory and item-schema traffic,
// none of it on the demo wire path. Carrying the file whole drags all three imports into every
// consumer that compiles the artifact set. Measured downstream by DemoViewer.NET against the
// GameTracking tree: 231,377 -> 157,465 lines of generated C# (-32%), with the generated code for
// every other compiled file BYTE-IDENTICAL before and after. See CS2OpenDev-SchemaTracker#3.
//
// WHY IT IS DERIVED RATHER THAN A COMMITTED STUB: a hand-maintained stub is silently restored by
// the next refresh — the copy overwrites it, all the removed types come back, and the build still
// succeeds, so nothing reports the regression. Deriving it on every run makes that failure mode
// impossible: a new root, or a new field on a kept type, is picked up automatically.
//
// SAFETY PROPERTIES:
//   - Roots are collected from EVERY other file in the set, not from a hardcoded importer list, so
//     a second consumer of the pruned file cannot be broken by a prune it did not know about.
//   - If no other file references the target at all, the file is left UNCHANGED. That is the
//     conservative branch: an empty result is far more likely to mean reference detection failed
//     than to mean the file is genuinely dead.
//   - Only top-level declarations are removed. Nested types travel with their parent, so a kept
//     type is always structurally complete.
//   - Declared order is preserved for everything kept, because the emitter treats FDP-declared
//     order as canonical.

using Google.Protobuf.Reflection;

namespace Cs2SchemaTracker.Host.ProtoDescriptors;

internal static class DependencyClosurePruner
{
    /// <summary>google's well-known options file; kept whenever it was already a dependency.</summary>
    private const string DescriptorProto = "google/protobuf/descriptor.proto";

    /// <summary>What a prune did, for logging. Never thrown away silently.</summary>
    internal sealed record PruneOutcome(
        string File,
        int KeptTypes,
        int RemovedTypes,
        IReadOnlyList<string> DroppedDependencies);

    /// <summary>
    /// Replace <paramref name="targetFileName"/> in <paramref name="files"/> with a copy carrying
    /// only the top-level types reachable from other files in the set, and only the dependencies
    /// that copy still needs.
    /// </summary>
    /// <returns>
    /// The outcome, or <c>null</c> when nothing changed — target absent, target not referenced by
    /// anything (see SAFETY above), or the closure already covers every declared type.
    /// </returns>
    public static PruneOutcome? Prune(IList<FileDescriptorProto> files, string targetFileName)
    {
        ArgumentNullException.ThrowIfNull(files);

        var targetIndex = -1;
        for (var i = 0; i < files.Count; i++)
        {
            if (string.Equals(files[i].Name, targetFileName, StringComparison.Ordinal))
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex < 0)
        {
            return null;
        }

        var target = files[targetIndex];

        // 1. Index every fully-qualified name the target declares (nested included) against the
        //    TOP-LEVEL declaration it belongs to. Keeping is a top-level decision: a nested type
        //    cannot be kept without its parent.
        var declaredToTopLevel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in target.MessageType)
        {
            IndexMessage(target.Package, message, message.Name, declaredToTopLevel);
        }

        foreach (var enumType in target.EnumType)
        {
            declaredToTopLevel[Qualify(target.Package, enumType.Name)] = enumType.Name;
        }

        if (declaredToTopLevel.Count == 0)
        {
            return null;
        }

        // 2. Roots — every target-declared type named by some OTHER file in the set.
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();

        foreach (var file in files)
        {
            if (ReferenceEquals(file, target))
            {
                continue;
            }

            foreach (var reference in TypeReferences(file))
            {
                if (declaredToTopLevel.TryGetValue(reference, out var topLevel) && keep.Add(topLevel))
                {
                    pending.Enqueue(topLevel);
                }
            }
        }

        // Nothing points at this file. Leave it alone rather than emptying it — see SAFETY.
        if (keep.Count == 0)
        {
            return null;
        }

        // 3. Transitive closure inside the target. Enums have no outgoing type references, so only
        //    messages need walking.
        var messagesByName = new Dictionary<string, DescriptorProto>(StringComparer.Ordinal);
        foreach (var message in target.MessageType)
        {
            messagesByName[message.Name] = message;
        }

        while (pending.Count > 0)
        {
            if (!messagesByName.TryGetValue(pending.Dequeue(), out var message))
            {
                continue;
            }

            foreach (var reference in MessageTypeReferences(message))
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
            // Everything survives; emitting a rebuilt-but-identical FDP would only risk churn.
            return null;
        }

        // 4. Rebuild, preserving declared order.
        var pruned = target.Clone();
        pruned.MessageType.Clear();
        pruned.EnumType.Clear();

        foreach (var message in target.MessageType)
        {
            if (keep.Contains(message.Name))
            {
                pruned.MessageType.Add(message);
            }
        }

        foreach (var enumType in target.EnumType)
        {
            if (keep.Contains(enumType.Name))
            {
                pruned.EnumType.Add(enumType);
            }
        }

        // 5. Recompute dependencies against what actually survived. A dependency is dropped only
        //    when no surviving reference resolves into it — an unused import is a protoc warning,
        //    but a missing one is a compile error, so this errs toward keeping.
        var declaringFile = BuildTypeOwnershipMap(files);
        var stillNeeded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in TypeReferences(pruned))
        {
            if (declaringFile.TryGetValue(reference, out var owner)
                && !string.Equals(owner, target.Name, StringComparison.Ordinal))
            {
                stillNeeded.Add(owner);
            }
        }

        var dropped = new List<string>();
        var keptDependencies = new List<string>();
        var oldToNewIndex = new Dictionary<int, int>();

        for (var i = 0; i < target.Dependency.Count; i++)
        {
            var dependency = target.Dependency[i];

            // descriptor.proto backs custom options, which are carried on the options messages
            // rather than as type references, so reference-scanning cannot see the need for it.
            var required = stillNeeded.Contains(dependency)
                || string.Equals(dependency, DescriptorProto, StringComparison.Ordinal);

            if (required)
            {
                oldToNewIndex[i] = keptDependencies.Count;
                keptDependencies.Add(dependency);
            }
            else
            {
                dropped.Add(dependency);
            }
        }

        pruned.Dependency.Clear();
        pruned.Dependency.AddRange(keptDependencies);

        // public_dependency / weak_dependency are INDICES into dependency, so they have to be
        // remapped rather than copied. CS2's protos use neither today; handled so that a future
        // one cannot corrupt the output silently.
        RemapDependencyIndices(target.PublicDependency, pruned.PublicDependency, oldToNewIndex);
        RemapDependencyIndices(target.WeakDependency, pruned.WeakDependency, oldToNewIndex);

        files[targetIndex] = pruned;

        return new PruneOutcome(
            target.Name,
            keep.Count,
            totalTopLevel - keep.Count,
            dropped);
    }

    private static void RemapDependencyIndices(
        Google.Protobuf.Collections.RepeatedField<int> source,
        Google.Protobuf.Collections.RepeatedField<int> destination,
        Dictionary<int, int> map)
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

    /// <summary>Map every fully-qualified type name in the set to the file that declares it.</summary>
    private static Dictionary<string, string> BuildTypeOwnershipMap(IEnumerable<FileDescriptorProto> files)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var declared = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in file.MessageType)
            {
                IndexMessage(file.Package, message, message.Name, declared);
            }

            foreach (var enumType in file.EnumType)
            {
                declared[Qualify(file.Package, enumType.Name)] = enumType.Name;
            }

            foreach (var name in declared.Keys)
            {
                // First declarer wins. Duplicate symbols across files are a real property of CS2's
                // dump (the collision domains), and picking deterministically beats throwing here —
                // this map only decides which import to keep.
                owners.TryAdd(name, file.Name);
            }
        }

        return owners;
    }

    private static void IndexMessage(
        string package, DescriptorProto message, string path, IDictionary<string, string> into)
    {
        var topLevel = path.Split('.')[0];
        into[Qualify(package, path)] = topLevel;

        foreach (var nested in message.NestedType)
        {
            IndexMessage(package, nested, path + "." + nested.Name, into);
        }

        foreach (var nestedEnum in message.EnumType)
        {
            into[Qualify(package, path + "." + nestedEnum.Name)] = topLevel;
        }
    }

    private static string Qualify(string package, string path) =>
        string.IsNullOrEmpty(package) ? "." + path : "." + package + "." + path;

    /// <summary>Every type name referenced anywhere in a file, fully qualified with a leading dot.</summary>
    private static IEnumerable<string> TypeReferences(FileDescriptorProto file)
    {
        foreach (var message in file.MessageType)
        {
            foreach (var reference in MessageTypeReferences(message))
            {
                yield return reference;
            }
        }

        foreach (var extension in file.Extension)
        {
            foreach (var reference in FieldTypeReferences(extension))
            {
                yield return reference;
            }
        }

        foreach (var service in file.Service)
        {
            foreach (var method in service.Method)
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
    }

    private static IEnumerable<string> MessageTypeReferences(DescriptorProto message)
    {
        foreach (var field in message.Field)
        {
            foreach (var reference in FieldTypeReferences(field))
            {
                yield return reference;
            }
        }

        foreach (var extension in message.Extension)
        {
            foreach (var reference in FieldTypeReferences(extension))
            {
                yield return reference;
            }
        }

        foreach (var nested in message.NestedType)
        {
            foreach (var reference in MessageTypeReferences(nested))
            {
                yield return reference;
            }
        }
    }

    private static IEnumerable<string> FieldTypeReferences(FieldDescriptorProto field)
    {
        // Set for TYPE_MESSAGE / TYPE_ENUM / TYPE_GROUP; empty for scalars.
        if (!string.IsNullOrEmpty(field.TypeName))
        {
            yield return field.TypeName;
        }

        // An extension declared here targets a type that may live in another file.
        if (!string.IsNullOrEmpty(field.Extendee))
        {
            yield return field.Extendee;
        }
    }
}
