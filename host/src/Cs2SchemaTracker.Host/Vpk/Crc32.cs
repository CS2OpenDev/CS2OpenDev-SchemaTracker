// CRC32 (IEEE 802.3, reflected, poly 0xEDB88320) for VPK entry verification.
//
// Implemented in-house rather than taking a dependency: System.IO.Hashing is NOT
// referenced by the host csproj, and we will not add a package solely for a 10-line
// table-driven checksum. This is the exact variant Valve's VPK format stores in each
// directory entry (zlib/PKZIP CRC-32). Pure BCL, compatible.

namespace Cs2SchemaTracker.Host.Vpk;

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint Poly = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? Poly ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    /// <summary>Compute the IEEE CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
