using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Lighting commands: light management, environment settings, light probes, reflection probes,
    /// and lightmap baking with an explicit, verifiable state machine.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPLightingCommands
    {
        // ─── Bake state (SessionState survives domain reloads; a bake can outlive one) ───
        private const string BakeStateKey   = "UnityMCP_Bake_State";
        private const string BakeIdKey      = "UnityMCP_Bake_Id";
        private const string BakeStartedKey = "UnityMCP_Bake_StartedTicks";
        private const string BakeEndedKey   = "UnityMCP_Bake_EndedTicks";
        private const string BakeByKey      = "UnityMCP_Bake_StartedBy";

        private const string StateIdle      = "Idle";
        private const string StateRunning   = "Running";
        private const string StateCompleted = "Completed";
        private const string StateFailed    = "Failed";
        private const string StateCancelled = "Cancelled";

        // isRunning can lag the BakeAsync call by a frame; don't call a bake "ended" before this.
        private const double StartGraceSeconds = 2.0;
        private const int MaxBakeLogs = 20;

        // Warnings/errors logged while a bake runs (the lightmapper reports failures only here).
        private static readonly List<Dictionary<string, object>> _bakeLogs = new List<Dictionary<string, object>>();
        private static bool _capturingBakeLogs;

        static MCPLightingCommands()
        {
            Lightmapping.bakeCompleted += OnBakeCompleted;
            Application.logMessageReceived += CaptureBakeLog;
            _capturingBakeLogs = SessionState.GetString(BakeStateKey, StateIdle) == StateRunning;
        }

        private static void OnBakeCompleted()
        {
            SetBakeEnded(StateCompleted);
        }

        private static void CaptureBakeLog(string condition, string stackTrace, LogType type)
        {
            if (!_capturingBakeLogs || type == LogType.Log || _bakeLogs.Count >= MaxBakeLogs) return;
            string message = condition ?? "";
            if (message.Length > 1000) message = message.Substring(0, 1000) + "…";
            _bakeLogs.Add(new Dictionary<string, object> { { "type", type.ToString() }, { "message", message } });
        }

        private static void SetBakeEnded(string state)
        {
            SessionState.SetString(BakeStateKey, state);
            SessionState.SetString(BakeEndedKey, DateTime.UtcNow.Ticks.ToString());
            _capturingBakeLogs = false;
        }

        private static DateTime? ReadTicks(string key)
        {
            return long.TryParse(SessionState.GetString(key, ""), out long ticks) && ticks > 0
                ? new DateTime(ticks, DateTimeKind.Utc)
                : (DateTime?)null;
        }

        /// <summary>
        /// Start an asynchronous lightmap bake of the open scene(s). Fails fast with a concrete
        /// reason when Unity cannot bake, and verifies that the bake actually started — the
        /// historical failure mode was a "successful" call that never started anything, followed by
        /// an agent polling forever for a bake that did not exist.
        /// </summary>
        public static object Bake(Dictionary<string, object> args)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return BakeError("Cannot bake lighting in Play mode. Stop Play mode first (unity_play_mode action=stop).");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return BakeError("The editor is compiling or importing assets. Retry once it is idle.");

            if (Lightmapping.isRunning)
            {
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "started", false },
                    { "alreadyRunning", true },
                    { "status", BuildBakeStatus() },
                };
            }

            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
                return BakeError("The active scene has never been saved. Unity stores baked lighting next to the scene asset, so save it first (unity_scene_save).");

            var settings = DescribeLightingSettings(out var hints);

            if (MCPArgs.GetBool(args, "clearFirst", false))
                Lightmapping.Clear();

            _bakeLogs.Clear();
            _capturingBakeLogs = true;
            bool started = Lightmapping.BakeAsync();
            if (!started)
            {
                SetBakeEnded(StateFailed);
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "started", false },
                    { "error", "Unity refused to start the bake (Lightmapping.BakeAsync returned false). The reason, if Unity gave one, is in unityConsole." },
                    { "settings", settings },
                    { "hints", hints },
                };
            }

            int bakeId = SessionState.GetInt(BakeIdKey, 0) + 1;
            SessionState.SetInt(BakeIdKey, bakeId);
            SessionState.SetString(BakeStateKey, StateRunning);
            SessionState.SetString(BakeByKey, "mcp");
            SessionState.SetString(BakeStartedKey, DateTime.UtcNow.Ticks.ToString());
            SessionState.EraseString(BakeEndedKey);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "started", true },
                { "bakeId", bakeId },
                { "scene", scene.path },
                { "settings", settings },
                { "hints", hints },
                { "next", "Bake runs asynchronously. Check it with unity_lighting_bake_status; it reports Running/Completed/Failed/Cancelled." },
            };
        }

        public static object GetBakeStatus(Dictionary<string, object> args) => BuildBakeStatus();

        public static object CancelBake(Dictionary<string, object> args)
        {
            if (!Lightmapping.isRunning)
                return new Dictionary<string, object>
                {
                    { "success", true }, { "cancelled", false }, { "message", "No bake is running." }, { "status", BuildBakeStatus() },
                };

            Lightmapping.Cancel();
            SetBakeEnded(StateCancelled);
            return new Dictionary<string, object> { { "success", true }, { "cancelled", true }, { "status", BuildBakeStatus() } };
        }

        public static object ClearBaked(Dictionary<string, object> args)
        {
            if (Lightmapping.isRunning)
                return BakeError("A bake is running. Cancel it first (unity_lighting_bake_cancel).");

            Lightmapping.Clear();
            bool clearedCache = MCPArgs.GetBool(args, "clearDiskCache", false);
            if (clearedCache)
                Lightmapping.ClearDiskCache();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "lightmapCount", LightmapSettings.lightmaps.Length },
                { "clearedDiskCache", clearedCache },
            };
        }

        private static Dictionary<string, object> BuildBakeStatus()
        {
            bool running = Lightmapping.isRunning;
            string state = SessionState.GetString(BakeStateKey, StateIdle);
            DateTime? startedAt = ReadTicks(BakeStartedKey);
            var now = DateTime.UtcNow;

            if (state == StateRunning && !running &&
                (!startedAt.HasValue || (now - startedAt.Value).TotalSeconds > StartGraceSeconds))
            {
                // Stopped without bakeCompleted: a lightmapper failure or a cancel from the Lighting window.
                SetBakeEnded(StateFailed);
                state = StateFailed;
            }
            else if (state != StateRunning && running)
            {
                // A bake started outside MCP (Lighting window "Generate Lighting") — track it too.
                SessionState.SetInt(BakeIdKey, SessionState.GetInt(BakeIdKey, 0) + 1);
                SessionState.SetString(BakeStateKey, StateRunning);
                SessionState.SetString(BakeByKey, "editor");
                SessionState.SetString(BakeStartedKey, now.Ticks.ToString());
                SessionState.EraseString(BakeEndedKey);
                _capturingBakeLogs = true;
                state = StateRunning;
                startedAt = now;
            }

            DateTime? endedAt = ReadTicks(BakeEndedKey);
            var status = new Dictionary<string, object>
            {
                { "state", state },
                { "isRunning", running },
                { "progress", running ? Math.Round(Lightmapping.buildProgress, 3) : (state == StateCompleted ? 1.0 : 0.0) },
                { "bakeId", SessionState.GetInt(BakeIdKey, 0) },
                { "startedBy", state == StateIdle ? null : SessionState.GetString(BakeByKey, "mcp") },
                { "startedAt", startedAt?.ToString("o") },
                { "endedAt", endedAt?.ToString("o") },
                { "elapsedSeconds", startedAt.HasValue ? Math.Round(((endedAt ?? now) - startedAt.Value).TotalSeconds, 1) : 0.0 },
                { "lightmapCount", LightmapSettings.lightmaps.Length },
                { "hint", BakeHint(state) },
            };
            if (_bakeLogs.Count > 0) status["logs"] = new List<Dictionary<string, object>>(_bakeLogs);
            return status;
        }

        private static string BakeHint(string state)
        {
            switch (state)
            {
                case StateRunning:   return "Bake in progress. Check again in a few seconds.";
                case StateCompleted: return "Bake finished. Nothing left to wait for.";
                case StateFailed:    return "The bake stopped without completing. See logs or the console. It is NOT running, so stop polling.";
                case StateCancelled: return "The bake was cancelled. It is NOT running.";
                default:             return "No bake is running and none was started this session. Nothing to wait for.";
            }
        }

        /// <summary>Settings the bake will use, plus hints for configurations that bake nothing.</summary>
        private static Dictionary<string, object> DescribeLightingSettings(out List<string> hints)
        {
            hints = new List<string>();
            var info = new Dictionary<string, object>();

            if (Lightmapping.TryGetLightingSettings(out var settings) && settings != null)
            {
                info["lightingSettingsAsset"] = AssetDatabase.GetAssetPath(settings);
                info["bakedGI"] = settings.bakedGI;
                info["realtimeGI"] = settings.realtimeGI;
                info["lightmapper"] = settings.lightmapper.ToString();
                if (!settings.bakedGI && !settings.realtimeGI)
                    hints.Add("Baked GI and Realtime GI are both disabled, so only probes will bake and no lightmaps will be produced.");
            }
            else
            {
                info["lightingSettingsAsset"] = null;
                hints.Add("The scene has no Lighting Settings asset, so Unity will bake with default settings.");
            }

            int contributors = 0;
#if UNITY_6000_5_OR_NEWER
            var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>();
#else
            var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
#endif
            foreach (var renderer in renderers)
            {
                if ((GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.ContributeGI) != 0)
                    contributors++;
            }
            info["contributeGIRenderers"] = contributors;
            if (contributors == 0)
                hints.Add("No MeshRenderer is marked 'Contribute GI' static, so the bake will produce no lightmaps.");

            return info;
        }

        private static Dictionary<string, object> BakeError(string message)
        {
            return new Dictionary<string, object> { { "success", false }, { "started", false }, { "error", message } };
        }

        public static object GetLightingInfo(Dictionary<string, object> args)
        {
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            var lightList = new List<Dictionary<string, object>>();
            foreach (var light in lights)
            {
                var info = new Dictionary<string, object>
                {
                    { "name", light.gameObject.name },
                    { "instanceId", MCPObjectId.Get(light.gameObject) },
                    { "type", light.type.ToString() },
                    { "color", new Dictionary<string, object> { { "r", light.color.r }, { "g", light.color.g }, { "b", light.color.b }, { "a", light.color.a } } },
                    { "intensity", light.intensity },
                    { "range", light.range },
                    { "spotAngle", light.spotAngle },
                    { "shadows", light.shadows.ToString() },
                    { "enabled", light.enabled },
                    { "renderMode", light.renderMode.ToString() },
                };
                lightList.Add(info);
            }

            return new Dictionary<string, object>
            {
                { "lightCount", lightList.Count },
                { "lights", lightList },
                { "ambientMode", RenderSettings.ambientMode.ToString() },
                { "ambientColor", ColorToDict(RenderSettings.ambientLight) },
                { "ambientIntensity", RenderSettings.ambientIntensity },
                { "fogEnabled", RenderSettings.fog },
                { "fogColor", ColorToDict(RenderSettings.fogColor) },
                { "fogDensity", RenderSettings.fogDensity },
                { "skybox", RenderSettings.skybox != null ? RenderSettings.skybox.name : null },
            };
        }

        public static object CreateLight(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "New Light";
            string typeStr = args.ContainsKey("lightType") ? args["lightType"].ToString() : "Point";

            LightType lightType;
            if (!Enum.TryParse(typeStr, true, out lightType))
                return new { error = $"Invalid light type: {typeStr}. Use Point, Directional, Spot, or Area." };

            var go = new GameObject(name);
            var light = go.AddComponent<Light>();
            light.type = lightType;

            if (args.ContainsKey("color"))
            {
                if (args["color"] != null) light.color = MCPArgs.ToColor(args["color"], "color", rgbDefault: 1f);
            }

            if (args.ContainsKey("intensity"))
                light.intensity = Convert.ToSingle(args["intensity"]);

            if (args.ContainsKey("range"))
                light.range = Convert.ToSingle(args["range"]);

            if (args.ContainsKey("spotAngle"))
                light.spotAngle = Convert.ToSingle(args["spotAngle"]);

            if (args.ContainsKey("shadows"))
            {
                LightShadows shadows;
                if (Enum.TryParse(args["shadows"].ToString(), true, out shadows))
                    light.shadows = shadows;
            }

            if (args.ContainsKey("position"))
                go.transform.position = MCPGameObjectCommands.DictToVector3(args["position"]);

            if (args.ContainsKey("rotation"))
                go.transform.eulerAngles = MCPGameObjectCommands.DictToVector3(args["rotation"]);

            Undo.RegisterCreatedObjectUndo(go, $"Create Light {name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "instanceId", MCPObjectId.Get(go) },
                { "lightType", lightType.ToString() },
                { "intensity", light.intensity },
                { "position", MCPWire.Vec(go.transform.position) },
            };
        }

        public static object SetEnvironment(Dictionary<string, object> args)
        {
            if (args.ContainsKey("ambientMode"))
            {
                AmbientMode mode;
                if (Enum.TryParse(args["ambientMode"].ToString(), true, out mode))
                    RenderSettings.ambientMode = mode;
            }

            if (args.ContainsKey("ambientColor"))
            {
                if (args["ambientColor"] != null) RenderSettings.ambientLight = MCPArgs.ToColor(args["ambientColor"], "ambientColor", rgbDefault: 1f);
            }

            if (args.ContainsKey("ambientIntensity"))
                RenderSettings.ambientIntensity = Convert.ToSingle(args["ambientIntensity"]);

            if (args.ContainsKey("fogEnabled"))
                RenderSettings.fog = Convert.ToBoolean(args["fogEnabled"]);

            if (args.ContainsKey("fogColor"))
            {
                if (args["fogColor"] != null) RenderSettings.fogColor = MCPArgs.ToColor(args["fogColor"], "fogColor", rgbDefault: 1f);
            }

            if (args.ContainsKey("fogDensity"))
                RenderSettings.fogDensity = Convert.ToSingle(args["fogDensity"]);

            if (args.ContainsKey("fogMode"))
            {
                FogMode mode;
                if (Enum.TryParse(args["fogMode"].ToString(), true, out mode))
                    RenderSettings.fogMode = mode;
            }

            if (args.ContainsKey("skyboxMaterialPath"))
            {
                string matPath = args["skyboxMaterialPath"].ToString();
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat != null) RenderSettings.skybox = mat;
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "ambientMode", RenderSettings.ambientMode.ToString() },
                { "ambientColor", ColorToDict(RenderSettings.ambientLight) },
                { "fogEnabled", RenderSettings.fog },
                { "fogColor", ColorToDict(RenderSettings.fogColor) },
                { "fogDensity", RenderSettings.fogDensity },
            };
        }

        public static object CreateReflectionProbe(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "Reflection Probe";

            var go = new GameObject(name);
            var probe = go.AddComponent<ReflectionProbe>();

            if (args.ContainsKey("position"))
                go.transform.position = MCPGameObjectCommands.DictToVector3(args["position"]);

            if (args.ContainsKey("size"))
                probe.size = MCPGameObjectCommands.DictToVector3(args["size"]);

            if (args.ContainsKey("resolution"))
                probe.resolution = Convert.ToInt32(args["resolution"]);

            if (args.ContainsKey("mode"))
            {
                ReflectionProbeMode mode;
                if (Enum.TryParse(args["mode"].ToString(), true, out mode))
                    probe.mode = mode;
            }

            Undo.RegisterCreatedObjectUndo(go, $"Create Reflection Probe {name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "instanceId", MCPObjectId.Get(go) },
                { "position", MCPWire.Vec(go.transform.position) },
                { "size", MCPWire.Vec(probe.size) },
            };
        }

        public static object CreateLightProbeGroup(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "Light Probe Group";

            var go = new GameObject(name);
            var group = go.AddComponent<LightProbeGroup>();

            if (args.ContainsKey("position"))
                go.transform.position = MCPGameObjectCommands.DictToVector3(args["position"]);

            Undo.RegisterCreatedObjectUndo(go, $"Create Light Probe Group {name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "instanceId", MCPObjectId.Get(go) },
                { "probeCount", group.probePositions.Length },
            };
        }

        // ─── Helpers ───

        private static float[] ColorToDict(Color c) => MCPWire.Vec(c);
    }
}
