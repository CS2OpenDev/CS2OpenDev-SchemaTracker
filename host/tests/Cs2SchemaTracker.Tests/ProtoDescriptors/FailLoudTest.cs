// fail-loud tests — (fail-loud, never-partial) and (no
// partial output if extraction throws mid-write).

using Cs2SchemaTracker.Host.ProtoDescriptors;

using Google.Protobuf;
using Google.Protobuf.Reflection;

using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace Cs2SchemaTracker.Tests.ProtoDescriptors;

public class FailLoudTest
{
    private static FileDescriptorProto BuildFdp(string name, int fieldNumber)
    {
        return new FileDescriptorProto
        {
            Name = name,
            Package = "test",
            Syntax = "proto3",
            MessageType =
            {
                new DescriptorProto
                {
                    Name = "Msg",
                    Field =
                    {
                        new FieldDescriptorProto
                        {
                            Name = "x",
                            Number = fieldNumber,
                            Type = Type.Int32,
                            Label = Label.Optional,
                            JsonName = "x",
                        },
                    },
                },
            },
        };
    }

    private static string CreateSyntheticBinary(string dir, string filename, FileDescriptorProto fdp)
    {
        var path = Path.Combine(dir, filename);
        using var ms = new MemoryStream();
        var noise = new byte[256];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = 0xff;
        ms.Write(noise);
        ms.Write(fdp.ToByteArray());
        ms.Write(noise);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Xunit.Fact]
    public void Name_Collision_With_Differing_Bytes_Resolves_First_Wins_And_Warns()
    {
        // NEW POLICY: a same-name descriptor whose bytes differ between binaries (even after
        // stripping source_code_info) is NOT a fail-loud condition — many CS2 DLLs statically
        // link their own protobuf runtime and embed byte-differing copies of the same well-known
        // dependency descriptor. We resolve deterministically to the Ordinal-FIRST source's copy
        // and WARN loudly to stderr (spirit: visible, never silent).: the choice is
        // the Ordinal-min source path, so it does not depend on enumeration order.
        var workDir = Path.Combine(Path.GetTempPath(), "collide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // Two FDPs named the same but with structurally different contents
            // (different field number => different bytes, and they still differ once
            // source_code_info is stripped because neither carries any).
            var binA = CreateSyntheticBinary(binDir, "a.so", BuildFdp("conflict.proto", 1));
            var binB = CreateSyntheticBinary(binDir, "b.so", BuildFdp("conflict.proto", 2));

            var outDir = Path.Combine(workDir, "out");
            var extractor = new ProtoDescriptorExtractor();

            // Capture warnings via the injected sink (not the process-global Console.Error,
            // which races other tests under xUnit parallelism).
            var captured = new StringWriter();
            // Pass binaries in reverse order; the winner must STILL be a.so (Ordinal-min path).
            extractor.Extract(new[] { binB, binA }, outDir, requireNonEmpty: false, warningSink: captured);

            // A single canonical copy was emitted (no throw, no partial state).
            Xunit.Assert.True(File.Exists(Path.Combine(outDir, "protos", "conflict.proto")));
            var set = FileDescriptorSet.Parser.ParseFrom(
                File.ReadAllBytes(Path.Combine(outDir, "protos.descriptorset")));
            Xunit.Assert.Single(set.File);
            Xunit.Assert.Equal("conflict.proto", set.File[0].Name);
            // a.so embeds field number 1; that is the Ordinal-first source and must win.
            Xunit.Assert.Equal(1, set.File[0].MessageType[0].Field[0].Number);

            // The collision was surfaced LOUDLY.
            var err = captured.ToString();
            Xunit.Assert.Contains("WARNING", err, StringComparison.Ordinal);
            Xunit.Assert.Contains("conflict.proto", err, StringComparison.Ordinal);
            Xunit.Assert.Contains("a.so", err, StringComparison.Ordinal);
            Xunit.Assert.Contains("b.so", err, StringComparison.Ordinal);

            // No leftover .tmp directories beside outDir.
            var parent = Path.GetDirectoryName(Path.GetFullPath(outDir))!;
            var leftovers = Directory.EnumerateDirectories(parent, ".protos.*.tmp").ToList();
            Xunit.Assert.Empty(leftovers);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try
                { Directory.Delete(workDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    [Xunit.Fact]
    public void Truncated_Descriptor_Bytes_Never_Emit_A_Bogus_Artifact_And_Fail_Loud_When_Required()
    {
        // (item 4): genuinely-corrupt / truncated FileDescriptorProto bytes must NOT be
        // silently turned into a real artifact. The scanner's round-trip verification rejects an
        // unparseable anchor (it never round-trips), so a binary whose ONLY descriptor-like region
        // is truncated yields ZERO descriptors. Under the real-corpus path (requireNonEmpty:true)
        // that is then a loud structural failure with no output bytes on disk — corruption is
        // distinct from the benign duplicate-name divergence we now tolerate.
        var workDir = Path.Combine(Path.GetTempPath(), "corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // Start from a VALID FDP's bytes, then TRUNCATE the back half so it cannot parse /
            // round-trip. Surround with noise so the only candidate region is the corrupt one.
            var good = BuildFdp("corrupt.proto", 1).ToByteArray();
            var truncated = good.AsSpan(0, good.Length / 2).ToArray();

            var binPath = Path.Combine(binDir, "corrupt.so");
            using (var ms = new MemoryStream())
            {
                var noise = new byte[256];
                for (var i = 0; i < noise.Length; i++)
                    noise[i] = 0xff;
                ms.Write(noise);
                ms.Write(truncated);
                ms.Write(noise);
                File.WriteAllBytes(binPath, ms.ToArray());
            }

            // Scanner discards the truncated region as noise -> zero descriptors recovered.
            Xunit.Assert.Empty(DescriptorScanner.Scan(binPath));

            var outDir = Path.Combine(workDir, "out");

            // requireNonEmpty:true -> zero descriptors is a loud structural failure.
            Xunit.Assert.Throws<InvalidDataException>(() =>
                new ProtoDescriptorExtractor().Extract(new[] { binPath }, outDir, requireNonEmpty: true));

            // nothing landed on disk.
            Xunit.Assert.False(Directory.Exists(Path.Combine(outDir, "protos")));
            Xunit.Assert.False(File.Exists(Path.Combine(outDir, "protos.descriptorset")));
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try
                { Directory.Delete(workDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    [Xunit.Fact]
    public void Missing_Input_Path_Throws_Before_Any_Output_Bytes()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var outDir = Path.Combine(workDir, "out");
            var missing = Path.Combine(workDir, "no-such-binary.so");

            var extractor = new ProtoDescriptorExtractor();
            Xunit.Assert.Throws<FileNotFoundException>(() =>
                extractor.Extract(new[] { missing }, outDir));

            Xunit.Assert.False(Directory.Exists(Path.Combine(outDir, "protos")));
            Xunit.Assert.False(File.Exists(Path.Combine(outDir, "protos.descriptorset")));
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try
                { Directory.Delete(workDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }
}
