# The schema handle-type family

`entity_schema.json` carries no handle discriminator field — deliberately (see issue #4: the
schema system records no entity/strong/weak semantic, so any emitted field would be name-derived
inference, and demo-embedded serializer strings could never carry it anyway). The **type-name
prefix family is the contract**, for every consumer on every channel. This page is the written
spec that string match can be checked against.

## The six prefixes

Counts are field-type-node occurrences at build 24662694 (windows-x86_64), all `category: "ATOMIC"`.

| Prefix | Typed | Count | Refers to | Notes |
|---|---|---:|---|---|
| `CHandle< T >` | yes | 404 | **entity** | The standard networked entity handle (index + serial). `T` is the declared entity class. |
| `CEntityHandle` | no | 29 | **entity** | The untyped form of the same entity handle. |
| `CStrongHandle< T >` | yes | 207 | **resource** | Strong (keep-loaded) resource reference; `T` is an `InfoForResourceType*` marker. |
| `CStrongHandleVoid` | no | 2 | **resource** | Untyped strong resource reference. |
| `CStrongHandleCopyable< T >` | yes | 4 | **resource** | Copyable variant of the strong reference. |
| `CWeakHandle< T >` | yes | 42 | **resource** | Weak (non-retaining) resource reference; `T` is an `InfoForResourceType*` marker. |

**Typed vs untyped is structural, not just naming**: the four typed prefixes always carry the
template argument both in the `name` text and as the `inner` type node; the two untyped ones never
have `inner`. Verified across every type node (including `inner`/`inner2`) at 24662694 — 657 typed
nodes all with `inner`, 31 untyped all without.

## Matching guidance

- **Match on prefix, not exact text.** This artifact renders template text with padded angle
  brackets (`CHandle< CBaseEntity >`); other channels spell the same type differently
  (demo-embedded `CSVCMsg_FlattenedSerializer` symbols, HTML-escaped tool output, etc.).
- **Prefix-order matters** if matching with alternation: `CStrongHandleCopyable` and
  `CStrongHandleVoid` share the `CStrongHandle` prefix — test the longer names first.
- The entity/resource split is the one that changes decode behavior: entity handles are the
  32-bit index+serial values that appear in networked entity state and game-event `*_pawn` keys;
  resource handles never do on the GOTV wire path (only `CHandle`, `CStrongHandle`, and
  `CEntityHandle` have been observed in demo flattened serializers).

## Stability

The six-name family has been stable across the whole committed corpus (2023-03-22 → present).
A new prefix appearing in `entity_schema.json` is a schema-system change worth an issue; this
page should be updated in the same change.
