// orchestrator tests — dedupe, ordering, atomic emission, byte-determinism.

using Cs2SchemaTracker.Host.ProtoDescriptors;

using Google.Protobuf;
using Google.Protobuf.Reflection;

using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace Cs2SchemaTracker.Tests.ProtoDescriptors;

public class ProtoDescriptorExtractorTest
{
    private static FileDescriptorProto BuildFdp(string name, int messageFieldNumber = 1)
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
                            Number = messageFieldNumber,
                            Type = Type.Int32,
                            Label = Label.Optional,
                            JsonName = "x",
                        },
                    },
                },
            },
        };
    }

    private static string CreateSyntheticBinary(string dir, string filename, params FileDescriptorProto[] embedded)
    {
        var path = Path.Combine(dir, filename);
        using var ms = new MemoryStream();
        // Prefix noise.
        var noise = new byte[256];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = 0xff;
        ms.Write(noise);
        foreach (var fdp in embedded)
        {
            var fdpBytes = fdp.ToByteArray();
            ms.Write(fdpBytes);
            ms.Write(noise);
        }
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Xunit.Fact]
    public void Extracts_All_Embedded_Fdps_And_Sorts_Them()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // Two binaries; three unique FDPs total.
            var binA = CreateSyntheticBinary(binDir, "libone.so", BuildFdp("zeta.proto"), BuildFdp("alpha.proto"));
            var binB = CreateSyntheticBinary(binDir, "libtwo.so", BuildFdp("middle.proto"));

            var outDir = Path.Combine(workDir, "out");
            var extractor = new ProtoDescriptorExtractor();
            extractor.Extract(new[] { binA, binB }, outDir);

            Xunit.Assert.True(Directory.Exists(Path.Combine(outDir, "protos")));
            Xunit.Assert.True(File.Exists(Path.Combine(outDir, "protos.descriptorset")));

            // Each FDP got a .proto file.
            Xunit.Assert.True(File.Exists(Path.Combine(outDir, "protos", "alpha.proto")));
            Xunit.Assert.True(File.Exists(Path.Combine(outDir, "protos", "middle.proto")));
            Xunit.Assert.True(File.Exists(Path.Combine(outDir, "protos", "zeta.proto")));

            // descriptorset has all three, in Name-sorted order.
            var setBytes = File.ReadAllBytes(Path.Combine(outDir, "protos.descriptorset"));
            var set = FileDescriptorSet.Parser.ParseFrom(setBytes);
            Xunit.Assert.Equal(3, set.File.Count);
            Xunit.Assert.Equal("alpha.proto", set.File[0].Name);
            Xunit.Assert.Equal("middle.proto", set.File[1].Name);
            Xunit.Assert.Equal("zeta.proto", set.File[2].Name);
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
    public void Two_Runs_Produce_Byte_Identical_Output()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "det-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            var binA = CreateSyntheticBinary(binDir, "libone.so", BuildFdp("aa.proto"), BuildFdp("bb.proto"));
            var binB = CreateSyntheticBinary(binDir, "libtwo.so", BuildFdp("cc.proto"));

            var outA = Path.Combine(workDir, "outA");
            var outB = Path.Combine(workDir, "outB");

            new ProtoDescriptorExtractor().Extract(new[] { binA, binB }, outA);
            new ProtoDescriptorExtractor().Extract(new[] { binA, binB }, outB);

            foreach (var rel in new[] { "protos/aa.proto", "protos/bb.proto", "protos/cc.proto", "protos.descriptorset" })
            {
                var bytesA = File.ReadAllBytes(Path.Combine(outA, rel.Replace('/', Path.DirectorySeparatorChar)));
                var bytesB = File.ReadAllBytes(Path.Combine(outB, rel.Replace('/', Path.DirectorySeparatorChar)));
                Xunit.Assert.Equal(bytesA, bytesB);
            }
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
    public void Duplicate_Name_With_Identical_Bytes_Is_Deduped()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "dedupe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // Same FDP in two binaries — same bytes, so dedupe.
            var shared = BuildFdp("shared.proto");
            var binA = CreateSyntheticBinary(binDir, "a.so", shared);
            var binB = CreateSyntheticBinary(binDir, "b.so", shared);

            var outDir = Path.Combine(workDir, "out");
            new ProtoDescriptorExtractor().Extract(new[] { binA, binB }, outDir);

            var set = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(outDir, "protos.descriptorset")));
            Xunit.Assert.Single(set.File);
            Xunit.Assert.Equal("shared.proto", set.File[0].Name);
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
    public void RequireNonEmpty_Zero_Descriptors_Throws_And_Writes_Nothing()
    {
        // with requireNonEmpty:true (the real extract set), a binary set that yields ZERO
        // FDPs is a structural failure — throw before any bytes land, leave no protos/ output.
        var workDir = Path.Combine(Path.GetTempPath(), "zero-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);
            // A binary with only noise — no embedded FDP.
            var bin = Path.Combine(binDir, "noproto.so");
            var noise = new byte[1024];
            for (var i = 0; i < noise.Length; i++)
                noise[i] = 0xff;
            File.WriteAllBytes(bin, noise);

            var outDir = Path.Combine(workDir, "out");
            Xunit.Assert.Throws<InvalidDataException>(() =>
                new ProtoDescriptorExtractor().Extract(new[] { bin }, outDir, requireNonEmpty: true));

            // Nothing was written.
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
    public void Default_Permissive_Zero_Descriptors_Emits_Empty_Set()
    {
        // The undocumented --binaries dev hook (requireNonEmpty:false, the default) tolerates a
        // zero-descriptor scan: it emits an empty protos/ + descriptorset rather than throwing.
        var workDir = Path.Combine(Path.GetTempPath(), "zero-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);
            var bin = Path.Combine(binDir, "noproto.so");
            var noise = new byte[1024];
            for (var i = 0; i < noise.Length; i++)
                noise[i] = 0xff;
            File.WriteAllBytes(bin, noise);

            var outDir = Path.Combine(workDir, "out");
            new ProtoDescriptorExtractor().Extract(new[] { bin }, outDir);   // requireNonEmpty defaults false

            Xunit.Assert.True(Directory.Exists(Path.Combine(outDir, "protos")));
            Xunit.Assert.True(File.Exists(Path.Combine(outDir, "protos.descriptorset")));
            var set = FileDescriptorSet.Parser.ParseFrom(
                File.ReadAllBytes(Path.Combine(outDir, "protos.descriptorset")));
            Xunit.Assert.Empty(set.File);
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
    public void Collision_Resolution_Is_Independent_Of_Input_Order()
    {
        // the canonical copy chosen for a byte-differing same-name collision must be a
        // PURE function of the input SET (Ordinal-min source path), not of enumeration order.
        // Two runs over the SAME binaries in DIFFERENT (shuffled) order must produce
        // byte-identical protos/ files AND a byte-identical protos.descriptorset.
        var workDir = Path.Combine(Path.GetTempPath(), "shuffle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // Several binaries, each embedding a byte-DIFFERING copy of the SAME descriptor name,
            // plus a couple of unique descriptors. Ordinal-min path among the colliders is "m1.so".
            var m1 = CreateSyntheticBinary(binDir, "m1.so", BuildFdp("dep.proto", 1), BuildFdp("only1.proto"));
            var m2 = CreateSyntheticBinary(binDir, "m2.so", BuildFdp("dep.proto", 2));
            var m3 = CreateSyntheticBinary(binDir, "m3.so", BuildFdp("dep.proto", 3), BuildFdp("only3.proto"));

            var inputs = new[] { m1, m2, m3 };

            var outForward = Path.Combine(workDir, "forward");
            var outShuffled = Path.Combine(workDir, "shuffled");

            // Forward order (already Ordinal-sorted).
            var fwd = inputs.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            // A deterministic shuffle: reverse + a swap so it is clearly not sorted.
            var shuffled = new[] { m3, m1, m2 };

            new ProtoDescriptorExtractor().Extract(fwd, outForward);
            new ProtoDescriptorExtractor().Extract(shuffled, outShuffled);

            foreach (var rel in new[]
                     {
                         "protos/dep.proto", "protos/only1.proto", "protos/only3.proto",
                         "protos.descriptorset",
                     })
            {
                var a = File.ReadAllBytes(Path.Combine(outForward, rel.Replace('/', Path.DirectorySeparatorChar)));
                var b = File.ReadAllBytes(Path.Combine(outShuffled, rel.Replace('/', Path.DirectorySeparatorChar)));
                Xunit.Assert.Equal(a, b);
            }

            // And confirm the canonical dep.proto is m1.so's copy (field number 1).
            var set = FileDescriptorSet.Parser.ParseFrom(
                File.ReadAllBytes(Path.Combine(outForward, "protos.descriptorset")));
            var dep = set.File.Single(f => f.Name == "dep.proto");
            Xunit.Assert.Equal(1, dep.MessageType[0].Field[0].Number);
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
    public void Same_Name_Differing_Only_In_SourceCodeInfo_Is_Collapsed_Silently()
    {
        // Policy item 5: copies that differ ONLY in source_code_info (debug/comment metadata)
        // are the same logical descriptor. They must collapse to one canonical copy WITHOUT a
        // warning (it is not a real schema divergence).
        var workDir = Path.Combine(Path.GetTempPath(), "sci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // Same schema; one copy carries source_code_info, the other does not -> bytes differ.
            var plain = BuildFdp("sci.proto");
            var withSci = BuildFdp("sci.proto");
            withSci.SourceCodeInfo = new SourceCodeInfo
            {
                Location = { new SourceCodeInfo.Types.Location { Path = { 1 }, Span = { 0, 0, 1 } } },
            };
            Xunit.Assert.NotEqual(plain.ToByteArray(), withSci.ToByteArray());

            // a.so (Ordinal-first) has the plain copy; b.so has the source_code_info copy.
            var binA = CreateSyntheticBinary(binDir, "a.so", plain);
            var binB = CreateSyntheticBinary(binDir, "b.so", withSci);

            var outDir = Path.Combine(workDir, "out");

            var captured = new StringWriter();
            new ProtoDescriptorExtractor().Extract(
                new[] { binA, binB }, outDir, requireNonEmpty: false, warningSink: captured);

            var set = FileDescriptorSet.Parser.ParseFrom(
                File.ReadAllBytes(Path.Combine(outDir, "protos.descriptorset")));
            Xunit.Assert.Single(set.File);
            Xunit.Assert.Equal("sci.proto", set.File[0].Name);

            // No warning was emitted: the divergence was purely source_code_info.
            Xunit.Assert.DoesNotContain("WARNING", captured.ToString(), StringComparison.Ordinal);
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
    public void Subdirectory_Names_Are_Created()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "subdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            var fdp = BuildFdp("google/protobuf/descriptor.proto");
            var bin = CreateSyntheticBinary(binDir, "lib.so", fdp);

            var outDir = Path.Combine(workDir, "out");
            new ProtoDescriptorExtractor().Extract(new[] { bin }, outDir);

            var expected = Path.Combine(outDir, "protos", "google", "protobuf", "descriptor.proto");
            Xunit.Assert.True(File.Exists(expected), $"missing: {expected}");
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

    // ---- supplemental (SDK-sourced wire) descriptor merge -----------------------------------

    [Xunit.Fact]
    public void Supplemental_Descriptor_Is_Merged_When_Not_Binary_Derived()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "supp-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);
            var bin = CreateSyntheticBinary(binDir, "lib.so", BuildFdp("binary.proto"));

            var outDir = Path.Combine(workDir, "out");
            new ProtoDescriptorExtractor().Extract(
                new[] { bin }, outDir,
                supplementalDescriptors: new[] { BuildFdp("wire.proto") });

            var set = FileDescriptorSet.Parser.ParseFrom(
                File.ReadAllBytes(Path.Combine(outDir, "protos.descriptorset")));
            // Both the binary-derived and the supplemental file are present, Name-sorted.
            Xunit.Assert.Equal(2, set.File.Count);
            Xunit.Assert.Equal("binary.proto", set.File[0].Name);
            Xunit.Assert.Equal("wire.proto", set.File[1].Name);

            // The supplemental .proto file carries the SDK-sourced provenance header; the
            // binary-derived one does not.
            var wireText = File.ReadAllText(Path.Combine(outDir, "protos", "wire.proto"));
            Xunit.Assert.StartsWith("// SDK-SOURCED wire descriptor.", wireText);
            var binText = File.ReadAllText(Path.Combine(outDir, "protos", "binary.proto"));
            Xunit.Assert.StartsWith("syntax = ", binText);
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
    public void Binary_Derived_Wins_Over_Same_Name_Supplemental()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "supp-win-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);
            // Binary carries "shared.proto" with field number 7.
            var bin = CreateSyntheticBinary(binDir, "lib.so", BuildFdp("shared.proto", messageFieldNumber: 7));

            var outDir = Path.Combine(workDir, "out");
            // A supplemental copy of the SAME name (different field number) must be IGNORED — the
            // binary-derived descriptor is always canonical and the supplemental carries no header.
            new ProtoDescriptorExtractor().Extract(
                new[] { bin }, outDir,
                supplementalDescriptors: new[] { BuildFdp("shared.proto", messageFieldNumber: 99) });

            var set = FileDescriptorSet.Parser.ParseFrom(
                File.ReadAllBytes(Path.Combine(outDir, "protos.descriptorset")));
            Xunit.Assert.Single(set.File);
            Xunit.Assert.Equal("shared.proto", set.File[0].Name);
            Xunit.Assert.Equal(7, set.File[0].MessageType[0].Field[0].Number);   // binary's copy won.

            var text = File.ReadAllText(Path.Combine(outDir, "protos", "shared.proto"));
            Xunit.Assert.StartsWith("syntax = ", text);   // NOT stamped as SDK-sourced.
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
    public void RequireNonEmpty_Guards_On_Binary_Scan_Not_Supplemental()
    {
        // A supplemental set must NOT satisfy the non-empty structural guard: zero embedded
        // descriptors in the binaries is still a structural failure even when supplementals exist.
        var workDir = Path.Combine(Path.GetTempPath(), "supp-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);
            var bin = Path.Combine(binDir, "noproto.so");
            var noise = new byte[1024];
            for (var i = 0; i < noise.Length; i++)
                noise[i] = 0xff;
            File.WriteAllBytes(bin, noise);

            var outDir = Path.Combine(workDir, "out");
            Xunit.Assert.Throws<InvalidDataException>(() =>
                new ProtoDescriptorExtractor().Extract(
                    new[] { bin }, outDir, requireNonEmpty: true,
                    supplementalDescriptors: new[] { BuildFdp("wire.proto") }));

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
    public void Gcmessages_Is_Pruned_To_Its_Referenced_Closure_End_To_End()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // A miniature of the real shape: gcmessages declares a referenced type and an orphan;
            // only the orphan reaches into steamish.proto, so that import must be dropped. The
            // consumer (cstrike15_usermessages in production) arrives as a SUPPLEMENTAL descriptor,
            // exactly as it does in a real extract — the prune must see supplemental references.
            var gc = new FileDescriptorProto
            {
                Name = "cstrike15_gcmessages.proto",
                Syntax = "proto2",
                Dependency = { "steamish.proto" },
                MessageType =
                {
                    new DescriptorProto
                    {
                        Name = "KeptData",
                        Field =
                        {
                            new FieldDescriptorProto
                            {
                                Name = "x", Number = 1, Type = Type.Int32,
                                Label = Label.Optional, JsonName = "x",
                            },
                        },
                    },
                    new DescriptorProto
                    {
                        Name = "OrphanData",
                        Field =
                        {
                            new FieldDescriptorProto
                            {
                                Name = "s", Number = 1, Type = Type.Message,
                                Label = Label.Optional, TypeName = ".SteamThing", JsonName = "s",
                            },
                        },
                    },
                },
            };
            var steamish = new FileDescriptorProto
            {
                Name = "steamish.proto",
                Syntax = "proto2",
                MessageType = { new DescriptorProto { Name = "SteamThing" } },
            };
            var consumer = new FileDescriptorProto
            {
                Name = "cstrike15_usermessages.proto",
                Syntax = "proto2",
                Dependency = { "cstrike15_gcmessages.proto" },
                MessageType =
                {
                    new DescriptorProto
                    {
                        Name = "CUserMsg",
                        Field =
                        {
                            new FieldDescriptorProto
                            {
                                Name = "d", Number = 1, Type = Type.Message,
                                Label = Label.Optional, TypeName = ".KeptData", JsonName = "d",
                            },
                        },
                    },
                },
            };

            var bin = CreateSyntheticBinary(binDir, "libgc.so", gc, steamish);
            var outDir = Path.Combine(workDir, "out");
            var warnings = new StringWriter();
            new ProtoDescriptorExtractor().Extract(
                new[] { bin }, outDir, warningSink: warnings,
                supplementalDescriptors: new[] { consumer });

            // The emitted text is the pruned closure, stamped as derived, minus the dead import.
            var gcText = File.ReadAllText(Path.Combine(outDir, "protos", "cstrike15_gcmessages.proto"));
            Xunit.Assert.StartsWith("// DERIVED CLOSURE.", gcText);
            Xunit.Assert.Contains("1 of 2 top-level types kept", gcText);
            Xunit.Assert.Contains("KeptData", gcText);
            Xunit.Assert.DoesNotContain("OrphanData", gcText);
            Xunit.Assert.DoesNotContain("steamish.proto\";", gcText);

            // The descriptorset carries the SAME pruned form (one pass fixes both outputs), and
            // steamish.proto itself — a real embedded descriptor — still travels in the set.
            var set = FileDescriptorSet.Parser.ParseFrom(
                File.ReadAllBytes(Path.Combine(outDir, "protos.descriptorset")));
            var gcInSet = set.File.Single(f => f.Name == "cstrike15_gcmessages.proto");
            Xunit.Assert.Equal(["KeptData"], gcInSet.MessageType.Select(m => m.Name));
            Xunit.Assert.Empty(gcInSet.Dependency);
            Xunit.Assert.Contains(set.File, f => f.Name == "steamish.proto");

            // The prune is loud, never silent.
            Xunit.Assert.Contains("pruned cstrike15_gcmessages.proto", warnings.ToString());
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
    public void Unreferenced_Gcmessages_Is_Emitted_Whole()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "noprune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // gcmessages present but nothing references it (no usermessages in the set): the
            // conservative branch must emit the full file with no derived-closure stamp.
            var gc = new FileDescriptorProto
            {
                Name = "cstrike15_gcmessages.proto",
                Syntax = "proto2",
                MessageType = { new DescriptorProto { Name = "LonelyData" } },
            };
            var bin = CreateSyntheticBinary(binDir, "libgc.so", gc);
            var outDir = Path.Combine(workDir, "out");
            new ProtoDescriptorExtractor().Extract(new[] { bin }, outDir);

            var gcText = File.ReadAllText(Path.Combine(outDir, "protos", "cstrike15_gcmessages.proto"));
            Xunit.Assert.DoesNotContain("DERIVED CLOSURE", gcText);
            Xunit.Assert.Contains("LonelyData", gcText);
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
