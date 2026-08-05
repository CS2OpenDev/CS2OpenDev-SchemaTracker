// Shared xUnit skip gate for tests that can only run on Windows.
//
// WHY: the extract-integration tests drive a full `extract` whose network-message stage runs the
// offline NetworkMessageRttiScanner over the fixture binaries. Those fixtures carry valid
// windows-x86_64 RTTI ONLY; linux-x86_64 extraction is a deferred, unshipped target, so on a
// non-Windows runner the scanner decodes ZERO messages, extract fail-louds (exit 65), and the
// tests fail for an environment reason rather than a real regression. Gate them to Windows so they
// remain real coverage where the fixtures are valid and are cleanly SKIPPED (not deleted, not
// silently passed) elsewhere. The skip reason surfaces in `dotnet test` output.

using System.Runtime.InteropServices;

using Xunit;

namespace Cs2SchemaTracker.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped on non-Windows runners. Use on tests whose
/// fixtures are windows-x86_64-only (see file header for the full rationale).
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Windows-only: extract fixtures carry windows-x86_64 RTTI; linux extraction is a deferred, unshipped target.";
        }
    }
}

/// <summary>
/// A <see cref="TheoryAttribute"/> that is skipped on non-Windows runners. Companion to
/// <see cref="WindowsOnlyFactAttribute"/> for data-driven tests.
/// </summary>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Windows-only: extract fixtures carry windows-x86_64 RTTI; linux extraction is a deferred, unshipped target.";
        }
    }
}
