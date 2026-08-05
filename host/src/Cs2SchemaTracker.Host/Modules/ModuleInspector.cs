// Format-dispatching module inspector.
//
// Reads the first 4 bytes of a candidate module file to decide which
// format-specific inspector to invoke, then streams the file once more to
// compute its SHA-256. Returns a record consumed by ModuleManifestEmitter.
//
// Invariants:
// Fail-loud: unreadable input → throw; unknown magic → throw
//          InvalidDataException with the path + first four bytes hex.
// Independence: no third-party PE/ELF parsing dep.
// Determinism: SHA-256 is independently reproducible via `sha256sum`.

using System.Security.Cryptography;

namespace Cs2SchemaTracker.Host.Modules;

internal static class ModuleInspector
{
    public sealed record InspectionResult(
        string Path,
        long SizeBytes,
        byte[] Sha256,
        int ExportCount);

    /// <summary>
    /// Inspect the binary at <paramref name="path"/>. Dispatches to a format
    /// inspector based on the first 4 magic bytes (PE/COFF: "MZ..", ELF64:
    /// 0x7F 'E' 'L' 'F'). Unknown formats throw.
    /// </summary>
    public static InspectionResult Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"ModuleInspector: input path '{path}' does not exist.", path);
        }

        // 1. Detect format from magic.
        Span<byte> magic = stackalloc byte[4];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (fs.Length < magic.Length)
            {
                throw new InvalidDataException(
                    $"ModuleInspector: '{path}' is {fs.Length} bytes; too small to identify format.");
            }
            var read = fs.Read(magic);
            if (read != magic.Length)
            {
                throw new InvalidDataException(
                    $"ModuleInspector: '{path}' short read while sniffing magic.");
            }
        }

        long sizeBytes;
        int exportCount;
        if (magic[0] == (byte)'M' && magic[1] == (byte)'Z')
        {
            (sizeBytes, exportCount) = PortableExecutableInspector.Inspect(path);
        }
        else if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
        {
            (sizeBytes, exportCount) = ElfInspector.Inspect(path);
        }
        else
        {
            throw new InvalidDataException(
                $"ModuleInspector: '{path}' has unrecognized magic bytes " +
                $"{Convert.ToHexString(magic)}; expected PE (MZ..) or ELF (7F454C46).");
        }

        // 2. Stream the file once to compute SHA-256. Streaming keeps memory
        //    bounded even for very large client modules.
        byte[] hash;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            hash = SHA256.HashData(fs);
        }

        return new InspectionResult(path, sizeBytes, hash, exportCount);
    }
}
