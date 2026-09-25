using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
#if FISHNET_INSTALLED
using System.Collections;
using System.Reflection;
using FishNet.Component.Spawning;
using FishNet.Component.Transforming;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Transporting;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Object.Synchronizing.Internal;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using FishNetSceneManager = FishNet.Managing.Scened.SceneManager;
#endif

namespace UnityMCP.Editor
{
    /// <summary>
    /// Fish-Networking integration (com.firstgeargames.fishnet 4.x). Covers the everyday work
    /// that otherwise needs execute-code snippets:
    /// <list type="bullet">
    /// <item>Edit Mode setup: NetworkManager + transport, NetworkObjects on scene objects and
    /// prefabs, the spawnable-prefab collection. Every scene mutation is Undo-tracked.</item>
    /// <item>Inspection in both modes: managers, network objects with their SyncType values, prefabs.</item>
    /// <item>Play Mode session control: start/stop server, client or host, connections, spawn,
    /// despawn, ownership, kick and networked scene loads.</item>
    /// </list>
    /// Play Mode handlers live in MCPFishNetCommands.Runtime.cs.
    ///
    /// The whole surface is gated on FISHNET_INSTALLED (asmdef versionDefine on the UPM package).
    /// Without FishNet every handler returns a clear "not installed" error instead of failing to compile.
    /// </summary>
    public static partial class MCPFishNetCommands
    {
#if !FISHNET_INSTALLED
        private static object NotInstalled()
        {
            return new Dictionary<string, object>
            {
                { "error", "FishNet (com.firstgeargames.fishnet 4.x) is not installed as a package. Install it to use fishnet/* tools." },
                { "hint", "Package Manager > + > Add package from git URL > https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet. " +
                          "For a copy imported into Assets/, add FISHNET_INSTALLED to Player > Scripting Define Symbols." },
            };
        }

        public static object GetStatus(Dictionary<string, object> args) => NotInstalled();
        public static object SetupNetworkManager(Dictionary<string, object> args) => NotInstalled();
        public static object ConfigureTransport(Dictionary<string, object> args) => NotInstalled();
        public static object AddNetworkObject(Dictionary<string, object> args) => NotInstalled();
        public static object GetNetworkObject(Dictionary<string, object> args) => NotInstalled();
        public static object ListNetworkObjects(Dictionary<string, object> args) => NotInstalled();
        public static object ListPrefabs(Dictionary<string, object> args) => NotInstalled();
        public static object RefreshPrefabs(Dictionary<string, object> args) => NotInstalled();
        public static object RegisterPrefab(Dictionary<string, object> args) => NotInstalled();
        public static void Start(Dictionary<string, object> args, Action<object> resolve) => resolve(NotInstalled());
        public static object Stop(Dictionary<string, object> args) => NotInstalled();
        public static object ListConnections(Dictionary<string, object> args) => NotInstalled();
        public static object Spawn(Dictionary<string, object> args) => NotInstalled();
        public static object Despawn(Dictionary<string, object> args) => NotInstalled();
        public static object SetOwnership(Dictionary<string, object> args) => NotInstalled();
        public static object Kick(Dictionary<string, object> args) => NotInstalled();
        public static object LoadScene(Dictionary<string, object> args) => NotInstalled();
        public static object UnloadScene(Dictionary<string, object> args) => NotInstalled();
#else
        private const string RefreshPrefabsMenu = "Tools/Fish-Networking/Utility/Refresh Default Prefabs";

        private static bool IsPlaying => EditorApplication.isPlaying;

        // ─────────────────────────────────────────────
        //  Status
        // ─────────────────────────────────────────────

        public static object GetStatus(Dictionary<string, object> args)
        {
            var managers = FindManagers();
            var described = new List<object>(managers.Count);
            foreach (var nm in managers) described.Add(DescribeManager(nm));

            var result = new Dictionary<string, object>
            {
                { "fishnetVersion", NetworkManager.FISHNET_VERSION },
                { "isPlaying", IsPlaying },
                { "networkManagers", described },
            };

            var dpo = FindDefaultPrefabObjects();
            if (dpo != null)
            {
                result["defaultPrefabObjects"] = new Dictionary<string, object>
                {
                    { "path", AssetDatabase.GetAssetPath(dpo) },
                    { "count", dpo.GetObjectCount() },
                };
            }

            if (managers.Count == 0)
                result["hint"] = "No NetworkManager in the loaded scenes. Create one with fishnet/setup-network-manager.";
            else if (!IsPlaying)
                result["hint"] = "Networking runs in Play Mode: enter it (unity_play_mode), then fishnet/start.";
            return result;
        }

        private static Dictionary<string, object> DescribeManager(NetworkManager nm)
        {
            var d = new Dictionary<string, object>
            {
                { "path", MCPWire.HierarchyPath(nm.transform) },
                { "instanceId", MCPObjectId.Get(nm.gameObject) },
                { "scene", nm.gameObject.scene.name },
                { "state", StateOf(nm) },
            };

            var transport = GetTransport(nm);
            if (transport != null)
            {
                d["transport"] = transport.GetType().Name;
                // Port and client address are safe to read offline: Tugboat falls back to its
                // serialized values while its sockets aren't running.
                d["port"] = transport.GetPort();
                string address = transport.GetClientAddress();
                if (!string.IsNullOrEmpty(address)) d["clientAddress"] = address;
            }
            else
            {
                d["transport"] = null; // FishNet adds Tugboat on Awake when none is assigned.
            }

            var prefabs = nm.SpawnablePrefabs;
            d["spawnablePrefabs"] = prefabs == null ? null : new Dictionary<string, object>
            {
                { "collection", CollectionLabel(prefabs) },
                { "count", prefabs.GetObjectCount() },
            };

            if (IsPlaying && nm.Initialized)
            {
                if (nm.ServerManager.Started)
                {
                    d["clients"] = nm.ServerManager.Clients.Count;
                    d["serverSpawned"] = nm.ServerManager.Objects.Spawned.Count;
                }
                if (nm.ClientManager.Started)
                {
                    var conn = nm.ClientManager.Connection;
                    d["clientId"] = conn.ClientId;
                    d["authenticated"] = conn.IsAuthenticated;
                    d["clientSpawned"] = nm.ClientManager.Objects.Spawned.Count;
                    d["rttMs"] = nm.TimeManager.RoundTripTime;
                }
                d["tick"] = nm.TimeManager.Tick;
                d["tickRate"] = nm.TimeManager.TickRate;
            }
            return d;
        }

        // ─────────────────────────────────────────────
        //  NetworkManager setup
        // ─────────────────────────────────────────────

        public static object SetupNetworkManager(Dictionary<string, object> args)
        {
            if (IsPlaying)
                return Error("Set up the NetworkManager in Edit Mode. Play Mode changes are lost on exit.");

            // Validate every input before touching the scene, so a bad argument can't leave a
            // half-built manager behind.
            string targetPath = GetString(args, "path");
            GameObject target = null;
            if (targetPath != null)
            {
                target = FindSceneObject(targetPath);
                if (target == null) return Error($"GameObject '{targetPath}' not found.");
                if (target.GetComponent<NetworkObject>() != null)
                    return Error($"'{targetPath}' has a NetworkObject. FishNet forbids a NetworkManager and a NetworkObject on the same GameObject.");
            }
            else
            {
                var existing = FindManagers();
                if (existing.Count > 0 && !GetBool(args, "allowMultiple", false))
                    return Error($"A NetworkManager already exists at '{MCPWire.HierarchyPath(existing[0].transform)}'.",
                        "Configure it with fishnet/configure-transport, pass path to target a specific object, or allowMultiple:true to add another.");
            }

            // Only an explicit transport replaces an existing one; the default just fills a gap.
            string transportSpec = GetString(args, "transport");
            Type transportType = ResolveTransportType(transportSpec, out object transportError);
            if (transportError != null) return transportError;

            PrefabObjects collection = null;
            string collectionPath = GetString(args, "spawnablePrefabs");
            if (collectionPath != null)
            {
                collection = AssetDatabase.LoadAssetAtPath<PrefabObjects>(collectionPath);
                if (collection == null) return Error($"No PrefabObjects asset at '{collectionPath}'.", "fishnet/list-prefabs shows the collection in use.");
            }

            NetworkObject playerPrefab = null;
            string playerPrefabPath = GetString(args, "playerPrefab");
            if (playerPrefabPath != null && !TryLoadPrefab(playerPrefabPath, out playerPrefab, out object prefabError))
                return prefabError;

            var spawnPoints = new List<Transform>();
            foreach (string spawnPath in GetStringList(args, "spawnPoints"))
            {
                var spawn = FindSceneObject(spawnPath);
                if (spawn == null) return Error($"Spawn point '{spawnPath}' not found.");
                spawnPoints.Add(spawn.transform);
            }

            // ── Mutations ──
            var added = new List<string>();
            var warnings = new List<string>();
            GameObject go = target;
            if (go == null)
            {
                go = new GameObject(GetString(args, "name") ?? "NetworkManager");
                Undo.RegisterCreatedObjectUndo(go, "MCP FishNet: Create NetworkManager");
            }

            var nm = go.GetComponent<NetworkManager>();
            if (nm == null)
            {
                // Adding the component in Edit Mode runs NetworkManager.Reset, which assigns
                // DefaultPrefabObjects automatically.
                nm = Undo.AddComponent<NetworkManager>(go);
                added.Add(nameof(NetworkManager));
            }

            if (collection != null || nm.SpawnablePrefabs == null)
            {
                var assign = collection != null ? collection : FindDefaultPrefabObjects();
                if (assign != null)
                {
                    Undo.RecordObject(nm, "MCP FishNet: Assign SpawnablePrefabs");
                    nm.SpawnablePrefabs = assign;
                }
                else
                {
                    warnings.Add("No DefaultPrefabObjects asset found, so SpawnablePrefabs is empty. Run fishnet/refresh-prefabs, then call this again.");
                }
            }

            var tm = go.GetComponent<TransportManager>();
            if (tm == null)
            {
                tm = Undo.AddComponent<TransportManager>(go);
                added.Add(nameof(TransportManager));
            }

            Transport transport = tm.Transport;
            if (transport == null || (transportSpec != null && transport.GetType() != transportType))
            {
                if (transport != null)
                    warnings.Add($"Replaced transport {transport.GetType().Name} with {transportType.Name}. The old component is still on the GameObject.");
                transport = go.GetComponent(transportType) as Transport;
                if (transport == null)
                {
                    transport = (Transport)Undo.AddComponent(go, transportType);
                    added.Add(transportType.Name);
                }
                Undo.RecordObject(tm, "MCP FishNet: Assign Transport");
                tm.Transport = transport;
            }

            if (GetBool(args, "addAllManagers", false))
            {
                // FishNet creates these on Awake when missing. Adding them now exposes their
                // settings in the Inspector (tick rate, observer conditions, scene handling).
                AddIfMissing<FishNet.Managing.Server.ServerManager>(go, added);
                AddIfMissing<FishNet.Managing.Client.ClientManager>(go, added);
                AddIfMissing<FishNet.Managing.Timing.TimeManager>(go, added);
                AddIfMissing<FishNetSceneManager>(go, added);
                AddIfMissing<FishNet.Managing.Observing.ObserverManager>(go, added);
            }

            var applied = ApplyTransportSettings(transport, args, true, out object settingsError);
            if (settingsError != null) return settingsError;

            if (playerPrefab != null)
            {
                var spawner = go.GetComponent<PlayerSpawner>();
                if (spawner == null)
                {
                    spawner = Undo.AddComponent<PlayerSpawner>(go);
                    added.Add(nameof(PlayerSpawner));
                }
                Undo.RecordObject(spawner, "MCP FishNet: Configure PlayerSpawner");
                spawner.SetPlayerPrefab(playerPrefab);
                if (spawnPoints.Count > 0) spawner.Spawns = spawnPoints.ToArray();
                PrefabUtility.RecordPrefabInstancePropertyModifications(spawner);
                if (!IsRegistered(nm.SpawnablePrefabs, playerPrefab))
                    warnings.Add($"Player prefab '{playerPrefab.name}' is not in SpawnablePrefabs yet. Run fishnet/register-prefab.");
            }

            PrefabUtility.RecordPrefabInstancePropertyModifications(nm);
            PrefabUtility.RecordPrefabInstancePropertyModifications(tm);

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "path", MCPWire.HierarchyPath(go.transform) },
                { "instanceId", MCPObjectId.Get(go) },
                { "added", added },
                { "transport", transport.GetType().Name },
                { "port", transport.GetPort() },
                { "spawnablePrefabs", nm.SpawnablePrefabs != null ? CollectionLabel(nm.SpawnablePrefabs) : null },
                { "hint", "Save the scene, enter Play Mode, then fishnet/start (mode: host)." },
            };
            if (applied.Count > 0) result["transportSettings"] = applied;
            if (playerPrefab != null) result["playerPrefab"] = AssetDatabase.GetAssetPath(playerPrefab);
            if (warnings.Count > 0) result["warnings"] = warnings;
            return result;
        }

        public static object ConfigureTransport(Dictionary<string, object> args)
        {
            var nm = ResolveManager(args, out object error);
            if (nm == null) return error;

            var transport = GetTransport(nm);
            if (transport == null)
                return Error($"NetworkManager '{nm.name}' has no transport.", "fishnet/setup-network-manager with path set to the manager adds Tugboat.");

            var applied = ApplyTransportSettings(transport, args, !IsPlaying, out error);
            if (error != null) return error;

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "transport", transport.GetType().Name },
                { "port", transport.GetPort() },
                { "clientAddress", transport.GetClientAddress() },
            };
            if (applied.Count > 0) result["applied"] = applied;

            if (IsPlaying)
            {
                result["maxClients"] = transport.GetMaximumClients();
                result["note"] = "This is a runtime change. It applies to the next start and is lost when Play Mode exits.";
            }
            else
            {
                // Offline, the serialized fields are the source of truth (some runtime getters
                // read socket state that is only set up on start).
                result["settings"] = MCPPropertyReader.FromArgs(args).Read(new SerializedObject(transport), null, true)["properties"];
            }
            return result;
        }

        /// <summary>
        /// Apply the transport-agnostic settings through FishNet's Transport API. Values are
        /// validated before anything changes. Tugboat's setters write its serialized fields,
        /// so with <paramref name="recordUndo"/> the change persists in the scene and is undoable.
        /// </summary>
        private static List<string> ApplyTransportSettings(Transport transport, Dictionary<string, object> args, bool recordUndo, out object error)
        {
            error = null;
            var applied = new List<string>();

            bool hasPort = HasValue(args, "port");
            int port = GetInt(args, "port", 0);
            if (hasPort && (port < 0 || port > ushort.MaxValue))
            {
                error = Error($"port must be between 0 and 65535 (got {port}).");
                return applied;
            }

            bool hasMaxClients = HasValue(args, "maxClients");
            int maxClients = GetInt(args, "maxClients", 0);
            if (hasMaxClients && maxClients < 1)
            {
                error = Error($"maxClients must be at least 1 (got {maxClients}).");
                return applied;
            }

            string clientAddress = GetString(args, "clientAddress");
            string ipv4 = GetString(args, "bindAddressIPv4");
            string ipv6 = GetString(args, "bindAddressIPv6");
            if (!hasPort && !hasMaxClients && clientAddress == null && ipv4 == null && ipv6 == null)
                return applied;

            if (recordUndo) Undo.RecordObject(transport, "MCP FishNet: Configure Transport");
            if (hasPort) { transport.SetPort((ushort)port); applied.Add("port"); }
            if (hasMaxClients) { transport.SetMaximumClients(maxClients); applied.Add("maxClients"); }
            if (clientAddress != null) { transport.SetClientAddress(clientAddress); applied.Add("clientAddress"); }
            if (ipv4 != null) { transport.SetServerBindAddress(ipv4, IPAddressType.IPv4); applied.Add("bindAddressIPv4"); }
            if (ipv6 != null) { transport.SetServerBindAddress(ipv6, IPAddressType.IPv6); applied.Add("bindAddressIPv6"); }
            if (recordUndo) PrefabUtility.RecordPrefabInstancePropertyModifications(transport);
            return applied;
        }

        private static Type ResolveTransportType(string spec, out object error)
        {
            error = null;
            if (string.IsNullOrEmpty(spec) || spec.Equals("tugboat", StringComparison.OrdinalIgnoreCase))
                return typeof(Tugboat);

            var candidates = new List<string>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<Transport>())
            {
                if (type.IsAbstract) continue;
                if (type.Name.Equals(spec, StringComparison.OrdinalIgnoreCase) || type.FullName == spec)
                    return type;
                candidates.Add(type.Name);
            }
            candidates.Sort(StringComparer.Ordinal);
            error = Error($"Unknown transport '{spec}'. Available: {string.Join(", ", candidates)}.");
            return null;
        }

        private static void AddIfMissing<T>(GameObject go, List<string> added) where T : Component
        {
            if (go.GetComponent<T>() != null) return;
            Undo.AddComponent<T>(go);
            added.Add(typeof(T).Name);
        }

        // ─────────────────────────────────────────────
        //  NetworkObjects
        // ─────────────────────────────────────────────

        /// <summary>Tool argument → NetworkObject serialized field. One table drives both writes and reads.</summary>
        private static readonly (string arg, string field)[] NobSettings =
        {
            ("isNetworked", "_isNetworked"),
            ("isSpawnable", "_isSpawnable"),
            ("isGlobal", "_isGlobal"),
            ("initializeOrder", "_initializeOrder"),
            ("preventDespawnOnDisconnect", "_preventDespawnOnDisconnect"),
            ("defaultDespawnType", "_defaultDespawnType"),
        };

        public static object AddNetworkObject(Dictionary<string, object> args)
        {
            // Parse settings up front: a bad value must fail before the component is added.
            var settings = ParseNobSettings(args, out object error);
            if (error != null) return error;
            bool addTransform = GetBool(args, "networkTransform", false);

            string prefabPath = GetString(args, "prefabPath");
            if (prefabPath != null)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                    return Error($"Prefab '{prefabPath}' not found.");

                Dictionary<string, object> result = null;
                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    result = ConfigureNetworkObject(root, settings, addTransform, false);
                    if (result.ContainsKey("error")) return result;
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }

                // Saving triggers FishNet's prefab generator, which adds the prefab to
                // DefaultPrefabObjects when auto-generation is enabled.
                var saved = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<NetworkObject>();
                result["prefabPath"] = prefabPath;
                result["registered"] = IsRegistered(FindActiveCollection(), saved);
                if (!(bool)result["registered"])
                    result["hint"] = "Not in the spawnable prefabs yet. Run fishnet/register-prefab so it can be spawned.";
                return result;
            }

            if (IsPlaying)
                return Error("Add NetworkObjects in Edit Mode. At runtime, spawn registered prefabs with fishnet/spawn.");

            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return Error("GameObject not found. Pass path or instanceId for a scene object, or prefabPath for a prefab asset.");

            var sceneResult = ConfigureNetworkObject(go, settings, addTransform, true);
            if (!sceneResult.ContainsKey("error"))
            {
                sceneResult["path"] = MCPWire.HierarchyPath(go.transform);
                sceneResult["instanceId"] = MCPObjectId.Get(go);
            }
            return sceneResult;
        }

        private static Dictionary<string, object> ConfigureNetworkObject(GameObject go, Dictionary<string, object> settings, bool addTransform, bool undo)
        {
            if (go.GetComponent<NetworkManager>() != null)
                return Error($"'{go.name}' has a NetworkManager. FishNet forbids a NetworkObject on the same GameObject.");

            var added = new List<string>();
            var nob = go.GetComponent<NetworkObject>();
            if (nob == null)
            {
                nob = undo ? Undo.AddComponent<NetworkObject>(go) : go.AddComponent<NetworkObject>();
                added.Add(nameof(NetworkObject));
            }
            if (addTransform && go.GetComponent<NetworkTransform>() == null)
            {
                if (undo) Undo.AddComponent<NetworkTransform>(go);
                else go.AddComponent<NetworkTransform>();
                added.Add(nameof(NetworkTransform));
            }

            var warnings = new List<string>();
            var applied = WriteNobSettings(nob, settings, undo, warnings);

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "added", added },
                { "settings", ReadNobSettings(nob) },
            };
            if (applied.Count > 0) result["applied"] = applied;
            if (warnings.Count > 0) result["warnings"] = warnings;
            return result;
        }

        private static Dictionary<string, object> ParseNobSettings(Dictionary<string, object> args, out object error)
        {
            error = null;
            var parsed = new Dictionary<string, object>();
            foreach (var (arg, _) in NobSettings)
            {
                if (!HasValue(args, arg)) continue;
                switch (arg)
                {
                    case "initializeOrder":
                        int order = GetInt(args, arg, 0);
                        if (order < sbyte.MinValue || order > sbyte.MaxValue)
                        {
                            error = Error($"initializeOrder must be between {sbyte.MinValue} and {sbyte.MaxValue} (got {order}).");
                            return null;
                        }
                        parsed[arg] = order;
                        break;
                    case "defaultDespawnType":
                        if (!TryParseEnum(args[arg], out DespawnType despawnType))
                        {
                            error = Error($"defaultDespawnType must be one of: {string.Join(", ", Enum.GetNames(typeof(DespawnType)))}.");
                            return null;
                        }
                        parsed[arg] = (int)despawnType;
                        break;
                    default:
                        parsed[arg] = GetBool(args, arg, false);
                        break;
                }
            }
            return parsed;
        }

        /// <summary>
        /// Write through SerializedObject so the change is Undo-able, marks prefab overrides
        /// correctly, and doesn't trip the runtime guards in FishNet's setters (SetIsGlobal
        /// refuses offline objects).
        /// </summary>
        private static List<string> WriteNobSettings(NetworkObject nob, Dictionary<string, object> parsed, bool undo, List<string> warnings)
        {
            var applied = new List<string>();
            if (parsed.Count == 0) return applied;

            var so = new SerializedObject(nob);
            foreach (var (arg, field) in NobSettings)
            {
                if (!parsed.TryGetValue(arg, out object value)) continue;
                var prop = so.FindProperty(field);
                if (prop == null)
                {
                    warnings.Add($"{arg} is not supported by this FishNet version (field {field} not found).");
                    continue;
                }
                if (prop.propertyType == SerializedPropertyType.Boolean) prop.boolValue = (bool)value;
                else prop.intValue = (int)value; // sbyte and enum fields both take intValue
                applied.Add(arg);
            }
            if (undo) so.ApplyModifiedProperties();
            else so.ApplyModifiedPropertiesWithoutUndo();
            return applied;
        }

        private static Dictionary<string, object> ReadNobSettings(NetworkObject nob)
        {
            var so = new SerializedObject(nob);
            var d = new Dictionary<string, object>();
            foreach (var (arg, field) in NobSettings)
            {
                var prop = so.FindProperty(field);
                if (prop == null) continue;
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Boolean: d[arg] = prop.boolValue; break;
                    case SerializedPropertyType.Enum:
                        int index = prop.enumValueIndex;
                        d[arg] = index >= 0 && index < prop.enumNames.Length ? prop.enumNames[index] : (object)prop.intValue;
                        break;
                    default: d[arg] = prop.intValue; break;
                }
            }
            return d;
        }

        public static object GetNetworkObject(Dictionary<string, object> args)
        {
            var nob = ResolveNetworkObject(args, out object error);
            if (nob == null) return error;

            int maxItems = Mathf.Clamp(GetInt(args, "maxItems", 20), 0, 1000);
            var d = new Dictionary<string, object> { { "name", nob.name } };
            if (EditorUtility.IsPersistent(nob))
            {
                d["prefabPath"] = AssetDatabase.GetAssetPath(nob);
            }
            else
            {
                d["path"] = MCPWire.HierarchyPath(nob.transform);
                d["instanceId"] = MCPObjectId.Get(nob.gameObject);
            }
            d["settings"] = ReadNobSettings(nob);
            if (nob.PrefabId != NetworkObject.UNSET_PREFABID_VALUE) d["prefabId"] = nob.PrefabId;
            if (nob.IsSceneObject) d["isSceneObject"] = true;

            if (IsPlaying)
            {
                d["isSpawned"] = nob.IsSpawned;
                if (nob.IsSpawned)
                {
                    d["objectId"] = nob.ObjectId;
                    d["ownerId"] = nob.OwnerId;
                    d["isServerInitialized"] = nob.IsServerInitialized;
                    d["isClientInitialized"] = nob.IsClientInitialized;
                    if (nob.IsClientInitialized) d["isOwner"] = nob.IsOwner;
                    if (nob.IsServerInitialized) d["observers"] = nob.Observers.Count;
                }
            }

            var behaviours = new List<object>();
            var nested = new List<string>();
            foreach (var nb in nob.GetComponentsInChildren<NetworkBehaviour>(true))
            {
                if (OwningNetworkObject(nb.transform) != nob) continue; // belongs to a nested NetworkObject
                var entry = new Dictionary<string, object> { { "type", nb.GetType().Name } };
                if (nb.transform != nob.transform) entry["on"] = RelativePath(nob.transform, nb.transform);
                if (IsPlaying && nob.IsSpawned) entry["componentIndex"] = nb.ComponentIndex;
                var syncTypes = ReadSyncTypes(nb, maxItems);
                if (syncTypes.Count > 0) entry["syncTypes"] = syncTypes;
                behaviours.Add(entry);
            }
            foreach (var child in nob.GetComponentsInChildren<NetworkObject>(true))
                if (child != nob) nested.Add(RelativePath(nob.transform, child.transform));

            d["behaviours"] = behaviours;
            if (nested.Count > 0) d["nestedNetworkObjects"] = nested;
            return d;
        }

        public static object ListNetworkObjects(Dictionary<string, object> args)
        {
            int limit = Mathf.Clamp(GetInt(args, "limit", 200), 1, 5000);
            string nameFilter = GetString(args, "nameFilter");
            bool verbose = MCPWire.IsVerbose(args);

            NetworkManager nm = null;
            if (IsPlaying && (HasValue(args, "networkManager") || FindManagers().Count > 0))
            {
                nm = ResolveManager(args, out object managerError);
                if (nm == null) return managerError;
            }

            var rows = new List<Dictionary<string, object>>();
            int total = 0;
            string source;

            if (nm != null && nm.Initialized && (nm.ServerManager.Started || nm.ClientManager.Started))
            {
                string side = (GetString(args, "side") ?? (nm.ServerManager.Started ? "server" : "client")).ToLowerInvariant();
                if (side != "server" && side != "client") return Error("side must be 'server' or 'client'.");
                bool asServer = side == "server";
                if (asServer && !nm.ServerManager.Started) return Error("The server is not started on this editor.", "Pass side:'client', or start the server with fishnet/start.");
                if (!asServer && !nm.ClientManager.Started) return Error("The client is not started on this editor.", "Pass side:'server', or start the client with fishnet/start.");

                source = side + "-spawned";
                var spawned = asServer ? nm.ServerManager.Objects.Spawned : nm.ClientManager.Objects.Spawned;
                var ids = new List<int>(spawned.Keys);
                ids.Sort();
                foreach (int id in ids)
                {
                    var nob = spawned[id];
                    if (nob == null || !Matches(nob.name, nameFilter)) continue;
                    if (++total > limit) continue;
                    var row = new Dictionary<string, object>
                    {
                        { "path", MCPWire.HierarchyPath(nob.transform) },
                        { "objectId", id },
                        { "ownerId", nob.OwnerId },
                        { "prefabId", nob.PrefabId == NetworkObject.UNSET_PREFABID_VALUE ? null : (object)nob.PrefabId },
                        { "sceneObject", nob.IsSceneObject },
                    };
                    if (asServer) row["observers"] = nob.Observers.Count;
                    rows.Add(row);
                }
            }
            else
            {
                source = "scene";
                var all = FindAllInScenes<NetworkObject>();
                foreach (var nob in all)
                {
                    if (EditorUtility.IsPersistent(nob) || !Matches(nob.name, nameFilter)) continue;
                    if (++total > limit) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        { "path", MCPWire.HierarchyPath(nob.transform) },
                        { "instanceId", MCPObjectId.Get(nob.gameObject) },
                        { "isNetworked", nob.GetIsNetworked() },
                        { "isGlobal", nob.IsGlobal },
                        { "nested", nob.transform.parent != null && OwningNetworkObject(nob.transform.parent) != null },
                        { "behaviours", CountOwnBehaviours(nob) },
                    });
                }
                rows.Sort((a, b) => string.CompareOrdinal((string)a["path"], (string)b["path"]));
            }

            var result = new Dictionary<string, object>
            {
                { "source", source },
                { "total", total },
                { "objects", verbose ? (object)rows : MCPWire.Table(rows, "path", "name", "parent") },
            };
            if (total > limit) { result["truncated"] = true; result["limit"] = limit; }
            if (source == "scene" && IsPlaying)
                result["note"] = "Networking is not started, so this lists the scene's NetworkObjects. Start it with fishnet/start.";
            return result;
        }

        // ─────────────────────────────────────────────
        //  Spawnable prefabs
        // ─────────────────────────────────────────────

        public static object ListPrefabs(Dictionary<string, object> args)
        {
            var collection = ResolveCollection(args, out object error);
            if (collection == null) return error;

            int count = collection.GetObjectCount();
            int offset = Mathf.Clamp(GetInt(args, "offset", 0), 0, Math.Max(count, 0));
            int limit = Mathf.Clamp(GetInt(args, "limit", 200), 1, 5000);
            int end = Math.Min(count, offset + limit);

            // At runtime FishNet sorts a copy of the collection, and a prefab's index in that
            // copy is the prefabId fishnet/spawn accepts. Offline, the asset order is only an index.
            string idColumn = IsPlaying ? "prefabId" : "index";
            var rows = new List<Dictionary<string, object>>(Math.Max(end - offset, 0));
            int missing = 0;
            for (int i = offset; i < end; i++)
            {
                var nob = collection.GetObject(true, i);
                if (nob == null) missing++;
                rows.Add(new Dictionary<string, object>
                {
                    { "path", nob != null ? AssetDatabase.GetAssetPath(nob) : "<missing>" },
                    { idColumn, i },
                });
            }

            var result = new Dictionary<string, object>
            {
                { "collection", CollectionLabel(collection) },
                { "type", collection.GetType().Name },
                { "count", count },
                { "prefabs", MCPWire.IsVerbose(args) ? (object)rows : MCPWire.Table(rows, "path", "name", "folder") },
            };
            if (offset > 0) result["offset"] = offset;
            if (end < count) result["nextOffset"] = end;
            if (missing > 0) result["warning"] = $"{missing} entries are missing (deleted prefabs). Run fishnet/refresh-prefabs.";
            return result;
        }

        public static object RefreshPrefabs(Dictionary<string, object> args)
        {
            if (IsPlaying) return Error("Refresh the prefab collection in Edit Mode.");

            var before = FindDefaultPrefabObjects();
            int previousCount = before != null ? before.GetObjectCount() : 0;
            if (!EditorApplication.ExecuteMenuItem(RefreshPrefabsMenu))
                return Error($"FishNet's '{RefreshPrefabsMenu}' menu item was not found.", "This FishNet version may have moved it. Check the Tools > Fish-Networking menu.");

            var dpo = FindDefaultPrefabObjects();
            if (dpo == null)
                return Error("No DefaultPrefabObjects asset after the refresh.", "Enable the prefab generator in Tools > Fish-Networking > Configuration.");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "defaultPrefabObjects", AssetDatabase.GetAssetPath(dpo) },
                { "count", dpo.GetObjectCount() },
                { "previousCount", previousCount },
            };
        }

        public static object RegisterPrefab(Dictionary<string, object> args)
        {
            if (IsPlaying) return Error("Register prefabs in Edit Mode. Runtime collection changes are lost on exit.");

            string prefabPath = GetString(args, "prefabPath");
            if (prefabPath == null) return Error("prefabPath is required.");
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (go == null) return Error($"Prefab '{prefabPath}' not found.");

            var collection = ResolveCollection(args, out object error);
            if (collection == null) return error;
            if (collection is DualPrefabObjects)
                return Error("DualPrefabObjects collections hold server/client pairs and can't register a single prefab.", "Edit the collection in the Inspector, or use a SinglePrefabObjects collection.");

            bool addedNob = false;
            var nob = go.GetComponent<NetworkObject>();
            if (nob == null)
            {
                if (!GetBool(args, "addNetworkObject", false))
                    return Error($"'{prefabPath}' has no NetworkObject.", "Pass addNetworkObject:true, or call fishnet/add-network-object with prefabPath first.");
                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    if (root.GetComponent<NetworkManager>() != null)
                        return Error("That prefab holds a NetworkManager, which can't also carry a NetworkObject.");
                    root.AddComponent<NetworkObject>();
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
                nob = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<NetworkObject>();
                addedNob = true;
            }

            bool already = IsRegistered(collection, nob);
            if (!already)
            {
                if (collection is DefaultPrefabObjects)
                {
                    // DefaultPrefabObjects belongs to FishNet's generator, which rebuilds it from
                    // the search folders. A manual add would be wiped on the next rebuild, so
                    // regenerate and check that the prefab was picked up.
                    EditorApplication.ExecuteMenuItem(RefreshPrefabsMenu);
                    if (!IsRegistered(collection, nob))
                        return Error("FishNet's prefab generator did not pick the prefab up. It is probably outside the generator's search folders.",
                            "Check Tools > Fish-Networking > Configuration > Prefab Objects Generator, or give the NetworkManager a SinglePrefabObjects collection and register there.");
                }
                else
                {
                    Undo.RecordObject(collection, "MCP FishNet: Register Prefab");
                    collection.AddObject(nob, checkForDuplicates: true);
                    EditorUtility.SetDirty(collection);
                    AssetDatabase.SaveAssetIfDirty(collection);
                }
            }

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "prefabPath", prefabPath },
                { "collection", CollectionLabel(collection) },
                { "count", collection.GetObjectCount() },
            };
            if (already) result["alreadyRegistered"] = true;
            if (addedNob) result["addedNetworkObject"] = true;
            return result;
        }

        // ─────────────────────────────────────────────
        //  SyncType inspection
        // ─────────────────────────────────────────────

        private static readonly Dictionary<Type, FieldInfo[]> SyncFieldCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<Type, PropertyInfo> ValuePropertyCache = new Dictionary<Type, PropertyInfo>();

        /// <summary>SyncVar/SyncList/SyncDictionary/... fields declared between the concrete type and NetworkBehaviour, cached per type.</summary>
        private static FieldInfo[] GetSyncFields(Type type)
        {
            if (SyncFieldCache.TryGetValue(type, out var cached)) return cached;
            var fields = new List<FieldInfo>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t != typeof(NetworkBehaviour); t = t.BaseType)
                foreach (var f in t.GetFields(flags))
                    if (typeof(SyncBase).IsAssignableFrom(f.FieldType)) fields.Add(f);
            var array = fields.ToArray();
            SyncFieldCache[type] = array;
            return array;
        }

        private static List<object> ReadSyncTypes(NetworkBehaviour nb, int maxItems)
        {
            var fields = GetSyncFields(nb.GetType());
            var list = new List<object>(fields.Length);
            foreach (var field in fields)
            {
                var entry = new Dictionary<string, object>
                {
                    { "name", FieldDisplayName(field) },
                    { "kind", FriendlyTypeName(field.FieldType) },
                };
                try
                {
                    entry["value"] = SyncValue(field.GetValue(nb), maxItems);
                }
                catch (Exception ex)
                {
                    entry["error"] = (ex.InnerException ?? ex).Message;
                }
                list.Add(entry);
            }
            return list;
        }

        private static object SyncValue(object sync, int maxItems)
        {
            switch (sync)
            {
                case null: return null;
                case SyncTimer timer:
                    return new Dictionary<string, object> { { "remaining", timer.Remaining }, { "duration", timer.Duration }, { "paused", timer.Paused } };
                case SyncStopwatch stopwatch:
                    return new Dictionary<string, object> { { "elapsed", stopwatch.Elapsed }, { "paused", stopwatch.Paused } };
            }

            var type = sync.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SyncVar<>))
            {
                if (!ValuePropertyCache.TryGetValue(type, out var valueProperty))
                {
                    valueProperty = type.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                    ValuePropertyCache[type] = valueProperty;
                }
                return valueProperty != null ? FormatValue(valueProperty.GetValue(sync), maxItems, 0) : sync.ToString();
            }
            // SyncList / SyncHashSet / SyncDictionary all enumerate.
            return FormatValue(sync, maxItems, 0);
        }

        /// <summary>A runtime value as JSON-ready data: primitives as-is, vectors as arrays, objects by name, collections capped at <paramref name="maxItems"/>.</summary>
        private static object FormatValue(object value, int maxItems, int depth)
        {
            switch (value)
            {
                case null: return null;
                case string s: return s;
                case bool _: case int _: case long _: case short _: case byte _:
                case uint _: case ushort _: case sbyte _: case float _: case double _:
                    return value;
                case ulong ul: return ul.ToString(); // may exceed JavaScript's safe-integer range
                case Enum e: return e.ToString();
                case Vector2 v2: return MCPWire.Vec(v2);
                case Vector3 v3: return MCPWire.Vec(v3);
                case Vector4 v4: return MCPWire.Vec(v4);
                case Quaternion q: return MCPWire.Vec(q);
                case Color c: return MCPWire.Vec(c);
                case Color32 c32: return MCPWire.Vec((Color)c32);
                case NetworkConnection conn: return conn.IsValid ? (object)conn.ClientId : null;
                case NetworkObject nob:
                    if (nob == null) return null;
                    return nob.IsSpawned ? (object)new Dictionary<string, object> { { "objectId", nob.ObjectId }, { "name", nob.name } } : nob.name;
                case UnityEngine.Object uo:
                    return uo == null ? null : $"{uo.name} ({uo.GetType().Name})";
            }

            if (value is IEnumerable enumerable && depth < 2)
            {
                var items = new List<object>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ < maxItems) items.Add(FormatEntry(item, maxItems, depth + 1));
                }
                if (count <= maxItems) return items;
                return new Dictionary<string, object> { { "count", count }, { "items", items } };
            }
            return value.ToString();
        }

        /// <summary>Dictionary entries (KeyValuePair of any types) become [key, value]; anything else is formatted as a value.</summary>
        private static object FormatEntry(object item, int maxItems, int depth)
        {
            if (item != null)
            {
                var type = item.GetType();
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                {
                    return new List<object>
                    {
                        FormatValue(type.GetProperty("Key").GetValue(item), maxItems, depth),
                        FormatValue(type.GetProperty("Value").GetValue(item), maxItems, depth),
                    };
                }
            }
            return FormatValue(item, maxItems, depth);
        }

        /// <summary>Auto-property backing fields ("&lt;Health&gt;k__BackingField") report as the property name.</summary>
        private static string FieldDisplayName(FieldInfo field)
        {
            string name = field.Name;
            if (name.Length > 0 && name[0] == '<')
            {
                int close = name.IndexOf('>');
                if (close > 1) return name.Substring(1, close - 1);
            }
            return name;
        }

        private static string FriendlyTypeName(Type type)
        {
            if (!type.IsGenericType) return type.Name;
            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            var args = type.GetGenericArguments();
            var parts = new string[args.Length];
            for (int i = 0; i < args.Length; i++) parts[i] = FriendlyTypeName(args[i]);
            return $"{name}<{string.Join(",", parts)}>";
        }

        // ─────────────────────────────────────────────
        //  Resolution helpers
        // ─────────────────────────────────────────────

        private static List<NetworkManager> FindManagers()
        {
            var result = new List<NetworkManager>();
            // At runtime FishNet tracks the initialized managers (persistence already destroyed
            // duplicates). Offline, and before Awake, scan the loaded scenes.
            if (IsPlaying)
            {
                foreach (var nm in NetworkManager.Instances)
                    if (nm != null) result.Add(nm);
                if (result.Count > 0) return result;
            }
            foreach (var nm in FindAllInScenes<NetworkManager>())
                if (!EditorUtility.IsPersistent(nm)) result.Add(nm);
            return result;
        }

        private static NetworkManager ResolveManager(Dictionary<string, object> args, out object error)
        {
            error = null;
            string spec = GetString(args, "networkManager");
            if (spec != null)
            {
                var go = FindSceneObject(spec);
                var named = go != null ? go.GetComponent<NetworkManager>() : null;
                if (named == null) error = Error($"No NetworkManager at '{spec}'.", "fishnet/status lists the NetworkManagers in the loaded scenes.");
                return named;
            }

            var all = FindManagers();
            if (all.Count == 1) return all[0];
            if (all.Count == 0)
            {
                error = Error("No NetworkManager in the loaded scenes.", "Create one with fishnet/setup-network-manager.");
                return null;
            }
            var paths = new List<string>(all.Count);
            foreach (var nm in all) paths.Add(MCPWire.HierarchyPath(nm.transform));
            error = new Dictionary<string, object>
            {
                { "error", $"{all.Count} NetworkManagers found. Pass networkManager (a hierarchy path)." },
                { "candidates", paths },
            };
            return null;
        }

        /// <summary>
        /// A NetworkObject by <c>objectId</c> (Play Mode, server side first), <c>prefabPath</c>
        /// (prefab asset), or scene <c>path</c> / <c>instanceId</c>.
        /// </summary>
        private static NetworkObject ResolveNetworkObject(Dictionary<string, object> args, out object error)
        {
            error = null;
            if (HasValue(args, "objectId"))
            {
                if (!IsPlaying) { error = Error("objectId only exists in Play Mode. Pass path, instanceId or prefabPath."); return null; }
                var nm = ResolveManager(args, out error);
                if (nm == null) return null;
                var nob = FindSpawned(nm, GetInt(args, "objectId", -1), preferServer: true);
                if (nob == null) error = Error($"No spawned object with objectId {GetInt(args, "objectId", -1)}.", "fishnet/list-network-objects lists spawned objects.");
                return nob;
            }

            string prefabPath = GetString(args, "prefabPath");
            if (prefabPath != null)
            {
                return TryLoadPrefab(prefabPath, out var prefab, out error) ? prefab : null;
            }

            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null)
            {
                error = Error("GameObject not found. Pass objectId (Play Mode), path, instanceId or prefabPath.");
                return null;
            }
            var found = go.GetComponent<NetworkObject>();
            if (found == null) error = Error($"'{go.name}' has no NetworkObject.", "Add one with fishnet/add-network-object.");
            return found;
        }

        private static NetworkObject FindSpawned(NetworkManager nm, int objectId, bool preferServer)
        {
            if (!nm.Initialized) return null;
            NetworkObject nob;
            if (preferServer && nm.ServerManager.Started && nm.ServerManager.Objects.Spawned.TryGetValue(objectId, out nob)) return nob;
            if (nm.ClientManager.Started && nm.ClientManager.Objects.Spawned.TryGetValue(objectId, out nob)) return nob;
            if (!preferServer && nm.ServerManager.Started && nm.ServerManager.Objects.Spawned.TryGetValue(objectId, out nob)) return nob;
            return null;
        }

        private static bool TryLoadPrefab(string path, out NetworkObject prefab, out object error)
        {
            prefab = null;
            error = null;
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
            {
                error = Error($"Prefab '{path}' not found.");
                return false;
            }
            prefab = go.GetComponent<NetworkObject>();
            if (prefab == null)
            {
                error = Error($"Prefab '{path}' has no NetworkObject on its root.", "Add one with fishnet/add-network-object (prefabPath).");
                return false;
            }
            return true;
        }

        /// <summary>An explicit <c>collection</c> asset, else the NetworkManager's collection, else DefaultPrefabObjects.</summary>
        private static PrefabObjects ResolveCollection(Dictionary<string, object> args, out object error)
        {
            error = null;
            string path = GetString(args, "collection");
            if (path != null)
            {
                var asset = AssetDatabase.LoadAssetAtPath<PrefabObjects>(path);
                if (asset == null) error = Error($"No PrefabObjects asset at '{path}'.");
                return asset;
            }

            if (HasValue(args, "networkManager") || FindManagers().Count > 0)
            {
                var nm = ResolveManager(args, out error);
                if (nm == null) return null;
                if (nm.SpawnablePrefabs != null) return nm.SpawnablePrefabs;
            }

            var dpo = FindDefaultPrefabObjects();
            if (dpo == null)
                error = Error("No spawnable prefab collection found.", "Run fishnet/refresh-prefabs to generate DefaultPrefabObjects, or fishnet/setup-network-manager.");
            return dpo;
        }

        /// <summary>The collection FishNet will spawn from: the single NetworkManager's, else DefaultPrefabObjects.</summary>
        private static PrefabObjects FindActiveCollection()
        {
            var managers = FindManagers();
            if (managers.Count == 1 && managers[0].SpawnablePrefabs != null) return managers[0].SpawnablePrefabs;
            return FindDefaultPrefabObjects();
        }

        private static DefaultPrefabObjects FindDefaultPrefabObjects()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:DefaultPrefabObjects"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) return asset;
            }
            return null;
        }

        private static bool IsRegistered(PrefabObjects collection, NetworkObject prefab)
        {
            if (collection == null || prefab == null) return false;
            if (collection is SinglePrefabObjects single)
            {
                foreach (var p in single.Prefabs)
                    if (p == prefab) return true;
                return false;
            }
            int count = collection.GetObjectCount();
            for (int i = 0; i < count; i++)
                if (collection.GetObject(true, i) == prefab) return true;
            return false;
        }

        /// <summary>Asset path offline. At runtime FishNet swaps in a sorted, unnamed in-memory copy, so label it by type.</summary>
        private static string CollectionLabel(PrefabObjects collection)
        {
            string path = AssetDatabase.GetAssetPath(collection);
            if (!string.IsNullOrEmpty(path)) return path;
            return string.IsNullOrEmpty(collection.name) ? collection.GetType().Name + " (runtime copy)" : collection.name;
        }

        private static Transport GetTransport(NetworkManager nm)
        {
            // TransportManager is cached on Awake, so offline read the components directly.
            var tm = nm.TransportManager != null ? nm.TransportManager : nm.GetComponent<TransportManager>();
            if (tm != null && tm.Transport != null) return tm.Transport;
            return nm.GetComponent<Transport>();
        }

        private static string StateOf(NetworkManager nm)
        {
            if (!IsPlaying || !nm.Initialized) return "offline";
            bool server = nm.ServerManager.Started;
            bool client = nm.ClientManager.Started;
            return server && client ? "host" : server ? "server" : client ? "client" : "offline";
        }

        private static NetworkObject OwningNetworkObject(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
            {
                var nob = cur.GetComponent<NetworkObject>();
                if (nob != null) return nob;
            }
            return null;
        }

        private static int CountOwnBehaviours(NetworkObject nob)
        {
            int count = 0;
            foreach (var nb in nob.GetComponentsInChildren<NetworkBehaviour>(true))
                if (OwningNetworkObject(nb.transform) == nob) count++;
            return count;
        }

        private static string RelativePath(Transform root, Transform t)
        {
            string full = MCPWire.HierarchyPath(t);
            string prefix = MCPWire.HierarchyPath(root) + "/";
            return full.StartsWith(prefix, StringComparison.Ordinal) ? full.Substring(prefix.Length) : full;
        }

        /// <summary>Every loaded-scene instance of <typeparamref name="T"/>, inactive included (Unity 6.5 deprecates the sort-mode overload).</summary>
        private static T[] FindAllInScenes<T>() where T : UnityEngine.Object
        {
#if UNITY_6000_5_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include);
#else
            return UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#endif
        }

        private static GameObject FindSceneObject(string path) =>
            MCPGameObjectCommands.FindGameObject(new Dictionary<string, object> { { "path", path } });

        private static bool Matches(string name, string filter) =>
            filter == null || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool TryParseEnum<T>(object raw, out T value) where T : struct, Enum
        {
            value = default;
            if (raw == null) return false;
            string s = raw.ToString();
            // Only named values: Enum.TryParse would also accept any integer.
            return !int.TryParse(s, out _) && Enum.TryParse(s, true, out value) && Enum.IsDefined(typeof(T), value);
        }

        private static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            var list = new List<string>();
            if (args == null || !args.TryGetValue(key, out object raw) || raw == null) return list;
            if (raw is IList items)
            {
                foreach (var item in items)
                    if (item != null && item.ToString().Length > 0) list.Add(item.ToString());
            }
            else if (raw.ToString().Length > 0)
            {
                list.Add(raw.ToString());
            }
            return list;
        }
#endif

        // ─── Shared arg helpers (available in both branches) ───

        private static Dictionary<string, object> Error(string message, string hint = null)
        {
            var d = new Dictionary<string, object> { { "error", message } };
            if (hint != null) d["hint"] = hint;
            return d;
        }

        private static bool HasValue(Dictionary<string, object> args, string key) =>
            args != null && args.TryGetValue(key, out object value) && value != null;

        private static string GetString(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null) return null;
            string s = value.ToString();
            return s.Length > 0 ? s : null;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int def) => MCPArgs.GetInt(args, key, def);

        private static float GetFloat(Dictionary<string, object> args, string key, float def) => MCPArgs.GetFloat(args, key, def);

        private static bool GetBool(Dictionary<string, object> args, string key, bool def) => MCPArgs.GetBool(args, key, def);
    }
}
