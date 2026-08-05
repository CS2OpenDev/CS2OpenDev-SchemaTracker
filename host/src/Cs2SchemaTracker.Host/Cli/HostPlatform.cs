// Local-runnable, with documented host-OS constraint.
//
// The walker kernel dlopen/LoadLibrary's the target platform's CS2 binaries into
// its own process, so it can only run on a host whose OS+arch matches the
// target platform's OS+arch:
//   linux-x86_64   ⇒ Linux  x64 host
//   windows-x86_64 ⇒ Windows x64 host
// A macOS developer (arm64 or x64) matches NEITHER platform and cannot natively
// dump any platform — they must use a Linux/Windows runner or VM.
//
// PLATFORM MODEL (v0.2): there are exactly TWO platforms, "windows-x86_64" and
// "linux-x86_64". client vs server is no longer a platform dimension — one walk
// per platform loads ALL modules (client+server+engine) and each emitted class
// carries its source as the per-class SchemaClass.module tag.
//
// This type provides the cross-OS guard for `extract`. It is hand-rolled and
// dependency-free (no CLI library, no extra NuGet), matching Program.cs's
// stated design. It changes only behavior, not any CLI surface in
// README.md, so no schema-family version bump is required.

using System.Runtime.InteropServices;

namespace Cs2SchemaTracker.Host.Cli;

internal static class HostPlatform
{
    /// <summary>The two v1 platforms (README.md).</summary>
    public static readonly IReadOnlyList<string> KnownPlatforms = new[]
    {
        "linux-x86_64",
        "windows-x86_64",
    };

    /// <summary>The OS+arch a host must have to natively extract a platform.</summary>
    internal enum RequiredHost
    {
        LinuxX64,
        WindowsX64,
    }

    /// <summary>
    /// Maps a platform string to the host OS+arch required to extract it.
    /// Returns false for any string that is not one of the two known platforms.
    /// </summary>
    public static bool TryGetRequiredHost(string platform, out RequiredHost required)
    {
        switch (platform)
        {
            case "linux-x86_64":
                required = RequiredHost.LinuxX64;
                return true;
            case "windows-x86_64":
                required = RequiredHost.WindowsX64;
                return true;
            default:
                required = default;
                return false;
        }
    }

    public static bool IsKnownPlatform(string platform) =>
        TryGetRequiredHost(platform, out _);

    /// <summary>
    /// True iff the current host's OS+arch can natively run the walker for
    /// <paramref name="platform"/>. False for an unknown platform OR a cross-OS host.
    /// Inspect <see cref="IsKnownPlatform"/> first to distinguish the two cases.
    /// </summary>
    public static bool CanExtractPlatform(string platform)
    {
        if (!TryGetRequiredHost(platform, out var required))
            return false;
        return required == CurrentHost();
    }

    /// <summary>
    /// The current host's OS+arch as a <see cref="RequiredHost"/>, or null if
    /// the host is neither Linux-x64 nor Windows-x64 (e.g. macOS, or a non-x64
    /// Linux/Windows arch). A null result means no platform can be extracted here.
    /// </summary>
    internal static RequiredHost? CurrentHost()
    {
        var isX64 = RuntimeInformation.OSArchitecture == Architecture.X64;
        if (!isX64)
            return null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return RequiredHost.LinuxX64;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RequiredHost.WindowsX64;
        return null;
    }

    /// <summary>
    /// Human-readable label for the OS+arch a platform needs ("Linux x86_64", ...).
    /// </summary>
    internal static string RequiredHostLabel(RequiredHost required) => required switch
    {
        RequiredHost.LinuxX64 => "Linux x86_64",
        RequiredHost.WindowsX64 => "Windows x86_64",
        _ => required.ToString(),
    };

    /// <summary>Label for the current host, for use in guidance messages.</summary>
    internal static string CurrentHostLabel()
    {
        string os =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" :
            "unknown-OS";
        return $"{os} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }

    /// <summary>
    /// The documented "use a matching runner/VM" guidance message for a cross-OS
    /// extraction attempt. Deterministic: no timestamps, no machine-varying data
    /// beyond the host OS/arch the user is already on.
    /// </summary>
    public static string CrossOsGuidanceMessage(string platform, RequiredHost required) =>
        $"extract: cannot extract platform '{platform}' on this host." + Environment.NewLine +
        $"  This host is {CurrentHostLabel()}; platform '{platform}' requires a {RequiredHostLabel(required)} host." + Environment.NewLine +
        " The walker loads the target's CS2 binaries into its own process, so the host OS+arch must match the platform." + Environment.NewLine +
        $"  Run this extraction on a {RequiredHostLabel(required)} runner or VM instead.";

    /// <summary>
    /// The fail-loud message for an unknown/invalid platform string, listing the
    /// two valid platforms (README.md).
    /// </summary>
    public static string UnknownPlatformMessage(string platform) =>
        $"extract: unknown platform '{platform}'. Valid platforms: {string.Join(", ", KnownPlatforms)}.";
}
