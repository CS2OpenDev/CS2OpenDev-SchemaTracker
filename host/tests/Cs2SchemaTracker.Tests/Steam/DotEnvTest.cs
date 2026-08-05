// .env loader tests.
//
// Verifies the hand-rolled KEY=VALUE loader:
//   - parses keys/values, strips quotes, skips comments/blanks/`export `
//   - NEVER clobbers an already-set process env var (precedence)
//   - ignores malformed keys + empty values
//
// CREDENTIAL HYGIENE: these tests use synthetic non-secret keys (CS2TEST_*), set
// and clear them explicitly, and never touch STEAM_USERNAME / STEAM_PASSWORD.

using System;
using System.IO;

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class DotEnvTest
{
    private static string WriteTempEnv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2-dotenv-" + Guid.NewGuid().ToString("N") + ".env");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Parses_keys_strips_quotes_and_skips_comments()
    {
        var path = WriteTempEnv(
            "# a comment\n" +
            "\n" +
            "CS2TEST_PLAIN=hello\n" +
            "CS2TEST_DQUOTE=\"quoted value\"\n" +
            "CS2TEST_SQUOTE='single'\n" +
            "export CS2TEST_EXPORTED=exp\n" +
            "  CS2TEST_TRIM = spaced  \n");
        try
        {
            ClearAll();
            int set = DotEnv.LoadFile(path);
            Assert.Equal(5, set);
            Assert.Equal("hello", Environment.GetEnvironmentVariable("CS2TEST_PLAIN"));
            Assert.Equal("quoted value", Environment.GetEnvironmentVariable("CS2TEST_DQUOTE"));
            Assert.Equal("single", Environment.GetEnvironmentVariable("CS2TEST_SQUOTE"));
            Assert.Equal("exp", Environment.GetEnvironmentVariable("CS2TEST_EXPORTED"));
            Assert.Equal("spaced", Environment.GetEnvironmentVariable("CS2TEST_TRIM"));
        }
        finally
        {
            ClearAll();
            File.Delete(path);
        }
    }

    [Fact]
    public void Does_not_clobber_already_set_var()
    {
        var path = WriteTempEnv("CS2TEST_PRESET=from_file\n");
        try
        {
            Environment.SetEnvironmentVariable("CS2TEST_PRESET", "from_process");
            int set = DotEnv.LoadFile(path);
            Assert.Equal(0, set);
            Assert.Equal("from_process", Environment.GetEnvironmentVariable("CS2TEST_PRESET"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CS2TEST_PRESET", null);
            File.Delete(path);
        }
    }

    [Fact]
    public void Ignores_empty_values_and_malformed_keys()
    {
        var path = WriteTempEnv(
            "CS2TEST_EMPTY=\n" +
            "9BADKEY=value\n" +
            "no_equals_line\n" +
            "CS2TEST_OK=good\n");
        try
        {
            ClearAll();
            int set = DotEnv.LoadFile(path);
            Assert.Equal(1, set);
            Assert.Null(Environment.GetEnvironmentVariable("CS2TEST_EMPTY"));
            Assert.Null(Environment.GetEnvironmentVariable("9BADKEY"));
            Assert.Equal("good", Environment.GetEnvironmentVariable("CS2TEST_OK"));
        }
        finally
        {
            ClearAll();
            File.Delete(path);
        }
    }

    [Fact]
    public void FindEnvFile_stops_at_repo_root_without_env()
    {
        // A temp dir with a .git marker but no .env returns null (does not climb to
        // a parent .env that might belong to a different tree).
        var root = Path.Combine(Path.GetTempPath(), "cs2-find-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "a", "b");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: x");
        try
        {
            Assert.Null(DotEnv.FindEnvFile(sub));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ClearAll()
    {
        foreach (var k in new[]
        {
            "CS2TEST_PLAIN", "CS2TEST_DQUOTE", "CS2TEST_SQUOTE", "CS2TEST_EXPORTED",
            "CS2TEST_TRIM", "CS2TEST_EMPTY", "9BADKEY", "CS2TEST_OK", "no_equals_line",
        })
        {
            Environment.SetEnvironmentVariable(k, null);
        }
    }
}
