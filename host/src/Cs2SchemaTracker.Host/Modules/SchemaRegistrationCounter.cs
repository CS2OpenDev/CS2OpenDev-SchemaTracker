// Per-binary schema-registration attribution.
//
// modules.json carries, per binary the tool opened, "the count of schema-system
// registrations attributed to it." The data is host-side already: WalkerOutput.entity_schema
// carries every SchemaClass and SchemaEnum, each tagged with the originating `module`. The
// registration count for a binary is the number of those classes + enums whose `module` maps
// to that binary.
//
// Module-string forms. The schema `module` tag is, in live walker output, the bare module
// FILE NAME — "client.dll", "server.dll", "animationsystem.dll" on Windows; "server.so" etc.
// on Linux — OR a pseudo-scope beginning with '!' ("!GlobalTypes"). Older/synthetic walks use
// the BARE module name ("client", "server", "engine2"). Both forms reduce to the same KEY by
// stripping a trailing ".dll"/".so" (and, on Linux, a leading "lib" prefix is NOT stripped —
// the module tag and the file name agree on it). The key is then matched against each
// modules.json entry's file name reduced the same way.
//
// "!GlobalTypes" (and any '!'-prefixed pseudo-scope) has NO backing shipped binary: its
// registrations are not attributed to any file row. Those counts are intentionally dropped —
// we never invent a fake module row for them. A binary with no matching schema
// registrations (tier0, filesystem, Qt/dependency DLLs) legitimately gets 0.
//
// Determinism: the count is a pure function of the entity_schema contents; iteration
// order does not affect the totals.

using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.Modules;

internal static class SchemaRegistrationCounter
{
    /// <summary>
    /// Build a map from normalized module key -> count of schema registrations (classes + enums)
    /// attributed to that module. Pseudo-scopes (names beginning with '!', e.g. "!GlobalTypes")
    /// are excluded — they have no backing shipped binary and are not attributed to any file.
    /// </summary>
    public static IReadOnlyDictionary<string, int> CountByModuleKey(EntitySchemaWalk? entitySchema)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (entitySchema is null)
        {
            return counts;
        }

        foreach (var cls in entitySchema.Classes)
        {
            Tally(counts, cls.Module);
        }
        foreach (var en in entitySchema.Enums)
        {
            Tally(counts, en.Module);
        }
        return counts;
    }

    /// <summary>
    /// The registration count attributed to a binary identified by its FILE NAME (e.g.
    /// "client.dll", "libserver.so") using a map produced by <see cref="CountByModuleKey"/>.
    /// A binary with no matching schema registrations returns 0 (legitimate, not an error).
    /// </summary>
    public static int CountForBinaryFileName(
        IReadOnlyDictionary<string, int> countByModuleKey, string binaryFileName)
    {
        ArgumentNullException.ThrowIfNull(countByModuleKey);
        ArgumentException.ThrowIfNullOrEmpty(binaryFileName);
        var key = NormalizeKey(binaryFileName);
        return countByModuleKey.TryGetValue(key, out var n) ? n : 0;
    }

    /// <summary>
    /// build a map from normalized module key -> the boot-resolved CreateInterface version
    /// strings the walker observed for that module. Reuses the SAME identity mapping
    /// (<see cref="NormalizeKey"/>) as the schema-registration merge so a ModulesWalk entry keyed
    /// "client" / "client.dll" / "libclient.so" all join onto the same modules.json row. Entries
    /// are returned verbatim (the emitter does the final sort/dedup); a '!'-prefixed pseudo-module
    /// is dropped (no backing binary, mirrors the registration-count rule). Duplicate module keys
    /// in the walk are merged (interfaces concatenated).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ResolvedInterfacesByModuleKey(
        ModulesWalk? modulesWalk)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (modulesWalk is null)
        {
            return byKey.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
        }

        foreach (var mi in modulesWalk.Modules)
        {
            if (string.IsNullOrEmpty(mi.Module) || mi.Module[0] == '!')
            {
                continue;
            }
            var key = NormalizeKey(mi.Module);
            if (!byKey.TryGetValue(key, out var list))
            {
                list = new List<string>();
                byKey[key] = list;
            }
            list.AddRange(mi.ResolvedInterfaces);
        }

        return byKey.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// the boot-resolved CreateInterface versions attributed to a binary identified by its
    /// FILE NAME, using a map produced by <see cref="ResolvedInterfacesByModuleKey"/>. Returns an
    /// empty list when the walk resolved none for this binary (legitimate, not an error).
    /// </summary>
    public static IReadOnlyList<string> ResolvedInterfacesForBinaryFileName(
        IReadOnlyDictionary<string, IReadOnlyList<string>> resolvedByModuleKey, string binaryFileName)
    {
        ArgumentNullException.ThrowIfNull(resolvedByModuleKey);
        ArgumentException.ThrowIfNullOrEmpty(binaryFileName);
        var key = NormalizeKey(binaryFileName);
        return resolvedByModuleKey.TryGetValue(key, out var list) ? list : Array.Empty<string>();
    }

    private static void Tally(Dictionary<string, int> counts, string? module)
    {
        if (string.IsNullOrEmpty(module))
        {
            // An untagged registration is not attributable to a binary; drop it (it cannot
            // pin to a file row anyway). This is rare in real walks.
            return;
        }
        if (module[0] == '!')
        {
            // Pseudo-scope (e.g. "!GlobalTypes"): no backing binary; intentionally unattributed.
            return;
        }
        var key = NormalizeKey(module);
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    /// <summary>
    /// Reduce a module tag OR a binary file name to a comparison key: lower-cased, with a
    /// trailing ".dll" / ".so" extension stripped and a leading "lib" prefix stripped.
    /// "client.dll" -> "client"; "client" -> "client"; "libserver.so" -> "server";
    /// "server" -> "server". The symmetric "lib" strip lets a Linux file name ("libserver.so")
    /// agree with the walker's bare module tag ("server") — CS2 Linux modules ship lib-prefixed
    /// but the schema scope reports the bare name.
    /// </summary>
    internal static string NormalizeKey(string nameOrFile)
    {
        var s = nameOrFile.ToLowerInvariant();
        if (s.EndsWith(".dll", StringComparison.Ordinal))
        {
            s = s[..^4];
        }
        else if (s.EndsWith(".so", StringComparison.Ordinal))
        {
            s = s[..^3];
        }
        if (s.StartsWith("lib", StringComparison.Ordinal) && s.Length > 3)
        {
            s = s[3..];
        }
        return s;
    }
}
