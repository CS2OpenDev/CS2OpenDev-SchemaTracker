// Standalone emit of the tools-content artifacts (`emit-tools-content`).
//
// Runs the SAME CsScriptApiEmitter / EntityIoEmitter extract runs, without a walk: for backfilling
// cs_script_api.json / entity_io.json into a set that predates them, and for development against a
// local CS2 install with the Workshop Tools.
//
// Surface:
//   emit-tools-content --build <id> [--platform windows-x86_64] [--out <dir>]
//                      [--tools-content-root <dir>]
//   --platform  defaults to windows-x86_64, the only platform the tools depot exists for; any
//               other value is a usage error.
//   --out       output DIRECTORY (default artifacts/<id>/<platform>/). The two files are written
//               into it; nothing else in it is touched.
//   --tools-content-root
//               read the loose files from this tree (depot layout). Default: the build's tuple
//               dir, resolved like extract does, gated on its tools-content.json marker.
//
// Absence is reported, not recorded: this command writes no omissions.json and no provenance —
// extract owns both. Exit 0 when every present source emitted, 65 when there is no usable tree
// or a source is malformed, 64 on usage errors.

using Cs2SchemaTracker.Host.CsScript;
using Cs2SchemaTracker.Host.EntityIo;

namespace Cs2SchemaTracker.Host.Cli;

internal static class EmitToolsContentCommand
{
    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }
        var parsed = CliArgs.Parse(args);

        if (!parsed.TryGetValue("build", out var build) || string.IsNullOrEmpty(build))
        {
            Console.Error.WriteLine("emit-tools-content: --build <id> is required.");
            return 64;
        }
        parsed.TryGetValue("platform", out var platform);
        platform = string.IsNullOrEmpty(platform) ? ExtractCommand.ToolsPlatform : platform;
        if (!string.Equals(platform, ExtractCommand.ToolsPlatform, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"emit-tools-content: the Workshop Tools depot exists for {ExtractCommand.ToolsPlatform} only (got --platform {platform}).");
            return 64;
        }
        parsed.TryGetValue("out", out var outDir);
        outDir = Path.GetFullPath(string.IsNullOrEmpty(outDir) ? ExtractCommand.DefaultOutDir(build, platform) : outDir);
        parsed.TryGetValue("tools-content-root", out var root);

        try
        {
            ToolsContent.ToolsContentTree? tree;
            string note;
            if (!string.IsNullOrEmpty(root))
            {
                tree = ExtractCommand.ResolveToolsContent("", root, out note);
            }
            else if (ExtractCommand.TryResolveBinariesDir(build, platform, out var binariesDir, out var resolveError))
            {
                tree = ExtractCommand.ResolveToolsContent(binariesDir, null, out note);
            }
            else
            {
                Console.Error.WriteLine(resolveError);
                return 65;
            }
            if (tree is null)
            {
                Console.Error.WriteLine($"emit-tools-content: no usable tools content — {note}.");
                return 65;
            }
            Console.Error.WriteLine($"emit-tools-content: {note}.");

            Directory.CreateDirectory(outDir);
            int written = 0;
            if (CsScriptApiEmitter.HasSource(tree))
            {
                new CsScriptApiEmitter(SchemaFamily.Version, build, platform)
                    .Emit(tree, Path.Combine(outDir, "cs_script_api.json"));
                Console.Error.WriteLine($"emit-tools-content: wrote {Path.Combine(outDir, "cs_script_api.json")}");
                written++;
            }
            else
            {
                Console.Error.WriteLine($"emit-tools-content: {CsScriptApiEmitter.DeclarationPath} absent — cs_script_api.json not written.");
            }
            if (EntityIoEmitter.HasSource(tree))
            {
                new EntityIoEmitter(SchemaFamily.Version, build, platform)
                    .Emit(tree, Path.Combine(outDir, "entity_io.json"));
                Console.Error.WriteLine($"emit-tools-content: wrote {Path.Combine(outDir, "entity_io.json")}");
                written++;
            }
            else
            {
                Console.Error.WriteLine($"emit-tools-content: {EntityIoEmitter.RootPath} absent — entity_io.json not written.");
            }
            return written > 0 ? 0 : 65;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"emit-tools-content: {ex.GetType().Name}: {ex.Message}");
            return 65;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Usage: cs2-schema-tracker emit-tools-content --build <id> [--platform windows-x86_64]
                                                         [--out <dir>] [--tools-content-root <dir>]

            Emit cs_script_api.json (from point_script.d.ts) and entity_io.json (from the Hammer
            FGDs) without a walk. Reads the build's tuple dir (gated on tools-content.json), or the
            tree named by --tools-content-root (e.g. a local CS2 install root). Writes into --out
            (default artifacts/<id>/<platform>/); records no omissions and no provenance.
            """);
    }
}
