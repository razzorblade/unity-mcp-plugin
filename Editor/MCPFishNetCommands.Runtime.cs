#if FISHNET_INSTALLED
using System;
using System.Collections.Generic;
using System.IO;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Logging;
using FishNet.Managing.Object;
using FishNet.Managing.Scened;
using FishNet.Managing.Server;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Play Mode half of the FishNet integration: session control and server-authoritative
    /// object operations. Everything here needs a running NetworkManager. The Edit Mode half
    /// and the shared helpers are in MCPFishNetCommands.cs.
    /// </summary>
    public static partial class MCPFishNetCommands
    {
        private const float DefaultStartWaitSeconds = 5f;
        private const float MaxStartWaitSeconds = 30f;

        // ─────────────────────────────────────────────
        //  Session: start / stop
        // ─────────────────────────────────────────────

        /// <summary>
        /// Deferred route. Starting a FishNet connection only schedules it: sockets come up and
        /// the client authenticates over the next frames. Resolving once the requested state is
        /// actually reached (or the wait times out) spares the agent a polling loop.
        /// </summary>
        public static void Start(Dictionary<string, object> args, Action<object> resolve)
        {
            try
            {
                var immediate = BeginStart(args, out NetworkManager nm, out bool server, out bool client, out float wait);
                if (immediate != null) { resolve(immediate); return; }

                string mode = server && client ? "host" : server ? "server" : "client";
                if (wait <= 0f) { resolve(StartResult(nm, mode, server, client, waited: false)); return; }

                WaitUntil(
                    () => !IsPlaying || nm == null || Reached(nm, server, client),
                    wait,
                    () =>
                    {
                        // The ticket only completes through resolve, so it must run even if building the result throws.
                        object result;
                        try { result = StartResult(nm, mode, server, client, waited: true); }
                        catch (Exception ex) { result = Error(ex.Message); }
                        resolve(result);
                    });
            }
            catch (Exception ex)
            {
                resolve(Error(ex.Message));
            }
        }

        /// <summary>Validates and kicks off the start. Returns a response to send right away (an error or a no-op), or null to wait.</summary>
        private static object BeginStart(Dictionary<string, object> args, out NetworkManager nm, out bool server, out bool client, out float wait)
        {
            server = client = false;
            wait = 0f;
            nm = null;

            if (!IsPlaying)
                return Error("FishNet connections run in Play Mode.", "Enter Play Mode first (unity_play_mode), then call fishnet/start.");

            nm = ResolveManager(args, out object error);
            if (nm == null) return error;
            if (!nm.Initialized)
                return Error($"NetworkManager '{nm.name}' did not initialize.", "Check the console: SpawnablePrefabs may be empty, or persistence destroyed this duplicate manager.");

            string mode = (GetString(args, "mode") ?? "host").ToLowerInvariant();
            server = mode == "server" || mode == "host";
            client = mode == "client" || mode == "host";
            if (!server && !client) return Error($"Unknown mode '{mode}'. Use server, client or host.");

            bool hasPort = HasValue(args, "port");
            int port = GetInt(args, "port", 0);
            if (hasPort && (port < 1 || port > ushort.MaxValue)) return Error($"port must be between 1 and 65535 (got {port}).");
            string address = GetString(args, "address");
            wait = Mathf.Clamp(GetFloat(args, "waitSeconds", DefaultStartWaitSeconds), 0f, MaxStartWaitSeconds);

            if (server && !nm.ServerManager.Started)
            {
                bool ok = hasPort ? nm.ServerManager.StartConnection((ushort)port) : nm.ServerManager.StartConnection();
                if (!ok) return Error("The server failed to start.", "Check the console (unity_console_log). The port may already be in use.");
            }
            if (client && !nm.ClientManager.Started)
            {
                bool ok;
                if (address != null && hasPort) ok = nm.ClientManager.StartConnection(address, (ushort)port);
                else if (address != null) ok = nm.ClientManager.StartConnection(address);
                else
                {
                    // A host's client must reach its own server's port.
                    var transport = GetTransport(nm);
                    if (hasPort && transport != null) transport.SetPort((ushort)port);
                    ok = nm.ClientManager.StartConnection();
                }
                if (!ok) return Error("The client failed to start.", "Check the console (unity_console_log) and the transport's client address.");
            }
            return null;
        }

        private static bool Reached(NetworkManager nm, bool server, bool client) =>
            (!server || nm.ServerManager.Started) && (!client || nm.IsClientStarted);

        private static object StartResult(NetworkManager nm, string mode, bool server, bool client, bool waited)
        {
            if (!IsPlaying || nm == null) return Error("Play Mode ended before the connection came up.");

            bool reached = Reached(nm, server, client);
            var d = new Dictionary<string, object>
            {
                { "success", reached || !waited },
                { "mode", mode },
                { "state", StateOf(nm) },
            };
            var transport = GetTransport(nm);
            if (transport != null) d["port"] = transport.GetPort();
            if (client && nm.ClientManager.Started)
            {
                d["clientId"] = nm.ClientManager.Connection.ClientId;
                d["authenticated"] = nm.ClientManager.Connection.IsAuthenticated;
            }

            if (!waited)
                d["note"] = "Start requested without waiting. Poll fishnet/status until state and authenticated settle.";
            else if (!reached)
            {
                string pending = server && !nm.ServerManager.Started ? "the server to start" : "the client to connect and authenticate";
                d["error"] = $"Timed out waiting for {pending}.";
                d["hint"] = "Check the console (unity_console_log). Common causes: the port is in use, the client address is wrong, or an authenticator rejected the client.";
            }
            return d;
        }

        public static object Stop(Dictionary<string, object> args)
        {
            if (!IsPlaying) return Error("Nothing to stop. FishNet only runs in Play Mode.");
            var nm = ResolveManager(args, out object error);
            if (nm == null) return error;
            if (!nm.Initialized) return Error($"NetworkManager '{nm.name}' is not initialized.");

            string mode = (GetString(args, "mode") ?? "all").ToLowerInvariant();
            bool stopServer = mode == "server" || mode == "all" || mode == "host";
            bool stopClient = mode == "client" || mode == "all" || mode == "host";
            if (!stopServer && !stopClient) return Error($"Unknown mode '{mode}'. Use server, client or all.");

            var stopped = new List<string>();
            // Client first, so a host's own client leaves cleanly before the server goes down.
            if (stopClient && nm.ClientManager.Started && nm.ClientManager.StopConnection()) stopped.Add("client");
            if (stopServer && nm.ServerManager.Started && nm.ServerManager.StopConnection(true)) stopped.Add("server");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "stopped", stopped },
                { "state", StateOf(nm) },
            };
        }

        // ─────────────────────────────────────────────
        //  Connections
        // ─────────────────────────────────────────────

        public static object ListConnections(Dictionary<string, object> args)
        {
            var nm = RequireRunning(args, needServer: false, out object error);
            if (nm == null) return error;

            var result = new Dictionary<string, object> { { "state", StateOf(nm) } };
            if (nm.ServerManager.Started)
            {
                var ids = new List<int>(nm.ServerManager.Clients.Keys);
                ids.Sort();
                var rows = new List<Dictionary<string, object>>(ids.Count);
                foreach (int id in ids)
                {
                    var conn = nm.ServerManager.Clients[id];
                    var scenes = new List<string>(conn.Scenes.Count);
                    foreach (var scene in conn.Scenes) scenes.Add(scene.name);
                    rows.Add(new Dictionary<string, object>
                    {
                        { "clientId", id },
                        { "address", conn.GetAddress() },
                        { "authenticated", conn.IsAuthenticated },
                        { "loadedStartScenes", conn.LoadedStartScenes(true) },
                        { "objects", conn.Objects.Count },
                        { "firstObjectId", conn.FirstObject != null ? (object)conn.FirstObject.ObjectId : null },
                        { "scenes", string.Join(",", scenes) },
                        { "isLocal", conn.IsLocalClient },
                    });
                }
                result["clientCount"] = rows.Count;
                result["clients"] = MCPWire.IsVerbose(args) ? (object)rows : MCPWire.Table(rows);
            }
            if (nm.ClientManager.Started)
            {
                var local = nm.ClientManager.Connection;
                result["localClient"] = new Dictionary<string, object>
                {
                    { "clientId", local.ClientId },
                    { "authenticated", local.IsAuthenticated },
                    { "rttMs", nm.TimeManager.RoundTripTime },
                };
            }
            return result;
        }

        public static object Kick(Dictionary<string, object> args)
        {
            var nm = RequireRunning(args, needServer: true, out object error);
            if (nm == null) return error;

            if (!HasValue(args, "clientId")) return Error("clientId is required.", "fishnet/list-connections lists connected clients.");
            int clientId = GetInt(args, "clientId", -1);
            if (!nm.ServerManager.Clients.ContainsKey(clientId))
                return Error($"No connected client with clientId {clientId}.", "fishnet/list-connections lists connected clients.");

            var reason = KickReason.Unset;
            if (HasValue(args, "reason") && !TryParseEnum(args["reason"], out reason))
                return Error($"reason must be one of: {string.Join(", ", Enum.GetNames(typeof(KickReason)))}.");

            string message = GetString(args, "message") ?? string.Empty;
            nm.ServerManager.Kick(clientId, reason, LoggingType.Common, message);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "clientId", clientId },
                { "reason", reason.ToString() },
            };
        }

        // ─────────────────────────────────────────────
        //  Objects: spawn / despawn / ownership
        // ─────────────────────────────────────────────

        public static object Spawn(Dictionary<string, object> args)
        {
            var nm = RequireRunning(args, needServer: true, out object error);
            if (nm == null) return error;

            var prefab = ResolveSpawnablePrefab(args, nm, out error);
            if (prefab == null) return error;

            NetworkConnection owner = null;
            if (HasValue(args, "ownerClientId"))
            {
                int ownerId = GetInt(args, "ownerClientId", -1);
                if (ownerId >= 0 && !nm.ServerManager.Clients.TryGetValue(ownerId, out owner))
                    return Error($"No connected client with clientId {ownerId}.", "fishnet/list-connections lists connected clients.");
            }

            Vector3 position = HasValue(args, "position") ? MCPArgs.ToVector3(args["position"], "position") : prefab.transform.position;
            Quaternion rotation = HasValue(args, "rotation") ? Quaternion.Euler(MCPArgs.ToVector3(args["rotation"], "rotation")) : prefab.transform.rotation;
            int count = Mathf.Clamp(GetInt(args, "count", 1), 1, 100);
            string name = GetString(args, "name");

            var spawned = new List<object>(count);
            for (int i = 0; i < count; i++)
            {
                // Pooled instantiation is FishNet's recommended path; it falls back to Instantiate
                // when the pool is empty.
                var nob = nm.GetPooledInstantiated(prefab, position, rotation, true);
                if (name != null) nob.gameObject.name = count > 1 ? $"{name}_{i}" : name;
                nm.ServerManager.Spawn(nob, owner);
                if (!nob.IsSpawned)
                {
                    UnityEngine.Object.Destroy(nob.gameObject);
                    return new Dictionary<string, object>
                    {
                        { "error", $"FishNet refused to spawn '{prefab.name}'." },
                        { "hint", "Check the console (unity_console_log) for FishNet's reason." },
                        { "spawned", spawned },
                    };
                }
                spawned.Add(ObjectRef(nob));
            }

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "prefab", prefab.name },
                { "spawned", spawned },
            };
            if (owner != null) result["ownerClientId"] = owner.ClientId;
            return result;
        }

        public static object Despawn(Dictionary<string, object> args)
        {
            var nm = RequireRunning(args, needServer: true, out object error);
            if (nm == null) return error;

            var nob = ResolveServerSpawned(args, nm, out error);
            if (nob == null) return error;

            DespawnType? despawnType = null;
            if (HasValue(args, "despawnType"))
            {
                if (!TryParseEnum(args["despawnType"], out DespawnType parsed))
                    return Error($"despawnType must be one of: {string.Join(", ", Enum.GetNames(typeof(DespawnType)))}.");
                despawnType = parsed;
            }

            var reference = ObjectRef(nob);
            string appliedType = (despawnType ?? nob.GetDefaultDespawnType()).ToString();
            nm.ServerManager.Despawn(nob, despawnType);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "despawned", reference },
                { "despawnType", appliedType },
            };
        }

        public static object SetOwnership(Dictionary<string, object> args)
        {
            var nm = RequireRunning(args, needServer: true, out object error);
            if (nm == null) return error;

            var nob = ResolveServerSpawned(args, nm, out error);
            if (nob == null) return error;

            if (!HasValue(args, "clientId")) return Error("clientId is required (-1 removes ownership).");
            int clientId = GetInt(args, "clientId", -1);
            bool includeNested = GetBool(args, "includeNested", false);

            if (clientId < 0)
            {
                nob.RemoveOwnership(includeNested);
            }
            else
            {
                if (!nm.ServerManager.Clients.TryGetValue(clientId, out var conn))
                    return Error($"No connected client with clientId {clientId}.", "fishnet/list-connections lists connected clients.");
                nob.GiveOwnership(conn);
                if (includeNested)
                {
                    foreach (var child in nob.GetComponentsInChildren<NetworkObject>(true))
                        if (child != nob && child.IsSpawned) child.GiveOwnership(conn);
                }
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "objectId", nob.ObjectId },
                { "name", nob.name },
                { "ownerId", nob.OwnerId },
            };
        }

        // ─────────────────────────────────────────────
        //  Networked scenes
        // ─────────────────────────────────────────────

        public static object LoadScene(Dictionary<string, object> args)
        {
            var nm = RequireRunning(args, needServer: true, out object error);
            if (nm == null) return error;

            var scenes = ResolveSceneNames(args, out error);
            if (scenes == null) return error;

            var replace = ReplaceOption.None;
            string replaceSpec = GetString(args, "replace");
            if (replaceSpec != null)
            {
                switch (replaceSpec.ToLowerInvariant())
                {
                    case "none": replace = ReplaceOption.None; break;
                    case "online": case "onlineonly": replace = ReplaceOption.OnlineOnly; break;
                    case "all": replace = ReplaceOption.All; break;
                    default: return Error($"replace must be none, online or all (got '{replaceSpec}').");
                }
            }

            var connections = ResolveConnections(args, nm, out error);
            if (error != null) return error;

            var data = new SceneLoadData(scenes.ToArray()) { ReplaceScenes = replace };
            if (connections == null) nm.SceneManager.LoadGlobalScenes(data);
            else nm.SceneManager.LoadConnectionScenes(connections, data);

            return SceneResult(scenes, connections, "load", replace.ToString());
        }

        public static object UnloadScene(Dictionary<string, object> args)
        {
            var nm = RequireRunning(args, needServer: true, out object error);
            if (nm == null) return error;

            var scenes = ResolveSceneNames(args, out error);
            if (scenes == null) return error;

            var connections = ResolveConnections(args, nm, out error);
            if (error != null) return error;

            var data = new SceneUnloadData(scenes.ToArray());
            if (connections == null) nm.SceneManager.UnloadGlobalScenes(data);
            else nm.SceneManager.UnloadConnectionScenes(connections, data);

            return SceneResult(scenes, connections, "unload", null);
        }

        private static object SceneResult(List<string> scenes, NetworkConnection[] connections, string action, string replace)
        {
            var d = new Dictionary<string, object>
            {
                { "success", true },
                { "queued", scenes },
                { "scope", connections == null ? "global" : "connections" },
                { "note", $"The {action} is queued. FishNet runs it over the next frames and replicates it to clients as they are ready." },
            };
            if (replace != null) d["replace"] = replace;
            if (connections != null)
            {
                var ids = new List<int>(connections.Length);
                foreach (var c in connections) ids.Add(c.ClientId);
                d["clientIds"] = ids;
            }
            return d;
        }

        /// <summary>
        /// FishNet loads scenes by name through Unity's SceneManager, which only sees scenes in
        /// Build Settings. Checking up front turns a console-only failure into a direct error.
        /// </summary>
        private static List<string> ResolveSceneNames(Dictionary<string, object> args, out object error)
        {
            error = null;
            var requested = GetStringList(args, "scenes");
            if (requested.Count == 0) requested = GetStringList(args, "scene");
            if (requested.Count == 0)
            {
                error = Error("scenes (array of scene names) is required.");
                return null;
            }

            var names = new List<string>(requested.Count);
            foreach (string spec in requested)
            {
                string found = null;
                foreach (var entry in EditorBuildSettings.scenes)
                {
                    if (!entry.enabled) continue;
                    string sceneName = Path.GetFileNameWithoutExtension(entry.path);
                    if (entry.path == spec || sceneName == spec) { found = sceneName; break; }
                }
                if (found == null)
                {
                    error = Error($"Scene '{spec}' is not an enabled scene in Build Settings, so it can't be loaded at runtime.",
                        "Add it under File > Build Settings, or with the build-settings MCP tools.");
                    return null;
                }
                names.Add(found);
            }
            return names;
        }

        /// <summary>Null (no clientIds argument) means global scope.</summary>
        private static NetworkConnection[] ResolveConnections(Dictionary<string, object> args, NetworkManager nm, out object error)
        {
            error = null;
            if (!HasValue(args, "clientIds")) return null;
            if (!(args["clientIds"] is IList<object> raw) || raw.Count == 0)
            {
                error = Error("clientIds must be a non-empty array of client ids.");
                return null;
            }

            var connections = new NetworkConnection[raw.Count];
            var cell = new Dictionary<string, object>(1); // reuse MCPArgs' strict integer parsing per element
            for (int i = 0; i < raw.Count; i++)
            {
                cell["clientIds"] = raw[i];
                int id = MCPArgs.GetInt(cell, "clientIds", -1);
                if (!nm.ServerManager.Clients.TryGetValue(id, out connections[i]))
                {
                    error = Error($"No connected client with clientId {id}.", "fishnet/list-connections lists connected clients.");
                    return null;
                }
            }
            return connections;
        }

        // ─────────────────────────────────────────────
        //  Runtime resolution helpers
        // ─────────────────────────────────────────────

        private static NetworkManager RequireRunning(Dictionary<string, object> args, bool needServer, out object error)
        {
            error = null;
            if (!IsPlaying)
            {
                error = Error("This needs a running FishNet session.", "Enter Play Mode (unity_play_mode), then fishnet/start.");
                return null;
            }
            var nm = ResolveManager(args, out error);
            if (nm == null) return null;
            if (!nm.Initialized)
            {
                error = Error($"NetworkManager '{nm.name}' is not initialized. Check the console.");
                return null;
            }
            if (needServer && !nm.ServerManager.Started)
            {
                error = Error("The server is not started on this editor. Spawning and other authority operations run on the server.",
                    "Call fishnet/start with mode server or host. From a client-only editor, use the editor that hosts.");
                return null;
            }
            if (!needServer && !nm.ServerManager.Started && !nm.ClientManager.Started)
            {
                error = Error("Networking is not started.", "Call fishnet/start.");
                return null;
            }
            return nm;
        }

        private static NetworkObject ResolveServerSpawned(Dictionary<string, object> args, NetworkManager nm, out object error)
        {
            error = null;
            if (HasValue(args, "objectId"))
            {
                int id = GetInt(args, "objectId", -1);
                if (nm.ServerManager.Objects.Spawned.TryGetValue(id, out var byId)) return byId;
                error = Error($"No server-spawned object with objectId {id}.", "fishnet/list-network-objects lists spawned objects.");
                return null;
            }

            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null)
            {
                error = Error("Target not found. Pass objectId, path or instanceId.");
                return null;
            }
            var nob = go.GetComponent<NetworkObject>();
            if (nob == null || !nob.IsServerInitialized)
            {
                error = Error($"'{go.name}' is not a server-spawned NetworkObject.");
                return null;
            }
            return nob;
        }

        /// <summary>A prefab to spawn by <c>prefabId</c>, asset path, or name, checked against the collections FishNet spawns from.</summary>
        private static NetworkObject ResolveSpawnablePrefab(Dictionary<string, object> args, NetworkManager nm, out object error)
        {
            error = null;
            var collection = nm.SpawnablePrefabs;
            if (collection == null)
            {
                error = Error("The NetworkManager has no SpawnablePrefabs collection.");
                return null;
            }

            if (HasValue(args, "prefabId"))
            {
                int id = GetInt(args, "prefabId", -1);
                int count = collection.GetObjectCount();
                if (id < 0 || id >= count)
                {
                    error = Error($"prefabId {id} is out of range (0-{count - 1}).", "fishnet/list-prefabs lists prefab ids.");
                    return null;
                }
                var byId = collection.GetObject(true, id);
                if (byId == null) error = Error($"prefabId {id} points at a missing prefab.");
                return byId;
            }

            string spec = GetString(args, "prefab");
            if (spec == null)
            {
                error = Error("prefab (asset path or name) or prefabId is required.");
                return null;
            }

            NetworkObject prefab;
            if (spec.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryLoadPrefab(spec, out prefab, out error)) return null;
            }
            else
            {
                prefab = FindPrefabByName(collection, spec, out error);
                if (prefab == null) return null;
            }

            if (!IsRegistered(collection, prefab) && !IsRuntimeRegistered(nm, prefab))
            {
                error = Error($"'{prefab.name}' is not in the NetworkManager's spawnable prefabs, so FishNet can't spawn it.",
                    "Exit Play Mode and run fishnet/register-prefab (or fishnet/refresh-prefabs for DefaultPrefabObjects).");
                return null;
            }
            return prefab;
        }

        private static NetworkObject FindPrefabByName(PrefabObjects collection, string name, out object error)
        {
            error = null;
            NetworkObject exact = null, loose = null;
            int exactCount = 0, looseCount = 0;
            int count = collection.GetObjectCount();
            for (int i = 0; i < count; i++)
            {
                var nob = collection.GetObject(true, i);
                if (nob == null) continue;
                if (nob.name == name) { exact = nob; exactCount++; }
                else if (nob.name.Equals(name, StringComparison.OrdinalIgnoreCase)) { loose = nob; looseCount++; }
            }

            if (exactCount == 1) return exact;
            if (exactCount == 0 && looseCount == 1) return loose;
            error = exactCount + looseCount == 0
                ? Error($"No spawnable prefab named '{name}'.", "fishnet/list-prefabs lists registered prefabs. You can also pass an asset path.")
                : Error($"'{name}' matches {exactCount + looseCount} spawnable prefabs.", "Pass the asset path or prefabId instead.");
            return null;
        }

        private static bool IsRuntimeRegistered(NetworkManager nm, NetworkObject prefab)
        {
            foreach (var collection in nm.RuntimeSpawnablePrefabs.Values)
                if (collection != nm.SpawnablePrefabs && IsRegistered(collection, prefab)) return true;
            return false;
        }

        private static Dictionary<string, object> ObjectRef(NetworkObject nob) => new Dictionary<string, object>
        {
            { "objectId", nob.ObjectId },
            { "name", nob.name },
            { "instanceId", MCPObjectId.Get(nob.gameObject) },
        };

        /// <summary>Invoke <paramref name="done"/> on the first editor frame where <paramref name="condition"/> holds, or after the timeout.</summary>
        private static void WaitUntil(Func<bool> condition, float timeoutSeconds, Action done)
        {
            double deadline = EditorApplication.timeSinceStartup + timeoutSeconds;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                bool met;
                try { met = condition(); }
                catch { met = true; } // the result builder reports whatever state is left
                if (!met && EditorApplication.timeSinceStartup < deadline) return;
                EditorApplication.update -= tick;
                done();
            };
            EditorApplication.update += tick;
        }
    }
}
#endif
