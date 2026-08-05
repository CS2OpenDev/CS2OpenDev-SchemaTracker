// tests for the byte-stream FileDescriptorProto scanner.
//
// These tests use synthetic blobs. The real-CS2-binary half of this coverage
// waits on Steam acquisition.

using Cs2SchemaTracker.Host.ProtoDescriptors;

using Google.Protobuf;
using Google.Protobuf.Reflection;

using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace Cs2SchemaTracker.Tests.ProtoDescriptors;

public class DescriptorScannerTest
{
    private static FileDescriptorProto BuildSimpleFdp(string name)
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
                    Name = "Foo",
                    Field =
                    {
                        new FieldDescriptorProto
                        {
                            Name = "x",
                            Number = 1,
                            Type = Type.Int32,
                            Label = Label.Optional,
                            JsonName = "x",
                        },
                    },
                },
            },
        };
    }

    [Xunit.Fact]
    public void Finds_Single_Embedded_Fdp_With_Noise()
    {
        var fdp = BuildSimpleFdp("test/sample.proto");
        var fdpBytes = fdp.ToByteArray();

        var blob = new byte[256 + fdpBytes.Length + 256];
        // Fill with deterministic non-FDP noise (no 0x0a anywhere).
        for (var i = 0; i < blob.Length; i++)
            blob[i] = 0xff;
        Array.Copy(fdpBytes, 0, blob, 256, fdpBytes.Length);

        var found = DescriptorScanner.Scan(blob);
        Xunit.Assert.Single(found);
        Xunit.Assert.Equal(fdpBytes, found[0].ToByteArray());
    }

    [Xunit.Fact]
    public void Finds_Three_Back_To_Back_Fdps_Through_Garbage()
    {
        var a = BuildSimpleFdp("a.proto");
        var b = BuildSimpleFdp("b.proto");
        var c = BuildSimpleFdp("c.proto");
        var aBytes = a.ToByteArray();
        var bBytes = b.ToByteArray();
        var cBytes = c.ToByteArray();

        // Garbage prefix/separators avoid 0x0a to prevent confusing the scanner
        // (some 0x0a bytes WOULD just be discarded as noise, but keep the test
        // tight and deterministic).
        var sep = new byte[32];
        for (var i = 0; i < sep.Length; i++)
            sep[i] = 0xff;

        var blob = new List<byte>();
        blob.AddRange(sep);
        blob.AddRange(aBytes);
        blob.AddRange(sep);
        blob.AddRange(bBytes);
        blob.AddRange(sep);
        blob.AddRange(cBytes);
        blob.AddRange(sep);

        var found = DescriptorScanner.Scan(blob.ToArray());
        Xunit.Assert.Equal(3, found.Count);
        Xunit.Assert.Equal("a.proto", found[0].Name);
        Xunit.Assert.Equal("b.proto", found[1].Name);
        Xunit.Assert.Equal("c.proto", found[2].Name);
    }

    [Xunit.Fact]
    public void Half_Written_Header_At_Eof_Does_Not_Crash()
    {
        var good = BuildSimpleFdp("good.proto").ToByteArray();
        // Construct a partial FDP-like prefix: tag 0x0a, varint name length 0x0c,
        // then ASCII "bad.proto" which is 9 bytes — TRUNCATED. The scanner must
        // see this as noise and return only the good FDP.
        var halfFdp = new byte[] { 0x0a, 0x0c, (byte)'b', (byte)'a', (byte)'d', (byte)'.', (byte)'p', (byte)'r', (byte)'o' };

        var blob = new byte[good.Length + 32 + halfFdp.Length];
        Array.Copy(good, 0, blob, 0, good.Length);
        for (var i = good.Length; i < good.Length + 32; i++)
            blob[i] = 0xff;
        Array.Copy(halfFdp, 0, blob, good.Length + 32, halfFdp.Length);

        var found = DescriptorScanner.Scan(blob);
        Xunit.Assert.Single(found);
        Xunit.Assert.Equal("good.proto", found[0].Name);
    }

    [Xunit.Fact]
    public void Scan_Of_All_Zero_Buffer_Returns_Empty()
    {
        var blob = new byte[4096];   // all zero
        var found = DescriptorScanner.Scan(blob);
        Xunit.Assert.Empty(found);
    }

    [Xunit.Fact]
    public void Scan_Of_Empty_Buffer_Returns_Empty()
    {
        var found = DescriptorScanner.Scan(Array.Empty<byte>());
        Xunit.Assert.Empty(found);
    }

    [Xunit.Fact]
    public void File_Path_Variant_Reads_From_Disk()
    {
        var fdp = BuildSimpleFdp("disk/sample.proto");
        var bytes = fdp.ToByteArray();
        var path = Path.Combine(Path.GetTempPath(), "scanner-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            // Add some noise on either side.
            var blob = new byte[64 + bytes.Length + 64];
            for (var i = 0; i < blob.Length; i++)
                blob[i] = 0xff;
            Array.Copy(bytes, 0, blob, 64, bytes.Length);
            File.WriteAllBytes(path, blob);

            var found = DescriptorScanner.Scan(path);
            Xunit.Assert.Single(found);
            Xunit.Assert.Equal("disk/sample.proto", found[0].Name);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Xunit.Fact]
    public void Missing_Input_Path_Throws_FileNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));
        Xunit.Assert.Throws<FileNotFoundException>(() => DescriptorScanner.Scan(missing));
    }
}
