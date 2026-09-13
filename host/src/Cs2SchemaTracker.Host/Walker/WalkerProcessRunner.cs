// real walker-subprocess runner.
//
// Launches the C++ walker binary (walker/) as a subprocess and runs its `walk`
// subcommand. The walker binary path is RESOLVED, never hardcoded absolute, because
// the walker may not be built on a given host yet:
//   1. $CS2_WALKER_BIN — explicit override (CI sets this to the built artifact).
//   2. else a sensible default under walker/build relative to the host executable's
//      repo, by platform-specific filename (cs2_schema_walker[.exe]).
//
// fail-loud: this runner does NOT interpret the walker's exit code — it returns
// it verbatim along with captured stderr, and the ExtractCommand orchestration decides.
// A non-zero exit (including 75 = unknown layout) is surfaced, not swallowed.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cs2SchemaTracker.Host.Walker;

/// <summary>
/// <see cref="IWalkerRunner"/> backed by launching the real walker executable. The
/// binary path is resolved from <c>CS2_WALKER_BIN</c> or a default under <c>walker/build</c>;
/// it is never a hardcoded absolute path (portability).
/// </summary>
internal sealed class WalkerProcessRunner : IWalkerRunner
{
    /// <summary>Environment variable that overrides the walker binary path.</summary>
    public const string BinaryPathEnvVar = "CS2_WALKER_BIN";

    /// <summary>
    /// Environment variable the walker reads to opt OUT of the live MGetKV3ClassDefaults recovery.
    /// The walker tests its PRESENCE and not its value (schema_walk.cpp MaybeResolveSaveKv3Json:
    /// <c>std::getenv("CS2_WALKER_NO_KV3_DEFAULTS") != nullptr</c>), so ANY value — <c>"0"</c>
    /// included — disables the recovery. That is why the host must REMOVE an inherited one rather
    /// than merely refrain from setting it; see <see cref="ApplyKv3DefaultsGate"/>.
    /// </summary>
    public const string DisableKv3DefaultsEnvVar = "CS2_WALKER_NO_KV3_DEFAULTS";

    private readonly string? _explicitBinaryPath;

    /// <summary>
    /// When true, the walker is launched with <c>CS2_WALKER_NO_KV3_DEFAULTS</c> set so it does NOT
    /// attempt the MGetKV3ClassDefaults live-recovery accessor call. Set by the host for eras whose
    /// KV3 accessor ABI is not validated (<see cref="InventoryEra.Kv3ClassDefaults"/> == false): on
    /// those eras the call yields nothing on windows and CRASHES on linux, so we emit empty
    /// (deferred-with-reason) instead. See EraWalkerResolver / the walker's MaybeResolveSaveKv3Json.
    /// When false the host explicitly REMOVES the variable from the child's environment rather than
    /// letting an inherited one through — the era, not the ambient shell, decides.
    /// </summary>
    private readonly bool _disableKv3Defaults;

    /// <summary>
    /// Default runner: resolves the walker binary from <c>CS2_WALKER_BIN</c> or the
    /// <c>walker/build</c> default (single-era, back-compat).
    /// </summary>
    public WalkerProcessRunner()
    {
    }

    /// <summary>
    /// Per-era runner: launch the explicitly-resolved <paramref name="explicitBinaryPath"/>
    /// (chosen by <see cref="EraWalkerResolver"/>). <c>CS2_WALKER_BIN</c> STILL wins over it
    /// (an operator override bypasses era selection). <paramref name="disableKv3Defaults"/> gates
    /// off the KV3 class-defaults recovery for eras whose accessor ABI is not validated.
    /// </summary>
    public WalkerProcessRunner(string explicitBinaryPath, bool disableKv3Defaults = false)
    {
        _explicitBinaryPath = explicitBinaryPath;
        _disableKv3Defaults = disableKv3Defaults;
    }

    public int Run(string binariesDir, string platform, string outPath, out string stderr)
    {
        ArgumentException.ThrowIfNullOrEmpty(binariesDir);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        ArgumentException.ThrowIfNullOrEmpty(outPath);

        var walkerBin = ResolveWalkerBinary(_explicitBinaryPath);
        if (!File.Exists(walkerBin))
        {
            // Fail loud: the walker is a hard dependency of a real extract run. We do not
            // fall back or guess. Tests inject a fake runner and never
            // reach this path.
            throw new FileNotFoundException(
                $"walker binary not found at '{walkerBin}'. Set {BinaryPathEnvVar} to the built " +
                "cs2_schema_walker executable, or build the walker (walker/build). " +
                "The host cannot extract without the walker.",
                walkerBin);
        }

        // A bundle unpacked from a .zip carries no Unix mode, so the era binaries land as 0644
        // (non-executable) on a real Linux box. Make the resolved walker executable before launch.
        EnsureExecutable(walkerBin);

        var psi = new ProcessStartInfo
        {
            FileName = walkerBin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("walk");
        psi.ArgumentList.Add("--binaries");
        psi.ArgumentList.Add(binariesDir);
        psi.ArgumentList.Add("--platform");
        psi.ArgumentList.Add(platform);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outPath);

        // Era-gated: for eras whose MGetKV3ClassDefaults accessor ABI is not validated, tell the
        // walker to SKIP the live KV3 recovery (emit empty). Set on BOTH platforms so the artifact
        // is identical cross-platform: on those eras windows recovers nothing anyway and linux would
        // CRASH calling the invalid accessor. Deterministic (a fixed env → the walker's
        // MaybeResolveSaveKv3Json early-returns → every MGetKV3ClassDefaults value stays empty).
        ApplyKv3DefaultsGate(psi, _disableKv3Defaults);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // The walker dlopen's the CS2 Source2 .so's, which resolve their OWN deps (libv8.so,
            // tier0, etc.) from the build's CS2 bin dirs. On Linux the dynamic loader does NOT add a
            // dlopen'd module's own dir to that search (unlike Windows), so we prepend the build's two
            // CS2 .so dirs to LD_LIBRARY_PATH — making a standalone `extract` work without the caller
            // pre-setting it. The walker's OWN libprotobuf.so is still resolved via its $ORIGIN
            // RUNPATH (searched after LD_LIBRARY_PATH), so this does not perturb it.
            var so1 = Path.Combine(binariesDir, "game", "bin", "linuxsteamrt64");
            var so2 = Path.Combine(binariesDir, "game", "csgo", "bin", "linuxsteamrt64");
            var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            var prepend = $"{so1}:{so2}";
            psi.Environment["LD_LIBRARY_PATH"] =
                string.IsNullOrEmpty(existing) ? prepend : $"{prepend}:{existing}";
        }

        using var proc = new Process { StartInfo = psi };
        var stderrBuilder = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderrBuilder.AppendLine(e.Data);
        };

        proc.Start();
        proc.BeginErrorReadLine();
        // Drain stdout so the child never blocks on a full pipe; the walker writes its
        // payload to --out, not stdout, so we discard stdout text.
        _ = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        stderr = stderrBuilder.ToString();
        return proc.ExitCode;
    }

    /// <summary>
    /// Resolve the walker executable path. Precedence:
    ///   1. <c>CS2_WALKER_BIN</c> — operator override, bypasses era selection.
    ///   2. <paramref name="explicitBinaryPath"/> — the per-era binary chosen by
    ///      <see cref="EraWalkerResolver"/> (when the caller resolved one).
    ///   3. the <c>walker/build</c> default for the current platform (single-era back-compat).
    /// Never a hardcoded absolute path.
    /// </summary>
    internal static string ResolveWalkerBinary(string? explicitBinaryPath = null)
    {
        // Effective override: CS2_WALKER_BIN env (live) wins, else appsettings WalkerBin.
        var overridePath = Cs2SchemaTracker.Host.Config.HostConfig.WalkerBin;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }
        if (!string.IsNullOrWhiteSpace(explicitBinaryPath))
        {
            return Path.GetFullPath(explicitBinaryPath);
        }
        return DefaultWalkerBinary();
    }

    /// <summary>
    /// On Unix, ensure the resolved walker binary carries the execute bits before launch. A
    /// bundle unpacked from a .zip stores no Unix mode, so the shipped era binaries arrive as
    /// 0644 and <see cref="Process.Start()"/> would fail with EACCES. Idempotent; a no-op on
    /// Windows and whenever the bits are already set. Internal (not private): <see cref="WalkerIdentity"/>
    /// launches the same binaries for `--version` and needs the identical guard.
    /// </summary>
    internal static void EnsureExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        var mode = File.GetUnixFileMode(path);
        var want = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if (want != mode)
            File.SetUnixFileMode(path, want);
    }

    /// <summary>
    /// Make the host authoritative over <see cref="DisableKv3DefaultsEnvVar"/> in BOTH directions:
    /// set it when the era needs the opt-out, and REMOVE it when the era does not. The removal is
    /// the point. The walker opts out on the variable's mere presence, and this process routinely
    /// carries an inherited value — an operator's exported shell var, or any key in the repo .env
    /// (Program.cs loads it wholesale via DotEnv.LoadFromRepoRoot, and DotEnv.LoadFile sets every
    /// syntactically valid key, not just the STEAM_* ones it exists to carry). Without the Remove
    /// that value passed straight through on <c>kv3ClassDefaults=true</c> eras and emptied every
    /// MGetKV3ClassDefaults value on the one path where the host believes it decides — and an
    /// inherited <c>CS2_WALKER_NO_KV3_DEFAULTS=0</c> reads to a human as "leave the recovery on"
    /// while doing the exact opposite. Mechanically: touching <see cref="ProcessStartInfo.Environment"/>
    /// seeds the dictionary from THIS process's environment, so the inherited entry is present and
    /// <c>Remove</c> genuinely drops it from the child's block; it does not disturb the host's own
    /// environment (a batch run reuses this process era after era). Internal (not private): the test
    /// suite exercises this directly via InternalsVisibleTo — <see cref="Run"/> itself cannot be
    /// unit-tested without a real walker binary.
    /// </summary>
    internal static void ApplyKv3DefaultsGate(ProcessStartInfo psi, bool disableKv3Defaults)
    {
        if (disableKv3Defaults)
        {
            psi.Environment[DisableKv3DefaultsEnvVar] = "1";
        }
        else
        {
            psi.Environment.Remove(DisableKv3DefaultsEnvVar);
        }
    }

    private static string DefaultWalkerBinary()
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cs2_schema_walker.exe"
            : "cs2_schema_walker";

        // Walk up from the host executable directory to a repo root that contains a
        // `walker/` sibling, then look under walker/build (and the common MSVC
        // Release subdir). Best-effort: if nothing is found we return the most likely
        // path so the not-found error message is informative.
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var walkerBuild = Path.Combine(dir.FullName, "walker", "build");
            if (Directory.Exists(walkerBuild))
            {
                var direct = Path.Combine(walkerBuild, exeName);
                if (File.Exists(direct))
                    return direct;
                var release = Path.Combine(walkerBuild, "Release", exeName);
                if (File.Exists(release))
                    return release;
                return direct; // informative default for the not-found message
            }
            dir = dir.Parent;
        }
        return Path.Combine(baseDir, "walker", "build", exeName);
    }
}
