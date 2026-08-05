// VPK gameevents extraction round-trip against REAL shipped Valve bytes.
//
// The contract: load a fixture pak01_dir.vpk for each v1-scoped platform, assert
// the expected .gameevents files are extractable, and assert the byte content
// matches a pinned expected SHA-256.
//
// Unlike the synthetic fixtures in VpkArchiveTest.cs, the bodies here are the
// genuine resource/{game,mod}.gameevents extracted (CRC-verified) from a shipped
// CS2 content VPK (content depot 2347770). They are committed under
// Vpk/fixtures/resource/ and copied to the test output dir by the csproj.
//
// We assemble those real bytes into a pak01_dir.vpk that mirrors how CS2 actually
// ships the files: game.gameevents lives in an EXTERNAL pak01_000.vpk archive,
// mod.gameevents is EMBEDDED in _dir.vpk. We then drive the production
// VpkArchive parser end-to-end: list -> Find -> ReadEntryBytes (which also
// enforces the CRC32 stored in the directory tree), and assert the extracted
// bytes hash to the pinned SHA-256.
//
// Production already parses genuine shipped pak01_dir.vpk at scale: the
// gameevents backfill walked real VPKs across 244 builds. This is the
// committed-content regression that codifies extract-correctness + byte-exact
// SHA so a parser regression can never silently corrupt a gameevents artifact.

using System.Security.Cryptography;
using System.Text;

using Cs2SchemaTracker.Host.Vpk;

namespace Cs2SchemaTracker.Tests.Vpk;

public partial class VpkArchiveTest
{
    // Real shipped CS2 resource/*.gameevents (content depot 2347770), CRC-verified.
    private const string GameGameEventsSha256 =
        "03b665315a0dca6ac542d81d0348331da86a59c090fdee2ee42f2d90aabef5c6";
    private const string ModGameEventsSha256 =
        "8edd8c95eded67f907c4661376caff006be131090b48bea8e4da6d640eddfdbe";

    private const int GameGameEventsLength = 9837;
    private const int ModGameEventsLength = 20982;

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Vpk", "fixtures", "resource");

    private static byte[] LoadFixture(string name)
    {
        string path = Path.Combine(FixtureDir, name);
        Xunit.Assert.True(File.Exists(path), $"missing fixture: {path}");
        return File.ReadAllBytes(path);
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // Sanity: the committed fixtures themselves are byte-exact before we ever
    // round-trip them through the VPK container. If these drift, the corpus is
    // wrong, not the parser.
    [Xunit.Fact]
    public void Fixtures_Match_Pinned_Sha256_On_Disk()
    {
        byte[] game = LoadFixture("game.gameevents");
        byte[] mod = LoadFixture("mod.gameevents");

        Xunit.Assert.Equal(GameGameEventsLength, game.Length);
        Xunit.Assert.Equal(ModGameEventsLength, mod.Length);

        Xunit.Assert.Equal(GameGameEventsSha256, Sha256Hex(game));
        Xunit.Assert.Equal(ModGameEventsSha256, Sha256Hex(mod));
    }

    // The headline case for VPK version 2 (the version CS2 ships).
    // game.gameevents -> external pak01_000.vpk; mod.gameevents -> embedded _dir.vpk.
    [Xunit.Fact]
    public void V2_Extracts_Real_GameEvents_With_Pinned_Sha256()
    {
        RunRoundTrip(version: 2);
    }

    // v1 bonus case: same real bytes, VPK1 container (no data-section header).
    [Xunit.Fact]
    public void V1_Extracts_Real_GameEvents_With_Pinned_Sha256()
    {
        RunRoundTrip(version: 1);
    }

    private static void RunRoundTrip(int version)
    {
        byte[] gameBody = LoadFixture("game.gameevents");
        byte[] modBody = LoadFixture("mod.gameevents");

        // mod.gameevents embedded in _dir.vpk; game.gameevents external in _000.vpk.
        // Mirrors how real CS2 ships gameevents (external pak01_NNN archives), while
        // also exercising the embedded path in the same container.
        var dirBytes = BuildMixedGameEventsVpk(
            version,
            embedded: new FileSpec("resource", "gameevents", "mod", modBody),
            external: new FileSpec("resource", "gameevents", "game", gameBody),
            externalArchiveIndex: 0,
            externalBody: out byte[] externalArchiveBytes);

        var workDir = Path.Combine(Path.GetTempPath(), "vpk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            string dirPath = Path.Combine(workDir, "pak01_dir.vpk");
            File.WriteAllBytes(dirPath, dirBytes);
            File.WriteAllBytes(Path.Combine(workDir, "pak01_000.vpk"), externalArchiveBytes);

            var archive = VpkArchive.Open(dirPath);
            Xunit.Assert.Equal((uint)version, archive.Version);

            // (a) Both expected .gameevents files are present/extractable (list).
            var gameEventsPaths = archive.Entries
                .Where(e => e.FullPath.EndsWith(".gameevents", StringComparison.Ordinal))
                .Select(e => e.FullPath)
                .ToArray();
            Xunit.Assert.Equal(
                ["resource/game.gameevents", "resource/mod.gameevents"],
                gameEventsPaths);

            // (b) ReadEntryBytes output hashes to the pinned SHA-256.
            var gameEntry = archive.Find("resource/game.gameevents");
            var modEntry = archive.Find("resource/mod.gameevents");
            Xunit.Assert.NotNull(gameEntry);
            Xunit.Assert.NotNull(modEntry);

            byte[] gameExtracted = archive.ReadEntryBytes(gameEntry!);
            byte[] modExtracted = archive.ReadEntryBytes(modEntry!);

            // Byte-exact against the real fixture bytes...
            Xunit.Assert.Equal(gameBody, gameExtracted);
            Xunit.Assert.Equal(modBody, modExtracted);

            //...and against the pinned SHA-256 (the load-bearing assertion).
            Xunit.Assert.Equal(GameGameEventsSha256, Sha256Hex(gameExtracted));
            Xunit.Assert.Equal(ModGameEventsSha256, Sha256Hex(modExtracted));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    // Builds a pak01_dir.vpk with one embedded entry and one external entry, reusing
    // the same on-disk layout the production parser expects. CRC32 over each real
    // body is stored in the tree so ReadEntryBytes' CRC check exercises real bytes.
    private static byte[] BuildMixedGameEventsVpk(
        int version,
        FileSpec embedded,
        FileSpec external,
        ushort externalArchiveIndex,
        out byte[] externalBody)
    {
        externalBody = external.Body;

        var tree = new MemoryStream();
        var dataSection = new MemoryStream();

        // Both files share extension "gameevents" and path "resource"; emit them in a
        // single ext/path group with deterministic Name order so the parser's Ordinal
        // sort yields game.gameevents before mod.gameevents.
        WriteCString(tree, embedded.Ext); // "gameevents"
        WriteCString(tree, embedded.Path); // "resource"

        // -- external entry: game.gameevents --
        WriteCString(tree, external.Name);
        WriteU32(tree, Crc32(external.Body));
        WriteU16(tree, 0);                       // preload bytes
        WriteU16(tree, externalArchiveIndex);    // external archive (pak01_000.vpk)
        WriteU32(tree, 0);                        // offset within external archive
        WriteU32(tree, (uint)external.Body.Length);
        WriteU16(tree, Terminator);

        // -- embedded entry: mod.gameevents --
        uint embeddedOffset = (uint)dataSection.Length;
        dataSection.Write(embedded.Body);
        WriteCString(tree, embedded.Name);
        WriteU32(tree, Crc32(embedded.Body));
        WriteU16(tree, 0);                       // preload bytes
        WriteU16(tree, Embedded);                // 0x7FFF => body in _dir.vpk data section
        WriteU32(tree, embeddedOffset);          // offset within data section
        WriteU32(tree, (uint)embedded.Body.Length);
        WriteU16(tree, Terminator);

        tree.WriteByte(0); // end of files for this path
        tree.WriteByte(0); // end of paths for this extension
        tree.WriteByte(0); // end of extension list

        byte[] treeBytes = tree.ToArray();
        byte[] dataBytes = dataSection.ToArray();

        var ms = new MemoryStream();
        WriteU32(ms, Signature);
        WriteU32(ms, (uint)version);
        WriteU32(ms, (uint)treeBytes.Length);
        if (version == 2)
        {
            WriteU32(ms, (uint)dataBytes.Length); // FileDataSectionSize
            WriteU32(ms, 0);                        // ArchiveMd5SectionSize
            WriteU32(ms, 0);                        // OtherMd5SectionSize
            WriteU32(ms, 0);                        // SignatureSectionSize
        }
        ms.Write(treeBytes);
        ms.Write(dataBytes);
        return ms.ToArray();
    }
}
