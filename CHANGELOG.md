# Changelog

All notable changes to this package will be documented in this file.

## [Unreleased]

Companion to the server's FishNet tools (`unity_fishnet_*`) and `unity_asset_refresh`.

### Fixed
- **`UnityException: SetLogCallbackDefined can only be called from the main thread`**, followed by a `TypeInitializationException` for `MCPRequestQueue`. The queue registered its console-capture callback in its static constructor, and that constructor runs on whichever thread touches the class first. After a domain reload that was often an HTTP pool thread: a ping reads the queue counters off-thread, before the first editor tick. Unity rejected the registration, and the failed type initializer left the queue unusable until the next reload. The callback is now registered from an `[InitializeOnLoadMethod]`, which always runs on the main thread, so the type initializer no longer calls Unity APIs.

### Added: `asset/refresh`
- Runs Assets > Refresh (Ctrl+R) on demand. It imports files changed outside Unity (IDE, an agent's file tools, git) and compiles changed scripts, with `forceRecompile` to recompile anyway. It returns `compiling` and the domain `epoch`, so a caller can tell when the compile and domain reload have finished. Unity's Auto Refresh only runs when the editor window regains focus. An explicit refresh has no focus requirement: it runs on the next main-thread tick, and the editor keeps ticking while unfocused (measured at about 10 Hz in the background). Route registry: 360 → 361 routes.

### Verified: refresh and queue fix
- A batch-mode probe on 6000.0.79f1 and 6000.5.1f1 checks five things:
  - The queue's type initializer runs from a background thread without error. The same check fails against the previous code with the reported exception.
  - Per-ticket console capture still records a warning.
  - A refresh with no changes reports `compiling=false`.
  - A refresh after writing a `.cs` file reports `compiling=true`.
  - The domain reloads with the new class compiled.

### Added: Fish-Networking (FishNet) integration
18 `fishnet/*` routes for FishNet 4.x, so everyday networking work no longer needs `execute-code` snippets. They compile only when FishNet is installed as the `com.firstgeargames.fishnet` package (asmdef `versionDefine` → `FISHNET_INSTALLED`). Without it, every route returns a "not installed" error with the install URL. For a copy imported into `Assets/`, add `FISHNET_INSTALLED` to the scripting define symbols.
- **Edit Mode setup** (every scene change is Undo-tracked; invalid arguments fail before anything changes):
  - `setup-network-manager` creates a NetworkManager with a TransportManager and transport (Tugboat by default, or any `Transport` type by name). It assigns the spawnable prefabs and can add a PlayerSpawner with spawn points and FishNet's other managers. It refuses to add a second manager unless `allowMultiple` is set.
  - `configure-transport` reads or changes port, client address, max clients and bind addresses. It works on any transport through FishNet's `Transport` API. In Edit Mode it returns the full serialized settings.
  - `add-network-object` adds a NetworkObject, and optionally a NetworkTransform, to a scene object or a prefab root, and applies its settings (`isNetworked`, `isSpawnable`, `isGlobal`, `initializeOrder`, `preventDespawnOnDisconnect`, `defaultDespawnType`). It is idempotent.
  - `list-prefabs`, `refresh-prefabs` and `register-prefab` manage the spawnable collection. For `DefaultPrefabObjects`, registering regenerates the collection and verifies the prefab was picked up, instead of adding an entry the generator would wipe.
- **Inspection** in both modes:
  - `status` shows every NetworkManager with its state, transport, port and prefab collection. In Play Mode it adds clients, spawned counts, tick and RTT.
  - `list-network-objects` lists scene NetworkObjects offline and server- or client-spawned objects at runtime.
  - `get-network-object` shows settings, behaviours and **live SyncType values** (SyncVar, SyncList, SyncDictionary, SyncHashSet, SyncTimer, SyncStopwatch), plus ObjectId, owner and observers at runtime.
- **Play Mode session control**:
  - `start` (host/server/client) is a deferred route. It answers once the server is listening and the client is authenticated, or reports a timeout (`waitSeconds`, default 5).
  - `stop` and `list-connections`.
  - `spawn`: pooled, by name, asset path or runtime prefabId, optionally owned by a client, and refuses unregistered prefabs up front.
  - `despawn`, `set-ownership` and `kick`.
  - `load-scene` and `unload-scene` go through FishNet's SceneManager, globally or per connection, and check Build Settings first.
- Self-test gains a `fishnet` case.

### Verified
- Compiles with 0 errors and no new warnings on Unity 6000.0.79f1, 6000.3.21f1 and 6000.5.1f1 with FishNet 4.7.3, and without FishNet. Route registry: 342 → 360 routes (`--check` clean).
- A batch-mode harness on 6000.0.79f1 ran 40 checks against the real handlers, all passing.
  - Edit Mode: the setup and inspection flows plus their error paths.
  - Play Mode: a live Tugboat host session covering the deferred start, PlayerSpawner, spawning by name and by prefabId with an owner, SyncVar/SyncList/SyncDictionary values read back from a spawned object, removing and giving ownership, despawn, a FishNet scene load that reached the client, unload and stop.
  - The bridge does not run in batch mode, so HTTP was not exercised.

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

