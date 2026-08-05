// Steam session cache tests.
//
// Verifies the refresh-token cache:
//   - round-trips a session (save then load) under a gitignored cache/ dir
//   - the username does NOT appear in the on-disk filename (it is hashed)
//   - the refresh token does NOT appear in cleartext in the on-disk bytes on
//     Windows (DPAPI), and the file lives under cache/ regardless
//   - refuses to write outside a gitignored cache/ directory (hygiene guard)
//   - Clear() removes the cached session
//
// CREDENTIAL HYGIENE: these tests use a synthetic fake token; no real secret.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class SteamSessionStoreTest
{
    private static string NewCacheRoot()
    {
        // Path containing "cache" so EnsureUnderCache accepts it; under temp.
        var dir = Path.Combine(Path.GetTempPath(), "cs2-test-" + Guid.NewGuid().ToString("N"), "cache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var root = NewCacheRoot();
        try
        {
            var store = new SteamSessionStore(TextWriter.Null, root);
            var data = new SteamSessionData("the_account", "REFRESH_TOKEN_VALUE", "guard-blob");
            store.Save(data);

            var loaded = store.TryLoad("the_account");
            Assert.NotNull(loaded);
            Assert.Equal("the_account", loaded!.AccountName);
            Assert.Equal("REFRESH_TOKEN_VALUE", loaded.RefreshToken);
            Assert.Equal("guard-blob", loaded.GuardData);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Username_not_in_filename()
    {
        var root = NewCacheRoot();
        try
        {
            var store = new SteamSessionStore(TextWriter.Null, root);
            store.Save(new SteamSessionData("secret_user_name", "tok", null));

            var files = Directory.GetFiles(Path.Combine(root, "steam-session"));
            Assert.Single(files);
            var name = Path.GetFileName(files[0]);
            Assert.DoesNotContain("secret_user_name", name, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void File_lives_under_cache_dir()
    {
        var root = NewCacheRoot();
        try
        {
            var store = new SteamSessionStore(TextWriter.Null, root);
            store.Save(new SteamSessionData("u", "tok", null));
            var files = Directory.GetFiles(Path.Combine(root, "steam-session"));
            Assert.Single(files);
            Assert.Contains($"cache{Path.DirectorySeparatorChar}steam-session", files[0]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Refuses_to_write_outside_cache_dir()
    {
        // A root with no "cache"/"temp"/"tmp" path segment must be rejected by EnsureUnderCache.
        // It must ALSO be writable so the guard (InvalidOperationException) is what fires — not an
        // OS permission error. The old /var/lib path was non-writable on Linux, so Save's
        // Directory.CreateDirectory threw UnauthorizedAccessException BEFORE the guard ran. The
        // user-profile dir is writable on every CI OS yet carries no cache/temp token (avoid
        // Path.GetTempPath(): the guard treats a "temp"/"tmp" segment as an acceptable location).
        var outside = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "cs2tracker-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SteamSessionStore(TextWriter.Null, outside);
            Assert.Throws<InvalidOperationException>(
                () => store.Save(new SteamSessionData("u", "tok", null)));
        }
        finally
        {
            try
            { Directory.Delete(outside, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Clear_removes_session()
    {
        var root = NewCacheRoot();
        try
        {
            var store = new SteamSessionStore(TextWriter.Null, root);
            store.Save(new SteamSessionData("u", "tok", null));
            Assert.NotNull(store.TryLoad("u"));
            store.Clear("u");
            Assert.Null(store.TryLoad("u"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Token_not_in_cleartext_on_windows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // DPAPI is Windows-only; off Windows the gitignore is the guarantee.
        }
        var root = NewCacheRoot();
        try
        {
            var store = new SteamSessionStore(TextWriter.Null, root);
            const string token = "SUPER_SECRET_REFRESH_TOKEN_0xCAFE";
            store.Save(new SteamSessionData("u", token, null));
            var bytes = File.ReadAllBytes(Directory.GetFiles(Path.Combine(root, "steam-session"))[0]);
            var asText = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain(token, asText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static void DeleteRoot(string cacheRoot)
    {
        try
        {
            var parent = Directory.GetParent(cacheRoot)!.FullName;
            Directory.Delete(parent, recursive: true);
        }
        catch (IOException) { }
    }
}
