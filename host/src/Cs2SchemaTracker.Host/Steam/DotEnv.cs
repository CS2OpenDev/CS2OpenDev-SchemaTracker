// Minimal, dependency-free .env loader.
//
// Why hand-rolled: we deliberately take NO new dependency (DotNetEnv etc.) for a
// 30-line KEY=VALUE parser. This mirrors the bootstrap script's Get-EnvFile so a
// single repo-root `.env` works for both the PowerShell bootstrap and the host.
//
// CREDENTIAL HYGIENE: this loader only ever COPIES values from the .env file into
// the process environment. It NEVER logs, prints, or returns the VALUES. Callers
// read secrets exclusively via Environment.GetEnvironmentVariable at the point of
// use. `.env` is gitignored; this code must keep secrets out of every transcript.
//
// Precedence: an already-set process env var WINS — the loader never clobbers a
// var the operator (or CI) already exported. This is what lets CI inject
// STEAM_USERNAME / STEAM_PASSWORD via secrets without a committed .env, while a
// local dev run picks them up from the gitignored file.

namespace Cs2SchemaTracker.Host.Steam;

internal static class DotEnv
{
    /// <summary>
    /// Walk up from <paramref name="startDir"/> (default: current directory) to
    /// find a repo-root <c>.env</c> and, for each KEY=VALUE line, set the process
    /// environment variable IF it is not already set. No-op when no <c>.env</c> is
    /// found. Returns the number of variables newly set (NOT their names+values —
    /// callers must not surface what was loaded).
    /// </summary>
    public static int LoadFromRepoRoot(string? startDir = null)
    {
        var envPath = FindEnvFile(startDir ?? Directory.GetCurrentDirectory());
        if (envPath is null)
        {
            return 0;
        }
        return LoadFile(envPath);
    }

    /// <summary>
    /// Locate the nearest <c>.env</c> walking up from <paramref name="startDir"/>.
    /// Anchors on the directory containing a <c>.git</c> entry if found first, but
    /// returns the first <c>.env</c> encountered regardless. Null if none.
    /// </summary>
    internal static string? FindEnvFile(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            // Stop climbing once we pass the repo root (.git present but no .env).
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return null;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Parse a KEY=VALUE file and set each var in the process env when not already
    /// present. Mirrors the bootstrap's Get-EnvFile semantics: skip blank lines and
    /// <c>#</c> comments, strip one layer of matching single/double quotes, ignore
    /// empty values. Returns the count newly set.
    /// </summary>
    internal static int LoadFile(string path)
    {
        int set = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            // Allow an optional leading "export ".
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line.Substring("export ".Length).TrimStart();
            }
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            var key = line.Substring(0, eq).Trim();
            if (!IsValidKey(key))
            {
                continue;
            }
            var value = line.Substring(eq + 1).Trim();
            value = StripMatchingQuotes(value);
            if (value.Length == 0)
            {
                continue;
            }
            // Precedence: never clobber an already-set var.
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
                set++;
            }
        }
        return set;
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0)
        {
            return false;
        }
        char first = key[0];
        if (!(char.IsLetter(first) || first == '_'))
        {
            return false;
        }
        foreach (var c in key)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }
        return true;
    }

    private static string StripMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value.Substring(1, value.Length - 2);
        }
        return value;
    }

    /// <summary>
    /// Convenience: read a required secret from the env (after <see cref="LoadFromRepoRoot"/>
    /// has run). Returns null if unset/blank. Never logs the value.
    /// </summary>
    internal static string? GetSecret(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
