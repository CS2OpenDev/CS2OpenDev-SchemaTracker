# Schema flag bits

`entity_schema.json` exposes the Source 2 schema system's raw runtime bitfields verbatim:

| Artifact field | Record | Source member |
|---|---|---|
| `flags` on a class record | `SchemaClass` | `SchemaClassInfoData_t.m_nFlags1` |
| `flags2` on a class record | `SchemaClass` | `SchemaClassInfoData_t.m_nFlags2` |
| `flags` on an enum record | `SchemaEnum` | `SchemaEnumInfoData_t.m_nFlags` |

The tool does not interpret these — it copies the words through as opaque bits, deliberately, because a projection (`"abstract": true`) throws away everything the projection didn't anticipate. This page is the decode table so that carrying raw bits doesn't also mean carrying uninterpretable bits.

**Source.** All names below are transcribed from the pinned hl2sdk the walker builds against:

- `alliedmodders/hl2sdk` (cs2 branch) @ **`5f891c9026230cce0fc0a3fc4b5fef1c467a1385`**
- `walker/third_party/hl2sdk/public/schemasystem/schematypes.h`, lines 32-61

Re-transcribe this page when the submodule pin moves. The names are hl2sdk's, and hl2sdk is itself reverse-engineered — they are the best available vocabulary for these bits, not Valve's own. Where a name is contradicted by measurement, this page says so rather than repeating it.

**Measurements.** The bit-frequency counts below were measured against this repo's committed artifacts for build **24537688** (`schema_version` 0.5.0), both platforms independently:

| | windows-x86_64 | linux-x86_64 |
|---|---:|---:|
| enum records | 674 | 549 |
| `module: "!GlobalTypes"` | 591 | 514 |
| other (real binary) | 83 | 35 |

Counts are per-build. Re-measure before treating any of them as exact for a different build — the verdicts below, however, are corroborated independently on both platforms.

## Class `flags` — `SchemaClassFlags1_t` (uint32)

| Bit | Value | Name |
|---:|---:|---|
| 0 | `0x00001` | `SCHEMA_CF1_HAS_VIRTUAL_MEMBERS` |
| 1 | `0x00002` | `SCHEMA_CF1_IS_ABSTRACT` |
| 2 | `0x00004` | `SCHEMA_CF1_HAS_TRIVIAL_CONSTRUCTOR` |
| 3 | `0x00008` | `SCHEMA_CF1_HAS_TRIVIAL_DESTRUCTOR` |
| 4 | `0x00010` | `SCHEMA_CF1_LIMITED_METADATA` |
| 5 | `0x00020` | `SCHEMA_CF1_INHERITANCE_DEPTH_CALCULATED` |
| 6 | `0x00040` | `SCHEMA_CF1_MODULE_LOCAL_TYPE_SCOPE` |
| 7 | `0x00080` | `SCHEMA_CF1_GLOBAL_TYPE_SCOPE` |
| 8 | `0x00100` | `SCHEMA_CF1_CONSTRUCT_ALLOWED` |
| 9 | `0x00200` | `SCHEMA_CF1_CONSTRUCT_DISALLOWED` |
| 10 | `0x00400` | `SCHEMA_CF1_INFO_TAG_MNetworkAssumeNotNetworkable` |
| 11 | `0x00800` | `SCHEMA_CF1_INFO_TAG_MNetworkNoBase` |
| 12 | `0x01000` | `SCHEMA_CF1_INFO_TAG_MIgnoreTypeScopeMetaChecks` |
| 13 | `0x02000` | `SCHEMA_CF1_INFO_TAG_MDisableDataDescValidation` |
| 14 | `0x04000` | `SCHEMA_CF1_INFO_TAG_MClassHasEntityLimitedDataDesc` |
| 15 | `0x08000` | `SCHEMA_CF1_INFO_TAG_MClassHasCustomAlignedNewDelete` |
| 16 | `0x10000` | `SCHEMA_CF1_UNK016` — unnamed upstream |
| 17 | `0x20000` | `SCHEMA_CF1_INFO_TAG_MConstructibleClassBase` |
| 18 | `0x40000` | `SCHEMA_CF1_INFO_TAG_MHasKV3TransferPolymorphicClassname` |

The `INFO_TAG_M*` bits mirror class-level metadata annotations of the same name — the schema system hoists a few frequently-tested annotations into the flags word so a consumer doesn't have to scan the metadata array for them.

**Abstract classes:** `flags & 0x2` is the abstract marker. It is safe to project — it was independently corroborated before this table existed, against three classes picked as abstract exemplars by inspection (`CPulseCell_BaseLerp`, `C_CS2HudModelBase`, `CPlayer_AutoaimServices`), all three of which carry the bit.

## Class `flags2` — `SchemaClassFlags2_t` (uint32)

`SchemaClassFlags2_t` is declared upstream as an **empty enum**: no bit in this word has a name.

This is an answer, not a documentation gap. `flags2` is often zero and there is nothing further to decode from it today. It is carried in the artifact because it is a real word in the record and because a future SDK will name its bits — not because it currently means anything a consumer can act on.

## Enum `flags` — `SchemaEnumFlags_t` (uint8)

Upstream names exactly three bits of the eight:

| Bit | Value | Name | Status |
|---:|---:|---|---|
| 0 | `0x01` | `SCHEMA_EF_IS_REGISTERED` | Confirmed — set on every enum record |
| 1 | `0x02` | `SCHEMA_EF_MODULE_LOCAL_TYPE_SCOPE` | Confirmed — see below |
| 2 | `0x04` | `SCHEMA_EF_GLOBAL_TYPE_SCOPE` | **Named but contradicted — do not use as a scope discriminator** |
| 3-7 | `0x08`-`0x80` | — | Undeclared upstream, but observed set in the wild |

### There is no flag-enum (`[Flags]`) marker bit

The declared vocabulary is the three bits above. **No bit marks an enum as a bit-flag set.** A consumer that wants to emit `[Flags]`, or the equivalent, has to decide it from the enumerator values — a power-of-two membership test over the members, or a curated list. This is not a bit that exists and is undocumented; it is a bit that does not exist.

`IS_REGISTERED` in particular is not a discriminator for anything: it is set on 100% of records, which follows from an unregistered enum not being reachable to walk in the first place.

### Undeclared does not mean unused

Bits 3, 4 and 5 (`0x08`, `0x10`, `0x20`) are **set on a substantial fraction of enum records** despite having no upstream name. Measured on build 24537688:

| Bit | windows | linux |
|---|---:|---:|
| `0x08` | 100 | 94 |
| `0x10` | 25 | 25 |
| `0x20` | 232 | 142 |
| `0x40` | 0 | 0 |
| `0x80` | 0 | 0 |

Bits 6 and 7 were set on no record on either platform — which is not the same as being reserved, only that nothing observed uses them.

Do not write a validity check that assumes unnamed bits are clear — it will reject roughly a third of the data. Mask to the bits you actually consume.

This is also why `0x10` can look like a plausible `[Flags]` marker under correlation testing: it is a real bit in real use, carrying some meaning, just not that one. Correlating it against a power-of-two oracle yields both false positives and false negatives, which is the signature of a near-miss rather than a match.

### Why `GLOBAL_TYPE_SCOPE` is flagged as contradicted

The two scope bits do not behave symmetrically against the record's own `module` field.

Measured on build 24537688, `0x02` tracks the `module` field exactly and `0x04` does not:

| | windows | linux |
|---|---:|---:|
| enums NOT in `!GlobalTypes` | 83 | 35 |
| `0x02` `MODULE_LOCAL_TYPE_SCOPE` set | **83** | **35** |
| enums IN `!GlobalTypes` | 591 | 514 |
| `0x04` `GLOBAL_TYPE_SCOPE` set | **11** | **11** |

`MODULE_LOCAL_TYPE_SCOPE` matches the non-global count exactly on both platforms — the name holds, and it is usable. `GLOBAL_TYPE_SCOPE` is set on 11 records against the 591 / 514 that actually live in the global scope. The name does not hold.

Two platforms with different totals landing on the same 11 says the bit means something specific and real; it just is not "this type is in the global scope." This page will not guess at what. Treat `0x04` as unreliable for scope questions.

**Use `module` instead.** It is the authoritative scope field and always populated. For the related question of which *project* owns a record, use `projectName` — present on class records, and on enum records as of schema family 0.5.1.

## Consuming these

Two rules that fall out of everything above:

1. **Mask what you read.** `flags & SOME_BIT`, never `flags == SOME_BIT` and never an exhaustiveness check over the whole word. Unnamed bits are set today and more will be named later.
2. **A named bit is a claim, not a guarantee.** These names come from reverse engineering. Where this page marks one confirmed, it was checked against data. Where it marks one contradicted, so was that. Bits carrying neither mark are transcriptions that nobody has verified against a real artifact — treat them accordingly.
