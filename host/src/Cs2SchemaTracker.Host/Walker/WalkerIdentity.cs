// Walker identity chain, host side.
//
// The walker binary embeds its own git SHA + a content fingerprint (walker/src/version.h,
// printed by `<walker> --version`), but until this file existed NO host code ever read it:
// provenance.tool.git_commit only ever carried the HOST's own build SHA (ToolBuildInfo), never the
// WALKER's. That gap is how a mixed-vintage walker set (some eras rebuilt, some stale) and a stale
// Docker image both produced corpus-scale damage undetected (incident #8) — nothing compared what
// actually ran against what the operator believed was built.
//
// This is the ONE place that launches `<walker> --version` and parses its stdout. Two lines:
//   line 1 (pre-existing): "cs2-schema-walker <ver> (git <sha>, schema <ver>)"
//   line 2 (added with kWalkerSrcFingerprint; absent on an older binary): "src-fingerprint <64-hex>"
// A binary built before that line existed prints only line 1; SrcFingerprint then reads "unknown" —
// never guessed, never treated as a parse failure (a stale-but-honest binary is not garbage output).
//
// Resolve() is <1s (no CS2 binaries touched, just --version) and is called once per DISTINCT walker
// binary path per process (memoized — a batch over hundreds of builds sharing a handful of per-era
// binaries must not re-spawn --version per build).

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cs2SchemaTracker.Host.Walker;

/// <summary>
/// One walker binary's self-reported identity: its own semver, the git SHA it was built from
/// (<c>kWalkerGitSha</c>), and its content fingerprint (<c>kWalkerSrcFingerprint</c>;
/// <see cref="UnknownFingerprint"/> on an older binary that prints only line 1).
/// </summary>
internal sealed partial record WalkerIdentity(string Version, string GitSha, string SrcFingerprint)
{
    /// <summary>
    /// The fingerprint value when the resolved binary's <c>--version</c> output carries only line 1
    /// (it predates the <c>src-fingerprint</c> line). Never a guess — an explicit,
    /// recognizable "not reported" sentinel the identity gate treats as unverified.
    /// </summary>
    internal const string UnknownFingerprint = "unknown";

    // Line 1 is byte-stable by contract (walker/src/main.cpp RunVersion() — the original one-line
    // shape the per-era build harness already keys on); never change it without a coordinated
    // walker bump.
    // The git-sha token is NOT constrained to hex: version.h's WALKER_GIT_SHA fallback is the literal
    // string "unknown" on a no-git build, so the capture is any run of non-comma characters.
    [GeneratedRegex(@"^cs2-schema-walker\s+(?<ver>\S+)\s+\(git\s+(?<sha>[^,]+),\s*schema\s+(?<schema>\S+)\)\s*$")]
    private static partial Regex VersionLineRegex();

    /// <summary>
    /// Parse a walker's raw <c>--version</c> stdout into a <see cref="WalkerIdentity"/>. Fail-loud on
    /// an unparseable line 1 (never guess what a garbage/corrupt/wrong-binary output means) — an
    /// absent or unrecognized line 2 is NOT a failure, it degrades to <see cref="UnknownFingerprint"/>
    /// (the documented one-line shape).
    /// </summary>
    internal static WalkerIdentity Parse(string versionOutput)
    {
        ArgumentNullException.ThrowIfNull(versionOutput);

        var lines = versionOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var line1 = lines.Length > 0 ? lines[0].Trim() : "";
        var match = VersionLineRegex().Match(line1);
        if (!match.Success)
        {
            throw new InvalidDataException(
                "WalkerIdentity: unparseable walker '--version' output. Expected line 1 to match " +
                "'cs2-schema-walker <ver> (git <sha>, schema <ver>)', got: " +
                $"'{line1}' (full output: '{versionOutput}').");
        }

        string version = match.Groups["ver"].Value;
        string gitSha = match.Groups["sha"].Value.Trim();

        // Line 2: "src-fingerprint <64-hex>". Absent (older binary) or unrecognized ⇒
        // UnknownFingerprint — never a parse failure; line 1 alone is a complete, valid contract.
        string srcFingerprint = UnknownFingerprint;
        for (int i = 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            const string prefix = "src-fingerprint ";
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                var token = trimmed[prefix.Length..].Trim();
                if (token.Length > 0)
                {
                    srcFingerprint = token;
                }
            }
            break;   // the walker prints src-fingerprint immediately after line 1 or not at all.
        }

        return new WalkerIdentity(version, gitSha, srcFingerprint);
    }

    // Memoized per resolved absolute binary path: a batch run resolves the SAME per-era binary once
    // per build sharing that era, and re-spawning `--version` per build would be pure waste (the
    // identity of a given file on disk cannot change mid-process). Not a determinism concern — this
    // cache never reaches an artifact byte, it only avoids redundant subprocess launches.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WalkerIdentity> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Launch <c>&lt;walkerBinaryPath&gt; --version</c> and parse its identity. Fail-loud
    /// (<see cref="FileNotFoundException"/>) when the binary is missing, or
    /// <see cref="InvalidOperationException"/> when the subprocess exits non-zero — both mirror
    /// <see cref="WalkerProcessRunner"/>'s own "the walker is a hard dependency, never guess" stance.
    /// &lt;1s: no CS2 binaries are touched, this is exactly the `--version` self-report.
    /// </summary>
    internal static WalkerIdentity Resolve(string walkerBinaryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(walkerBinaryPath);
        var full = Path.GetFullPath(walkerBinaryPath);

        if (Cache.TryGetValue(full, out var cached))
        {
            return cached;
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"WalkerIdentity: walker binary not found at '{full}' — cannot resolve its identity.", full);
        }

        // Mirrors WalkerProcessRunner.EnsureExecutable: a bundle unpacked from a .zip carries no Unix
        // mode, so a freshly-installed era binary can land 0644 (non-executable) on Linux.
        WalkerProcessRunner.EnsureExecutable(full);

        var psi = new ProcessStartInfo
        {
            FileName = full,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--version");

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"WalkerIdentity: '{full} --version' exited {proc.ExitCode}. stderr: {stderr}");
        }

        var identity = Parse(stdout);
        Cache[full] = identity;
        return identity;
    }
}
