# walker/third_party/

External dependencies for the C++ schema-walker kernel. This directory holds the **only** allowed third-party CS2-domain build input.

## hl2sdk

`alliedmodders/hl2sdk` (cs2 branch), pinned as a git submodule at `walker/third_party/hl2sdk`. Required by the walker for the C++ struct layouts of `CSchemaSystem`, `CSchemaClassInfo`, `CSchemaSystemTypeScope`, `CSchemaEnumInfo`, `SchemaClassFieldData_t`, `SchemaMetadataEntryData_t`, `ICvar`, `ConVarRefAbstract`, and the NetMessages registry types.

### Fetching it

The submodule is intentionally lazy-initialized — `git clone` of this repo skips it by default because it is large.

```bash
git submodule update --init walker/third_party/hl2sdk
```

The pinned commit and the branch tracking hint live in `.gitmodules` at the repo root. The pin moves only as part of a deliberate "bump HL2SDK" PR.

### What it is NOT allowed to become

Do **not** add `SteamDatabase/GameTracking-CS2`, `ValveResourceFormat/SchemaExplorer`, or `ValveResourceFormat/DumpSource2` as submodules here. Walker code is clean-room and owned in our own tree; it is not a fork of any of those.
