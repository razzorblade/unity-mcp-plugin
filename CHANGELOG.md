# Changelog

All notable changes to this package will be documented in this file.

## [Unreleased]

Companion to the server's FishNet tools (`unity_fishnet_*`), `unity_asset_refresh` and `unity_editor_dialog_click`.

### Added: blocking-dialog detection and answering (protocol 3)
- **The native dialog blocking the main thread is named.** A dialog raised while an MCP command runs (e.g. "Scene(s) Have Been Modified — Save / Don't Save / Cancel" behind a menu item, or `DisplayDialog` in user code) parks the main thread on a human click. `ping` and every `queue/status` now carry `dialog`: `{ id, kind, title, message, buttons, defaultButton, cancelButton, clickable, blockedMs }`. `busyReason` becomes `blocked by dialog "<title>"`, and `executingTicketId` names the command the main thread is inside.
  - **How it detects:** `MCPDialogProbe` runs on the HTTP pool threads, the only managed threads still running during a modal loop, and only once the main thread has stalled for 1s. Results are cached per stall, so a healthy editor pays nothing.
    - It walks the UI thread's top-level windows with `FindWindowEx`, so no `EnumWindows` callback runs on a pool thread.
    - Unity's dialogs are standard `#32770` dialogs. Buttons have fixed ids: 1 = primary, 2 = cancel/Esc, 3 = alternate.
    - Titles and buttons are read without sending window messages. The message is in a read-only Edit control, which is read with a 250ms `SendMessageTimeout`.
  - **Window kinds:**
    - `dialog`: clickable.
    - `filePanel`: native open/save panels; only Cancel is offered.
    - `window`: modal windows Unity draws itself (`ShowModalUtility`); title only, not clickable.
    - `progress`: never a question.
- **`POST dialog/click {dialogId, button}`** presses a button off the main thread. It posts `WM_COMMAND`/`BN_CLICKED` to the dialog, which works whether or not the dialog is focused, and waits up to 2s for the dialog to close.
  - `button` is a label (case-insensitive, `&` mnemonics ignored) or a role (`primary`, `cancel`, `alternate`, `default`).
  - A `dialogId` that no longer matches the open dialog is refused with the current one (409), so a chained dialog is never answered unread. Other refusals: no dialog → 404, unknown button → 400, not clickable → 409, disabled → 403, non-Windows editor → 501.
  - Every click is logged to the Console and recorded in the action history as `dialog/click`.
- **Dashboard → Settings → Dialogs → "Let AI Answer Blocking Dialogs"** (project-scoped, on by default). When off, dialogs are still reported, but a human has to answer them.
- Windows editor only. Other platforms keep the stall-only report.
- Protocol version 2 → 3. `dialog/click` is handled beside the queue endpoints, not in the route switch, so the route registry is unchanged.

### Verified: dialogs
- Compiles with 0 errors on 6000.0.79f1, 6000.3.21f1 and 6000.5.1f1.
- An interactive probe harness raised real dialogs on all three editors and read and answered them from a background thread through the same calls the bridge makes:
  - the scenes-modified prompt (`SaveCurrentModifiedScenesIfUserWantsTo`), with its full message;
  - `DisplayDialog` and `DisplayDialogComplex`;
  - the opt-out variant: a third button in 6000.0, a checkbox in 6000.3+ that is reported as `optOut` and left unchanged;
  - `SaveFilePanel`, where only Cancel is offered and "Save" is refused;
  - a `ShowModalUtility` window, reported as `window` and not clickable.
- Each dialog was detected about 1.1s after it opened. Every click closed its dialog in 25–42ms, and `DisplayDialog`/`DisplayDialogComplex` returned the chosen button's value.
- Stale ids and unknown buttons were refused.
- A progress bar (drawn on another thread) and a plain 2.5s main-thread stall reported no dialog.
- On 6000.3 and 6000.5, Unity's own "Packages with Errors" prompt appeared at startup. `ping` reported it with its message and buttons, and `POST dialog/click` dismissed it over HTTP.
- End to end, with the real MCP server over stdio and a 6000.0 editor over HTTP:
  1. An `execute-code` call that raises the save prompt came back `paused` with the dialog.
  2. A second command was cancelled behind the dialog within 10ms.
  3. A stale id was refused.
  4. "Cancel" returned the held command's real result (`false`, scene still dirty).
  5. A repeat with "Don't Save" returned `true`.

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

