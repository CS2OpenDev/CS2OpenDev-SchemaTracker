// build-baked tool identity (git commit SHA).
//
// requires provenance.tool.git_commit to carry the dumper's git SHA. forbids any
// nondeterministic runtime value in an artifact — so the SHA must NOT be obtained by shelling out
// to `git` at extract time (that would also re-read the working tree, which is not the built-exe's
// identity). Instead Nerdbank.GitVersioning (nbgv) bakes the commit SHA into the host assembly at
// BUILD time as ThisAssembly.GitCommitId. ThisAssembly is an internal, build-generated type, so it
// is read HERE (a single source) and exposed for the provenance build site.
//
// Determinism: two runs of the SAME built exe read the same baked constant => byte-identical
// provenance.json. Re-building at a different commit legitimately changes the SHA — that is the
// tool-version identity wants, and is correct (different input/tool => different
// output).
//
// No-git / archive build: nbgv yields an empty (or absent) GitCommitId. We fall back to "" and
// never throw — provenance with an empty git_commit is the documented "unavailable deterministically"
// state (same as the pre-nbgv hardcoded "").

namespace Cs2SchemaTracker.Host;

/// <summary>
/// Build-baked identity of the host tool. The git commit SHA is stamped by Nerdbank.GitVersioning
/// (nbgv) into <c>ThisAssembly.GitCommitId</c> at build time; this is the single place that reads it.
/// </summary>
public static class ToolBuildInfo
{
    /// <summary>
    /// Full git commit SHA (40 hex chars) of CS2-Schema-Tracker the host was built from, baked at
    /// build time by nbgv. Returns "" when unavailable (e.g. a no-git/archive build) — never throws,
    /// never shells out to git. Reads ThisAssembly.GitCommitId, which nbgv generates as an
    /// internal constant in the host assembly.
    /// </summary>
    public static string GitCommitId => ThisAssembly.GitCommitId ?? "";
}
