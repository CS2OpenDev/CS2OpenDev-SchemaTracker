// closure-prune tests — root discovery, transitive reach, import dropping, and the conservative
// branches that must NOT prune.

using Cs2SchemaTracker.Host.ProtoDescriptors;

using Google.Protobuf.Reflection;

using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace Cs2SchemaTracker.Tests.ProtoDescriptors;

public class DependencyClosurePrunerTest
{
    private const string Target = "target.proto";

    /// <summary>A message with one message-typed field pointing at <paramref name="referenceTo"/>.</summary>
    private static DescriptorProto Message(string name, string? referenceTo = null)
    {
        var message = new DescriptorProto { Name = name };
        if (referenceTo is not null)
        {
            message.Field.Add(new FieldDescriptorProto
            {
                Name = "ref",
                Number = 1,
                Type = Type.Message,
                Label = Label.Optional,
                TypeName = referenceTo,
                JsonName = "ref",
            });
        }

        return message;
    }

    /// <summary>
    /// target.proto declares Kept / Reached / Orphan (+ an unreferenced enum) and imports two files.
    /// Only Kept is referenced from outside; Kept -> Reached; Orphan holds the only reference into
    /// dead.proto, so pruning Orphan must drop that import and keep live.proto.
    /// </summary>
    private static List<FileDescriptorProto> BuildSet()
    {
        var target = new FileDescriptorProto
        {
            Name = Target,
            Syntax = "proto3",
            Dependency = { "live.proto", "dead.proto" },
            MessageType =
            {
                Message("Kept", ".Reached"),
                Message("Reached", ".LiveType"),
                Message("Orphan", ".DeadType"),
            },
            EnumType = { new EnumDescriptorProto { Name = "UnusedEnum" } },
        };

        var consumer = new FileDescriptorProto
        {
            Name = "consumer.proto",
            Syntax = "proto3",
            Dependency = { Target },
            MessageType = { Message("Uses", ".Kept") },
        };

        var live = new FileDescriptorProto
        {
            Name = "live.proto",
            Syntax = "proto3",
            MessageType = { Message("LiveType") },
        };
        var dead = new FileDescriptorProto
        {
            Name = "dead.proto",
            Syntax = "proto3",
            MessageType = { Message("DeadType") },
        };

        return [target, consumer, live, dead];
    }

    private static FileDescriptorProto Find(IEnumerable<FileDescriptorProto> files, string name) =>
        files.Single(f => f.Name == name);

    [Xunit.Fact]
    public void KeepsReferencedTypesAndTheirTransitiveClosure()
    {
        var files = BuildSet();

        var outcome = DependencyClosurePruner.Prune(files, Target);

        Xunit.Assert.NotNull(outcome);
        var pruned = Find(files, Target);
        Xunit.Assert.Equal(["Kept", "Reached"], pruned.MessageType.Select(m => m.Name));
    }

    [Xunit.Fact]
    public void RemovesUnreferencedTypesIncludingEnums()
    {
        var files = BuildSet();

        var outcome = DependencyClosurePruner.Prune(files, Target);

        Xunit.Assert.NotNull(outcome);
        // Orphan + UnusedEnum
        Xunit.Assert.Equal(2, outcome.RemovedTypes);
        Xunit.Assert.Equal(2, outcome.KeptTypes);
        Xunit.Assert.Empty(Find(files, Target).EnumType);
    }

    [Xunit.Fact]
    public void DropsImportsTheSurvivingClosureNoLongerNeeds()
    {
        var files = BuildSet();

        var outcome = DependencyClosurePruner.Prune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Equal(["dead.proto"], outcome.DroppedDependencies);
        Xunit.Assert.Equal(["live.proto"], Find(files, Target).Dependency);
    }

    [Xunit.Fact]
    public void PreservesDeclaredOrder()
    {
        var files = BuildSet();
        // Declare Reached BEFORE Kept; the emitter treats declared order as canonical, so the
        // prune must not reorder into discovery order.
        var target = Find(files, Target);
        var kept = target.MessageType[0];
        var reached = target.MessageType[1];
        target.MessageType.Clear();
        target.MessageType.Add(reached);
        target.MessageType.Add(kept);

        DependencyClosurePruner.Prune(files, Target);

        Xunit.Assert.Equal(["Reached", "Kept"], Find(files, Target).MessageType.Select(m => m.Name));
    }

    [Xunit.Fact]
    public void KeepsNestedTypesWithTheirParent()
    {
        var files = BuildSet();
        var target = Find(files, Target);
        var outer = Message("Outer");
        outer.NestedType.Add(Message("Inner"));
        target.MessageType.Add(outer);
        // Reference the NESTED type from outside; the top-level parent must survive whole.
        Find(files, "consumer.proto").MessageType.Add(Message("UsesNested", ".Outer.Inner"));

        DependencyClosurePruner.Prune(files, Target);

        var kept = Find(files, Target).MessageType.Single(m => m.Name == "Outer");
        Xunit.Assert.Equal(["Inner"], kept.NestedType.Select(n => n.Name));
    }

    [Xunit.Fact]
    public void LeavesFileUntouchedWhenNothingReferencesIt()
    {
        var files = BuildSet();
        // Drop the only consumer. An unreferenced target must NOT be emptied — that is far more
        // likely to mean reference detection broke than that the file is genuinely dead.
        files.Remove(Find(files, "consumer.proto"));

        var outcome = DependencyClosurePruner.Prune(files, Target);

        Xunit.Assert.Null(outcome);
        Xunit.Assert.Equal(3, Find(files, Target).MessageType.Count);
    }

    [Xunit.Fact]
    public void ReturnsNullWhenTargetIsAbsent()
    {
        var files = BuildSet();

        Xunit.Assert.Null(DependencyClosurePruner.Prune(files, "not-in-the-set.proto"));
    }

    [Xunit.Fact]
    public void ReturnsNullWhenEveryTypeIsAlreadyReferenced()
    {
        var files = BuildSet();
        var target = Find(files, Target);
        target.EnumType.Clear();
        target.MessageType.Remove(target.MessageType.Single(m => m.Name == "Orphan"));

        Xunit.Assert.Null(DependencyClosurePruner.Prune(files, Target));
    }

    [Xunit.Fact]
    public void CollectsRootsFromEveryFileNotJustTheKnownImporter()
    {
        var files = BuildSet();
        // A second consumer reaches Orphan. Pruning on the first consumer alone would delete it.
        files.Add(new FileDescriptorProto
        {
            Name = "other-consumer.proto",
            Syntax = "proto3",
            Dependency = { Target },
            MessageType = { Message("AlsoUses", ".Orphan") },
        });

        DependencyClosurePruner.Prune(files, Target);

        var names = Find(files, Target).MessageType.Select(m => m.Name).ToList();
        Xunit.Assert.Contains("Orphan", names);
        Xunit.Assert.Contains("dead.proto", Find(files, Target).Dependency);
    }

    [Xunit.Fact]
    public void KeepsDescriptorProtoImportEvenWhenNoTypeReferencesIt()
    {
        var files = BuildSet();
        // Custom options hang off the options messages, not off type references, so a scan cannot
        // see the need for descriptor.proto. It must survive on name.
        var target = Find(files, Target);
        target.Dependency.Add("google/protobuf/descriptor.proto");

        var outcome = DependencyClosurePruner.Prune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Contains("google/protobuf/descriptor.proto", Find(files, Target).Dependency);
        Xunit.Assert.DoesNotContain("google/protobuf/descriptor.proto", outcome.DroppedDependencies);
    }

    [Xunit.Fact]
    public void RemapsPublicDependencyIndicesAcrossDroppedImports()
    {
        var files = BuildSet();
        var target = Find(files, Target);
        // dependency = [live.proto, dead.proto]; mark BOTH public. dead.proto is dropped, so the
        // surviving index must be rewritten from 0 to 0 and the entry for index 1 removed —
        // a stale index would silently re-point at another import.
        target.PublicDependency.Add(0);
        target.PublicDependency.Add(1);

        DependencyClosurePruner.Prune(files, Target);

        var pruned = Find(files, Target);
        Xunit.Assert.Equal(["live.proto"], pruned.Dependency);
        Xunit.Assert.Equal([0], pruned.PublicDependency);
    }
}
