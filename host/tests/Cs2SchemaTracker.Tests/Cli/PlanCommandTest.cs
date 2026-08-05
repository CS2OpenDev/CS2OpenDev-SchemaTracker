// PlanCommand tests — the host-owned build-target selector (`plan`).
//
// Drives PlanCommand.Run over a synthetic inventory and asserts the two projections match the
// exact selection the build/validate/bundle scripts used to derive by hand:
//   - validation:   every era's oldest+newest build_id (deduped for single-build eras), era-sorted.
//   - compile-pins: compile-pin eras only (runtime-variant excluded), per-platform layoutSignature.
// Also covers tsv/json shape and the usage-error exits.

using System.Text.Json;

using Cs2SchemaTracker.Host.Cli;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("console-capturing")]
public class PlanCommandTest
{
    private static readonly string[] ExpectedValidationRows = { "100\tcs2-a-older", "300\tcs2-a-older", "500\tcs2-b-newer" };
    private static readonly string[] ExpectedCompilePinLinuxRows = { "cs2-b-newer\tbbbb\tsig-b-linux", "cs2-a-older\taaaa\t" };

    // Two compile-pin eras (one with both platform signatures, one linux-only) + one runtime-variant
    // era that must NEVER appear in compile-pins. Builds: era A has three builds (oldest+newest
    // selected, middle dropped), era B has one (single row), era C rides A at runtime.
    private const string Inventory = """
    {
      "app": { "app_id": 730 },
      "eras": [
        {
          "era": "cs2-b-newer",
          "kind": "compile-pin",
          "hl2sdkSha": "bbbb",
          "layoutSignatures": { "windows-x86_64": "sig-b-win", "linux-x86_64": "sig-b-linux" },
          "minClasses": 100,
          "maxClasses": 200
        },
        {
          "era": "cs2-a-older",
          "kind": "compile-pin",
          "hl2sdkSha": "aaaa",
          "layoutSignatures": { "windows-x86_64": "sig-a-win" }
        },
        {
          "era": "cs2-c-variant",
          "kind": "runtime-variant",
          "ridesCompilePin": "bbbb",
          "variantSignature": "sig-c"
        }
      ],
      "builds": [
        { "build_id": 300, "era": "cs2-a-older" },
        { "build_id": 100, "era": "cs2-a-older" },
        { "build_id": 200, "era": "cs2-a-older" },
        { "build_id": 500, "era": "cs2-b-newer" }
      ]
    }
    """;

    private static string NewInventory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cs2-assets-inventory.json");
        File.WriteAllText(path, Inventory.Replace("\r\n", "\n"));
        return path;
    }

    private static (int Code, string Out) RunCapture(params string[] args)
    {
        var stdout = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(new StringWriter());
        try
        { return (PlanCommand.Run(args), stdout.ToString()); }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }

    [Fact]
    public void Validation_Tsv_Is_OldestPlusNewest_Per_Era_EraSorted_Deduped()
    {
        var inv = NewInventory();
        var (code, output) = RunCapture("--targets", "validation", "--format", "tsv", "--inventory", inv);

        Assert.Equal(0, code);
        var rows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // era-sorted ascending: cs2-a-older (oldest 100, newest 300) then cs2-b-newer (single 500).
        Assert.Equal(ExpectedValidationRows, rows);
    }

    [Fact]
    public void Validation_Json_Carries_Position_And_Build()
    {
        var inv = NewInventory();
        var (code, output) = RunCapture("--targets", "validation", "--inventory", inv);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        var arr = doc.RootElement;
        Assert.Equal(3, arr.GetArrayLength());
        Assert.Equal(100, arr[0].GetProperty("build_id").GetInt64());
        Assert.Equal("oldest", arr[0].GetProperty("position").GetString());
        Assert.Equal(300, arr[1].GetProperty("build_id").GetInt64());
        Assert.Equal("newest", arr[1].GetProperty("position").GetString());
        // Single-build era emits exactly one (oldest) row — no duplicate newest.
        Assert.Equal("oldest", arr[2].GetProperty("position").GetString());
    }

    [Fact]
    public void CompilePins_Excludes_RuntimeVariant_And_Scopes_Signature_To_Platform()
    {
        var inv = NewInventory();
        var (code, output) = RunCapture("--targets", "compile-pins", "--platform", "linux-x86_64", "--format", "tsv", "--inventory", inv);

        Assert.Equal(0, code);
        var rows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Only the 2 compile-pin eras (runtime-variant excluded); inventory order preserved.
        // Era A has no linux signature -> empty third column (the bash builder skips those).
        Assert.Equal(ExpectedCompilePinLinuxRows, rows);
    }

    [Fact]
    public void CompilePins_Json_Without_Platform_Emits_Full_Signature_Map()
    {
        var inv = NewInventory();
        var (code, output) = RunCapture("--targets", "compile-pins", "--inventory", inv);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        var arr = doc.RootElement;
        Assert.Equal(2, arr.GetArrayLength());
        var b = arr[0];
        Assert.Equal("cs2-b-newer", b.GetProperty("era").GetString());
        Assert.Equal("sig-b-win", b.GetProperty("layoutSignatures").GetProperty("windows-x86_64").GetString());
        Assert.Equal("sig-b-linux", b.GetProperty("layoutSignatures").GetProperty("linux-x86_64").GetString());
        Assert.Equal(100, b.GetProperty("minClasses").GetInt32());
    }

    [Fact]
    public void CompilePins_Tsv_Without_Platform_Is_Usage_Error()
    {
        var inv = NewInventory();
        var (code, _) = RunCapture("--targets", "compile-pins", "--format", "tsv", "--inventory", inv);
        Assert.Equal(64, code);
    }

    [Fact]
    public void Missing_Targets_Is_Usage_Error()
    {
        var inv = NewInventory();
        var (code, _) = RunCapture("--inventory", inv);
        Assert.Equal(64, code);
    }

    [Fact]
    public void Unknown_Targets_Is_Usage_Error()
    {
        var inv = NewInventory();
        var (code, _) = RunCapture("--targets", "bogus", "--inventory", inv);
        Assert.Equal(64, code);
    }

    [Fact]
    public void Missing_Inventory_Fails_Loud()
    {
        var missing = Path.Combine(Path.GetTempPath(), "plan-missing-" + Guid.NewGuid().ToString("N"), "x.json");
        Assert.Throws<InvalidDataException>(() => RunCapture("--targets", "validation", "--inventory", missing));
    }

    [Fact]
    public void Help_Flag_Exits_Zero()
    {
        var (code, _) = RunCapture("--help");
        Assert.Equal(0, code);
    }
}
