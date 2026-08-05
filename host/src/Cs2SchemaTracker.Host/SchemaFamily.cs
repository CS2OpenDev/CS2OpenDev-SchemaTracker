// single source of the schemas/*.proto FAMILY version in host code.
//
// This is the schemas/*.proto family version (currently 0.5.0). Every per-tuple artifact
// emitter stamps this string into its `schema_version` field (entity_schema.json,
// modules.json, ...). It is intentionally ONE constant so a single edit here flows to every
// emitter and no literal "0.x.y" is scattered across the host. Emitters MUST source the
// version from here rather than hardcoding it.
//
// Under the lightweight pre-v1.0.0 stability rule (README "Stability"): bump this when the
// artifact surface changes AND the sibling CS2OpenDev-Docs is updated in the same change.
// Formal semver discipline returns at v1.0.0.

namespace Cs2SchemaTracker.Host;

/// <summary>
/// The schemas/*.proto family version. Single source of truth in host code.
/// </summary>
public static class SchemaFamily
{
    /// <summary>
    /// Current schemas/*.proto family version. Pre-v1.0.0 it changes when the artifact
    /// surface changes and CS2OpenDev-Docs is updated in lockstep (README "Stability").
    /// Do not hardcode this literal elsewhere — reference this field.
    /// </summary>
    // 0.5.0: schema-coverage expansion. The walker now walks the global
    // "!GlobalTypes" scope, drives full registration for every loaded module, and
    // loads the wider schema-bearing module set — entity_schema grows from ~1.1k
    // to ~3.6k classes and 15 to ~590 enums (strictly additive; shape unchanged).
    // Globally-registered types carry module "!GlobalTypes" with their owning
    // project in projectName. See CS2OpenDev-Docs SCHEMA_COVERAGE_GAP_EVALUATION.md.
    public const string Version = "0.5.0";
}
