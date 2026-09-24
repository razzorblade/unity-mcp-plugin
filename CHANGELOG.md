# Changelog

All notable changes to this package will be documented in this file.

## [2.41.0] - 2026-09-25

Companion to server **2.37.0** (token-budget paging). Every response is read by an LLM, so bytes are tokens: the read endpoints now answer in dense JSON that drops only what the reader can reconstruct. No information is removed. `verbose:true` restores the previous shape on every endpoint listed below.

### Changed: token-dense responses
- **Vectors and colors are arrays**: `[x,y,z]`, `[r,g,b,a]`. This applies to the hierarchy, transforms, physics, lighting, selection and graphics outputs, and to property values. The code is one shared writer (`MCPWire`); it used to be six copies.
- **Row lists are tables**: `{columns, common?, groupedBy, groups:{prefix:[[...]]}}`. Columns that hold the same value on every row are stated once in `common`, and rows are grouped by parent path or folder, so the full path is `prefix + "/" + first cell`. A GameObject whose name contains `/` still round-trips. This applies to the `search/*` endpoints (by component, tag, layer, name and shader, missing references, assets), `asset/list`, `selection/find-by-type`, `packages/list`, `packages/search` and the `graphics/material-info` properties.
- **Default-valued and derivable fields are omitted**:
  - `gameobject/info`:
    - Local transform appears only when it differs from world, and `lossyScale` only when it differs from scale.
    - `layerIndex`, `childCount` and `parent` are dropped (derivable).
    - Disabled components are listed once (`Type#2` = the second of that type).
    - Namespaces are listed only for non-`UnityEngine` types.
  - Property reads: `displayName` appears only when it differs from the nicified name, and `editable` only when false.
  - `prefab-asset/hierarchy`: dense like `scene/hierarchy`, and it gains `maxNodes`.
  - `graphics` bounds: center and size only (extents, min and max are derivable).
- **`console/log` collapses identical entries** (type + message + stack trace), like the Console window's Collapse toggle. Each entry gets `repeats` and `firstTimestamp`. `count` now bounds distinct entries, so one error spamming every frame no longer pushes every other message out. Empty stack traces are omitted. `collapse:false` gives the old behaviour.
- **`prefab/info` overrides are grouped per target**: `{"Name (Type)": {propertyPath: value}}`. The bare target name used to be ambiguous, because a GameObject and each of its components share one name.
- Measured on a probe scene (Claude tokenizer, dense vs verbose):

  | Endpoint | Tokens saved |
  |---|---|
  | console/log | 83% |
  | prefab-asset/hierarchy | 71% |
  | gameobject/info | 70% |
  | search | 64% |
  | asset/list | 41% |
  | prefab/info | 40% |
  | component properties | 37% |
  | scene/hierarchy | 33% |
  | scriptableobject/info | 32% |

### Fixed
- **Arrays and nested structs came back as the literal string `"Generic"`.** Property reads were top-level only, so their data never reached the caller. Component, prefab-asset and ScriptableObject reads now share one recursive reader (`MCPPropertyReader`). Past `maxDepth` (default 4) a subtree becomes `{"$more": "<propertyPath>"}`, and arrays longer than `maxArrayElements` (default 100) become `{length, items}`. The new `propertyPath` argument reads any single nested property.
- **`asset/list` ignored `maxResults`** and returned the whole folder however large. It now honours `maxResults` (default 500) plus `offset`, and reports `totalFound`, `truncated` and `nextOffset`. `includeGuid:false` drops the GUIDs, which are about 80% of a listing.
- **`graphics/mesh-info`, `material-info` and `renderer-info` never saw the object path.** The MCP schemas send `objectPath` but the handlers read `gameObjectPath`, so `renderer-info` (where the path is required) could not succeed at all. Both names are accepted now.
- **`graphics/material-info` and `texture-info` ignored `includePreview` and `previewSize`** and always rendered a preview. Both are honoured now, and `previewSize` actually scales the preview.
- **A vector sent as an array was silently set to zero.** Setters cast the argument `as Dictionary`, so `[1,2,3]` became `Vector3.zero`. Every vector and color input (`MCPArgs.ToVector3`, `ToColor` and the others) accepts both forms, and an unreadable value fails with an error. `Quaternion` properties can now be set.
- **`scriptableobject/info` formatted vectors with the current culture.** On decimal-comma locales it wrote `"(1,5, 2)"`. Values are typed now.
- Unbounded replies are capped: `selection/find-by-type` (`limit`, default 500) and `packages/search` (`limit`, default 50). Both report `totalFound` and `truncated`.

### Compatibility
- Response shapes changed. Anything that parses the old shapes should pass `verbose:true`. Inputs accept both the old and new vector forms, and older servers work unchanged.

### Verified
- Compiles with 0 errors on Unity 6000.0.79f1, 6000.3.21f1 and 6000.5.1f1. Route registry unchanged (342 routes, `--check` clean).
- A batch-mode harness built a probe scene (nested arrays and structs, duplicate components, a GameObject named `A/B`, 39 console logs, a prefab instance with overrides). It called the real handlers dense and verbose, checked every expectation above, and exercised array and object vector inputs. The bridge itself does not run in batch mode, so HTTP was not exercised.
- Unity 6000.3.21f1 exits with a crash (exit 139) at the end of every batch-mode run, after "Batchmode quit successfully invoked". This is an editor bug, not the package: an empty project without the package crashes the same way, including through `-quit` with or without `-nographics`, and through `EditorApplication.Exit(0)`. Compilation is unaffected (0 errors). Scripted checks on that version should read the log rather than the exit code.

