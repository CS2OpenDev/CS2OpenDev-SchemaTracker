// referenced-closure prune tests — root discovery across the set, transitive reach, import
// dropping and index remapping, and every conservative branch that must NOT prune.

using Cs2SchemaTracker.Host.ProtoDescriptors;

using Google.Protobuf;
using Google.Protobuf.Reflection;

using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace Cs2SchemaTracker.Tests.ProtoDescriptors;

public class ReferencedClosurePrunerTest
{
    private const string Target = "target.proto";

    /// <summary>A message optionally carrying one message-typed field referencing <paramref name="referenceTo"/>.</summary>
    private static DescriptorProto Message(string name, string? referenceTo = null)
    {
        var message = new DescriptorProto { Name = name };
        if (referenceTo is not null)
        {
            message.Field.Add(new FieldDescriptorProto
            {
                Name = "r",
                Number = 1,
                Type = Type.Message,
                Label = Label.Optional,
                TypeName = referenceTo,
                JsonName = "r",
            });
        }
        return message;
    }

    /// <summary>
    /// target.proto declares Kept / Reached / Orphan / UnusedEnum and imports live.proto and
    /// dead.proto. Only Kept is referenced from outside; Kept -> Reached -> LiveType (live.proto);
    /// Orphan holds the only reference into dead.proto. The correct prune keeps Kept + Reached,
    /// removes Orphan + UnusedEnum, keeps the live.proto import, and drops dead.proto.
    /// </summary>
    private static List<FileDescriptorProto> BuildSet()
    {
        var target = new FileDescriptorProto
        {
            Name = Target,
            Syntax = "proto2",
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
            Syntax = "proto2",
            Dependency = { Target },
            MessageType = { Message("Uses", ".Kept") },
        };
        var live = new FileDescriptorProto
        {
            Name = "live.proto",
            Syntax = "proto2",
            MessageType = { Message("LiveType") },
        };
        var dead = new FileDescriptorProto
        {
            Name = "dead.proto",
            Syntax = "proto2",
            MessageType = { Message("DeadType") },
        };
        return [target, consumer, live, dead];
    }

    private static FileDescriptorProto Find(IEnumerable<FileDescriptorProto> files, string name) =>
        files.Single(f => f.Name == name);

    [Xunit.Fact]
    public void Keeps_Referenced_Types_And_Their_Transitive_Closure()
    {
        var files = BuildSet();

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Equal(["Kept", "Reached"], outcome.Pruned.MessageType.Select(m => m.Name));
        Xunit.Assert.Empty(outcome.Pruned.EnumType);
        Xunit.Assert.Equal(2, outcome.KeptTopLevel);
        Xunit.Assert.Equal(2, outcome.RemovedTopLevel); // Orphan + UnusedEnum
    }

    [Xunit.Fact]
    public void Never_Mutates_The_Input_Set()
    {
        var files = BuildSet();
        var originalTargetBytes = Find(files, Target).ToByteArray();

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        // The outcome is a fresh clone; the caller's descriptor must be byte-untouched.
        Xunit.Assert.Equal(originalTargetBytes, Find(files, Target).ToByteArray());
    }

    [Xunit.Fact]
    public void Drops_Imports_The_Closure_No_Longer_Needs()
    {
        var files = BuildSet();

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Equal(["live.proto"], outcome.Pruned.Dependency);
        Xunit.Assert.Equal(["dead.proto"], outcome.DroppedImports);
    }

    [Xunit.Fact]
    public void Preserves_Declared_Order_Not_Discovery_Order()
    {
        var files = BuildSet();
        // Declare Reached BEFORE Kept. Discovery reaches Kept first (it is the root), so a
        // discovery-ordered rebuild would flip them; the emitter treats declared order as
        // canonical, so the prune must not.
        var target = Find(files, Target);
        var kept = target.MessageType[0];
        var reached = target.MessageType[1];
        var orphan = target.MessageType[2];
        target.MessageType.Clear();
        target.MessageType.Add(reached);
        target.MessageType.Add(kept);
        target.MessageType.Add(orphan);

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Equal(["Reached", "Kept"], outcome.Pruned.MessageType.Select(m => m.Name));
    }

    [Xunit.Fact]
    public void A_Reference_To_A_Nested_Type_Keeps_The_Whole_Parent()
    {
        var files = BuildSet();
        var outer = Message("Outer");
        outer.NestedType.Add(Message("Inner"));
        Find(files, Target).MessageType.Add(outer);
        Find(files, "consumer.proto").MessageType.Add(Message("UsesNested", ".Outer.Inner"));

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        var keptOuter = outcome.Pruned.MessageType.Single(m => m.Name == "Outer");
        Xunit.Assert.Equal(["Inner"], keptOuter.NestedType.Select(n => n.Name));
    }

    [Xunit.Fact]
    public void An_Extension_Targeting_A_Declared_Type_Counts_As_A_Reference()
    {
        var files = BuildSet();
        // consumer extends Orphan rather than referencing it as a field type. Extendee must be
        // treated as a reference or the extended type is pruned out from under the extension.
        Find(files, "consumer.proto").Extension.Add(new FieldDescriptorProto
        {
            Name = "ext",
            Number = 100,
            Type = Type.Int32,
            Label = Label.Optional,
            Extendee = ".Orphan",
            JsonName = "ext",
        });

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Contains("Orphan", outcome.Pruned.MessageType.Select(m => m.Name));
    }

    [Xunit.Fact]
    public void Collects_Roots_From_Every_File_Not_Just_One_Importer()
    {
        var files = BuildSet();
        // A second consumer reaches Orphan; pruning from the first consumer's view alone would
        // delete it (and with it the dead.proto import its field needs).
        files.Add(new FileDescriptorProto
        {
            Name = "second-consumer.proto",
            Syntax = "proto2",
            Dependency = { Target },
            MessageType = { Message("AlsoUses", ".Orphan") },
        });

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Contains("Orphan", outcome.Pruned.MessageType.Select(m => m.Name));
        Xunit.Assert.Contains("dead.proto", outcome.Pruned.Dependency);
    }

    [Xunit.Fact]
    public void Unreferenced_Target_Is_Left_Whole()
    {
        var files = BuildSet();
        files.Remove(Find(files, "consumer.proto"));

        // No file references the target: an empty closure is far likelier to mean reference
        // detection broke than that the file is dead, so the prune must refuse.
        Xunit.Assert.Null(ReferencedClosurePruner.TryPrune(files, Target));
    }

    [Xunit.Fact]
    public void Absent_Target_Is_A_NoOp()
    {
        Xunit.Assert.Null(ReferencedClosurePruner.TryPrune(BuildSet(), "no-such.proto"));
    }

    [Xunit.Fact]
    public void Full_Coverage_Is_A_NoOp()
    {
        var files = BuildSet();
        var target = Find(files, Target);
        target.EnumType.Clear();
        target.MessageType.Remove(target.MessageType.Single(m => m.Name == "Orphan"));

        // Everything declared is already in the closure; rebuilding an identical FDP is churn.
        Xunit.Assert.Null(ReferencedClosurePruner.TryPrune(files, Target));
    }

    [Xunit.Fact]
    public void Descriptor_Proto_Import_Survives_Without_Any_Type_Reference()
    {
        var files = BuildSet();
        // Custom options hang off the options messages, not off type references, so no scan can
        // prove descriptor.proto necessary — it must survive on name.
        Find(files, Target).Dependency.Add("google/protobuf/descriptor.proto");

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Contains("google/protobuf/descriptor.proto", outcome.Pruned.Dependency);
        Xunit.Assert.DoesNotContain("google/protobuf/descriptor.proto", outcome.DroppedImports);
    }

    [Xunit.Fact]
    public void Public_Dependency_Indices_Are_Remapped_Across_Dropped_Imports()
    {
        var files = BuildSet();
        var target = Find(files, Target);
        // dependency = [live.proto, dead.proto], both marked public. dead.proto is dropped, so
        // index 0 must survive as 0 and index 1 must disappear — copying the list verbatim would
        // leave index 1 silently pointing past the end (or at the wrong import).
        target.PublicDependency.Add(0);
        target.PublicDependency.Add(1);

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Equal(["live.proto"], outcome.Pruned.Dependency);
        Xunit.Assert.Equal([0], outcome.Pruned.PublicDependency);
    }

    [Xunit.Fact]
    public void Package_Qualified_Declarations_Resolve()
    {
        // Same shape as BuildSet but with the target and consumer under a package, matching how
        // packaged descriptors qualify references (.pkg.Type).
        var target = new FileDescriptorProto
        {
            Name = Target,
            Package = "pkg",
            Syntax = "proto2",
            MessageType = { Message("Kept"), Message("Orphan") },
        };
        var consumer = new FileDescriptorProto
        {
            Name = "consumer.proto",
            Syntax = "proto2",
            Dependency = { Target },
            MessageType = { Message("Uses", ".pkg.Kept") },
        };
        var files = new List<FileDescriptorProto> { target, consumer };

        var outcome = ReferencedClosurePruner.TryPrune(files, Target);

        Xunit.Assert.NotNull(outcome);
        Xunit.Assert.Equal(["Kept"], outcome.Pruned.MessageType.Select(m => m.Name));
    }
}
