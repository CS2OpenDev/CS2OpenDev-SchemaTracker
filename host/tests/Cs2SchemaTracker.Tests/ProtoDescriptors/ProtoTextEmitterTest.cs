// ProtoTextEmitter — emit canonical .proto text from a FileDescriptorProto.
//
// The protoc-compile test is the load-bearing one — the contract requires that
// every emitted .proto compiles cleanly via protoc with no warnings.
// We invoke protoc as a subprocess and assert exit code 0 with empty stderr.
// If protoc isn't on PATH, that's a test-environment gap; we throw with a
// loud message rather than silently skipping (build docs cover protoc
// usage so CI machines will have it).

using System.Diagnostics;

using Cs2SchemaTracker.Host.ProtoDescriptors;

using Google.Protobuf;
using Google.Protobuf.Reflection;

using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace Cs2SchemaTracker.Tests.ProtoDescriptors;

public class ProtoTextEmitterTest
{
    [Xunit.Fact]
    public void Emits_Lf_Endings_And_Final_Newline()
    {
        var fdp = MinimalProto3Fdp("simple.proto");
        var text = ProtoTextEmitter.Emit(fdp);

        Xunit.Assert.DoesNotContain("\r", text);
        Xunit.Assert.EndsWith("\n", text);
        Xunit.Assert.StartsWith("syntax = \"proto3\";", text);
    }

    [Xunit.Fact]
    public void Same_Input_Twice_Is_Byte_Identical()
    {
        var fdp = NonTrivialFdp("dets.proto");
        var first = ProtoTextEmitter.Emit(fdp);
        var second = ProtoTextEmitter.Emit(fdp);
        Xunit.Assert.Equal(first, second);
    }

    [Xunit.Fact]
    public void Protoc_Compiles_Emitted_Proto_Cleanly_With_No_Warnings()
    {
        var protocPath = FindProtoc();
        if (protocPath is null)
        {
            throw new Xunit.Sdk.XunitException(
                "protoc not on PATH; this test requires protoc to verify .proto output. " +
                "Install protoc or run scripts/bootstrap-windows.ps1.");
        }

        var fdp = NonTrivialFdp("test/sample.proto");
        var text = ProtoTextEmitter.Emit(fdp);

        var tmp = Path.Combine(Path.GetTempPath(), "emitter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var protoPath = Path.Combine(tmp, "test", "sample.proto");
            Directory.CreateDirectory(Path.GetDirectoryName(protoPath)!);
            File.WriteAllText(protoPath, text);

            var outBin = Path.Combine(tmp, "out.bin");

            var psi = new ProcessStartInfo
            {
                FileName = protocPath,
                ArgumentList =
                {
                    $"--proto_path={tmp}",
                    $"--descriptor_set_out={outBin}",
                    "test/sample.proto",
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);

            Xunit.Assert.True(p.HasExited, "protoc did not exit within 30s");
            Xunit.Assert.Equal(0, p.ExitCode);
            // The contract: NO warnings. protoc writes warnings to stderr.
            Xunit.Assert.True(string.IsNullOrWhiteSpace(stderr),
                $"protoc emitted warnings/errors:\n{stderr}\nstdout:\n{stdout}\nemitted proto:\n{text}");

            // Round-trip: parse protoc's descriptor set output and assert
            // structural equivalence.
            Xunit.Assert.True(File.Exists(outBin), "protoc did not produce descriptor_set_out");
            var setBytes = File.ReadAllBytes(outBin);
            var roundTripSet = FileDescriptorSet.Parser.ParseFrom(setBytes);
            Xunit.Assert.Single(roundTripSet.File);

            var compiled = roundTripSet.File[0];
            Xunit.Assert.Equal(fdp.Name, compiled.Name);
            Xunit.Assert.Equal(fdp.Package, compiled.Package);
            Xunit.Assert.Equal("proto3", compiled.Syntax);
            Xunit.Assert.Equal(fdp.MessageType.Count, compiled.MessageType.Count);
            Xunit.Assert.Equal(fdp.EnumType.Count, compiled.EnumType.Count);

            // Field-by-field on the first message.
            var origMsg = fdp.MessageType[0];
            var newMsg = compiled.MessageType[0];
            Xunit.Assert.Equal(origMsg.Name, newMsg.Name);
            Xunit.Assert.Equal(origMsg.Field.Count, newMsg.Field.Count);
            for (var i = 0; i < origMsg.Field.Count; i++)
            {
                Xunit.Assert.Equal(origMsg.Field[i].Name, newMsg.Field[i].Name);
                Xunit.Assert.Equal(origMsg.Field[i].Number, newMsg.Field[i].Number);
                Xunit.Assert.Equal(origMsg.Field[i].Type, newMsg.Field[i].Type);
            }
        }
        finally
        {
            if (Directory.Exists(tmp))
            {
                try
                { Directory.Delete(tmp, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    private static FileDescriptorProto MinimalProto3Fdp(string name) =>
        new()
        {
            Name = name,
            Package = "test",
            Syntax = "proto3",
            MessageType =
            {
                new DescriptorProto
                {
                    Name = "Empty",
                },
            },
        };

    /// <summary>
    /// Exercise: nested message, enum, repeated scalar, map field, oneof,
    /// cross-message reference. The protoc-compile test is the real check —
    /// this just gives it something non-trivial to chew on.
    /// </summary>
    private static FileDescriptorProto NonTrivialFdp(string name)
    {
        var fdp = new FileDescriptorProto
        {
            Name = name,
            Package = "test",
            Syntax = "proto3",
        };

        // Top-level enum.
        fdp.EnumType.Add(new EnumDescriptorProto
        {
            Name = "Color",
            Value =
            {
                new EnumValueDescriptorProto { Name = "COLOR_UNSPECIFIED", Number = 0 },
                new EnumValueDescriptorProto { Name = "RED", Number = 1 },
                new EnumValueDescriptorProto { Name = "BLUE", Number = 2 },
            },
        });

        // Message: Outer { Nested nested = 1; repeated int32 nums = 2; map<string,int32> ids = 3; oneof choice { string s = 4; int32 i = 5; } }
        var outer = new DescriptorProto { Name = "Outer" };

        // Nested message.
        outer.NestedType.Add(new DescriptorProto { Name = "Nested" });

        // Map entry (synthetic). FDP convention: a nested message with options.map_entry = true.
        outer.NestedType.Add(new DescriptorProto
        {
            Name = "IdsEntry",
            Field =
            {
                new FieldDescriptorProto { Name = "key", Number = 1, Type = Type.String, Label = Label.Optional, JsonName = "key" },
                new FieldDescriptorProto { Name = "value", Number = 2, Type = Type.Int32, Label = Label.Optional, JsonName = "value" },
            },
            Options = new MessageOptions { MapEntry = true },
        });

        // Fields.
        outer.Field.Add(new FieldDescriptorProto
        {
            Name = "nested",
            Number = 1,
            Type = Type.Message,
            TypeName = ".test.Outer.Nested",
            Label = Label.Optional,
            JsonName = "nested",
        });
        outer.Field.Add(new FieldDescriptorProto
        {
            Name = "nums",
            Number = 2,
            Type = Type.Int32,
            Label = Label.Repeated,
            JsonName = "nums",
        });
        outer.Field.Add(new FieldDescriptorProto
        {
            Name = "ids",
            Number = 3,
            Type = Type.Message,
            TypeName = ".test.Outer.IdsEntry",
            Label = Label.Repeated,
            JsonName = "ids",
        });
        outer.Field.Add(new FieldDescriptorProto
        {
            Name = "color",
            Number = 6,
            Type = Type.Enum,
            TypeName = ".test.Color",
            Label = Label.Optional,
            JsonName = "color",
        });

        // Oneof.
        outer.OneofDecl.Add(new OneofDescriptorProto { Name = "choice" });
        outer.Field.Add(new FieldDescriptorProto
        {
            Name = "s",
            Number = 4,
            Type = Type.String,
            Label = Label.Optional,
            OneofIndex = 0,
            JsonName = "s",
        });
        outer.Field.Add(new FieldDescriptorProto
        {
            Name = "i",
            Number = 5,
            Type = Type.Int32,
            Label = Label.Optional,
            OneofIndex = 0,
            JsonName = "i",
        });

        fdp.MessageType.Add(outer);
        return fdp;
    }

    private static string? FindProtoc()
    {
        var name = OperatingSystem.IsWindows() ? "protoc.exe" : "protoc";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir))
                continue;
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Some PATH entries can be invalid (e.g. on broken Windows installs).
                continue;
            }
        }
        return null;
    }
}
