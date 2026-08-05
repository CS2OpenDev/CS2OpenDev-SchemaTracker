// VerifyNativesCommand tests — the native-bundle completeness validator.
//
// Builds a synthetic inventory (2 compile-pin eras + 1 runtime-variant) and a natives/ tree, and
// asserts verify-natives passes only when every compile-pin era binary is present for BOTH platforms
// (windows .exe, linux bare-named), excluding the runtime-variant era.

using Cs2SchemaTracker.Host.Cli;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("console-capturing")]
public class VerifyNativesCommandTest
{
    private const string Inventory = """
    {
      "eras": [
        { "era": "cs2-a", "kind": "compile-pin", "hl2sdkSha": "aaaa" },
        { "era": "cs2-b", "kind": "compile-pin", "hl2sdkSha": "bbbb" },
        { "era": "cs2-variant", "kind": "runtime-variant", "ridesCompilePin": "bbbb" }
      ],
      "builds": []
    }
    """;

    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "verifynatives-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteInventory(string root)
    {
        var path = Path.Combine(root, "inv.json");
        File.WriteAllText(path, Inventory.Replace("\r\n", "\n"));
        return path;
    }

    /// <summary>Create a natives/ tree with the two compile-pin eras present for both platforms.</summary>
    private static string CompleteNatives(string root)
    {
        var natives = Path.Combine(root, "natives");
        foreach (var (platform, suffix) in new[] { ("windows-x86_64", ".exe"), ("linux-x86_64", "") })
        {
            var dir = Path.Combine(natives, platform);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "cs2-a" + suffix), "");
            File.WriteAllText(Path.Combine(dir, "cs2-b" + suffix), "");
        }
        return natives;
    }

    private static (int Code, string Err) Run(string natives, string inventory)
    {
        var stderr = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(stderr);
        try
        { return (VerifyNativesCommand.Run(new[] { "--natives", natives, "--inventory", inventory }), stderr.ToString()); }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }

    [Fact]
    public void Complete_Natives_For_Both_Platforms_Passes()
    {
        var root = NewRoot();
        var (code, _) = Run(CompleteNatives(root), WriteInventory(root));
        Assert.Equal(0, code);
    }

    [Fact]
    public void Missing_Era_Binary_Fails_Loud_65()
    {
        var root = NewRoot();
        var natives = CompleteNatives(root);
        File.Delete(Path.Combine(natives, "windows-x86_64", "cs2-b.exe"));

        var (code, err) = Run(natives, WriteInventory(root));
        Assert.Equal(65, code);
        Assert.Contains("windows-x86_64/cs2-b.exe", err);
    }

    [Fact]
    public void Missing_Platform_Dir_Fails_Loud_65()
    {
        var root = NewRoot();
        var natives = CompleteNatives(root);
        Directory.Delete(Path.Combine(natives, "linux-x86_64"), recursive: true);

        var (code, err) = Run(natives, WriteInventory(root));
        Assert.Equal(65, code);
        Assert.Contains("linux-x86_64/", err);
    }

    [Fact]
    public void Missing_Natives_Arg_Is_Usage_Error()
    {
        var stderr = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(stderr);
        int code;
        try
        { code = VerifyNativesCommand.Run(Array.Empty<string>()); }
        finally { Console.SetError(prevErr); }
        Assert.Equal(64, code);
    }
}
