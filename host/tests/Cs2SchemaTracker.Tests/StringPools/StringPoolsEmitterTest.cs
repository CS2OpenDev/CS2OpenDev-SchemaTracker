// StringPoolsEmitter unit tests (synthetic WalkerOutput).
//

using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.StringPools;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.StringPools;

public class StringPoolsEmitterTest
{
    private const string Platform = "linux-x86_64";

    private static WalkerOutput WalkWith(params StringPool[] pools)
    {
        var walk = new StringPoolsWalk();
        walk.Pools.AddRange(pools);
        return new WalkerOutput { Platform = Platform, StringPools = walk };
    }

    private static StringPool Pool(string name, params string[] entries)
    {
        var p = new StringPool { Name = name };
        p.Entries.AddRange(entries);
        return p;
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "stringpools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Lifts_Faithfully_Sorts_Pools_And_Dedupes_And_Sorts_Entries()
    {
        var dir = NewDir();
        try
        {
            // Pools out-of-order (sym before files lexically: 'CUtlSymbol...' > 'CUtlFile...').
            // sym has a duplicate + unsorted entries — the emitter dedupes and sorts.
            var walk = WalkWith(
                Pool("CUtlSymbolLarge", "m_vecOrigin", "m_iHealth", "m_iHealth"),
                Pool("CUtlFilenameSymbolTable", "b.vmat", "a.vmat"));

            var outPath = Path.Combine(dir, "string_pools.json");
            new StringPoolsEmitter(SchemaFamily.Version, "b1", Platform).Emit(walk, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var pools = doc.RootElement.GetProperty("pools");
            Assert.Equal(2, pools.GetArrayLength());

            // Pools sorted by name: CUtlFilenameSymbolTable before CUtlSymbolLarge.
            Assert.Equal("CUtlFilenameSymbolTable", pools[0].GetProperty("name").GetString());
            var files = pools[0].GetProperty("entries");
            Assert.Equal(2, files.GetArrayLength());
            Assert.Equal("a.vmat", files[0].GetString());
            Assert.Equal("b.vmat", files[1].GetString());

            Assert.Equal("CUtlSymbolLarge", pools[1].GetProperty("name").GetString());
            var sym = pools[1].GetProperty("entries");
            // Deduped (m_iHealth appears once) and sorted.
            Assert.Equal(2, sym.GetArrayLength());
            Assert.Equal("m_iHealth", sym[0].GetString());
            Assert.Equal("m_vecOrigin", sym[1].GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_Runs_Byte_Identical()
    {
        var dir = NewDir();
        try
        {
            var walk = WalkWith(
                Pool("Z", "c", "a", "b", "a"),
                Pool("A", "two", "one"));

            var pa = Path.Combine(dir, "a.json");
            var pb = Path.Combine(dir, "b.json");
            new StringPoolsEmitter(SchemaFamily.Version, "b", Platform).Emit(walk, pa);
            new StringPoolsEmitter(SchemaFamily.Version, "b", Platform).Emit(walk, pb);
            Assert.Equal(File.ReadAllBytes(pa), File.ReadAllBytes(pb));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Missing_Walk()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "string_pools.json");
            Assert.Throws<InvalidDataException>(() =>
                new StringPoolsEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(new WalkerOutput { Platform = Platform }, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Empty_Pool_Name_Writes_Nothing()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "string_pools.json");
            Assert.Throws<InvalidDataException>(() =>
                new StringPoolsEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(WalkWith(Pool("", "x")), outPath));
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Empty_Entry_Writes_Nothing()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "string_pools.json");
            Assert.Throws<InvalidDataException>(() =>
                new StringPoolsEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(WalkWith(Pool("CUtlSymbolLarge", "ok", "")), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
