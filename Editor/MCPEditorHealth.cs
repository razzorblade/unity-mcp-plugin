using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Thread-safe identity + liveness snapshot, readable from the HTTP listener's thread-pool
    /// threads WITHOUT touching the main thread.
    ///
    /// Why: ping used to be dispatched through the main-thread queue, so any main-thread stall
    /// (modal dialog, long import, MPPM play mode throttled in the background) made a live editor
    /// look dead to discovery, and every probe parked a thread-pool thread for the full 30s sync
    /// timeout — enough probes starve the pool and the bridge stops answering even queue/submit.
    ///
    /// Pattern: single writer (main thread, once per editor tick) publishes plain values with
    /// Volatile/Interlocked; any number of readers compose a response from them. Identity is
    /// captured once and never mutated afterwards.
    /// </summary>
    public static class MCPEditorHealth
    {
        /// <summary>
        /// Random id for this domain load. The request queue is static state and does not survive a
        /// domain reload — a changed epoch tells clients that tickets they were waiting on are gone.
        /// </summary>
        public static readonly string Epoch = Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>Stall beyond which the main thread is reported as busy.</summary>
        private const long BusyThresholdMs = 3000;

        [Flags]
        private enum StateFlags
        {
            None           = 0,
            Playing        = 1 << 0,
            Paused         = 1 << 1,
            Compiling      = 1 << 2,
            Updating       = 1 << 3,
            BakingLighting = 1 << 4,
            Focused        = 1 << 5,
        }

        private static long _lastTickUtcTicks;
        private static int _flags;
        private static Dictionary<string, object> _identity;

        // MPPM Virtual Players live in <project>/Library/VP/<player>; the main project is the prefix.
        private static readonly Regex VirtualPlayerPath =
            new Regex(@"^(.*)/Library/VP/[^/]+/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Capture identity. Main thread only (productName, PackageInfo and the MPPM reflection are
        /// main-thread APIs). Idempotent; called before the listener starts accepting requests.
        /// </summary>
        public static void Initialize(string pluginVersion, int protocolVersion)
        {
            string projectPath = MCPAssetSafety.ProjectRoot.Replace('\\', '/').TrimEnd('/');
            bool isVirtualPlayer = MCPScenarioCommands.IsVirtualPlayer();
            string mainProjectPath = null;
            if (isVirtualPlayer)
            {
                var match = VirtualPlayerPath.Match(projectPath);
                if (match.Success) mainProjectPath = match.Groups[1].Value;
            }

            var identity = new Dictionary<string, object>
            {
                { "status", "ok" },
                { "unityVersion", Application.unityVersion },
                { "projectName", Application.productName },
                { "projectPath", projectPath },
                { "platform", Application.platform.ToString() },
                { "isClone", MCPInstanceRegistry.IsParrelSyncClone() },
                { "cloneIndex", MCPInstanceRegistry.GetParrelSyncCloneIndex() },
                { "isVirtualPlayer", isVirtualPlayer },
                { "mainProjectPath", mainProjectPath },
                { "processId", System.Diagnostics.Process.GetCurrentProcess().Id },
                { "protocolVersion", protocolVersion },
                { "pluginVersion", pluginVersion },
                { "epoch", Epoch },
            };
            Volatile.Write(ref _identity, identity);
            MCPDialogProbe.CaptureMainThread();
            Tick();
        }

        /// <summary>Whether this editor is an MPPM Virtual Player (false before Initialize).</summary>
        public static bool IsVirtualPlayer =>
            Volatile.Read(ref _identity) is Dictionary<string, object> id && id["isVirtualPlayer"] is bool vp && vp;

        /// <summary>Main project path of an MPPM Virtual Player, or null.</summary>
        public static string MainProjectPath =>
            Volatile.Read(ref _identity) is Dictionary<string, object> id ? id["mainProjectPath"] as string : null;

        /// <summary>
        /// Publish liveness. Main thread, once per editor update — a handful of native bool getters,
        /// negligible next to the rest of the editor loop.
        /// </summary>
        public static void Tick()
        {
            var flags = StateFlags.None;
            if (EditorApplication.isPlaying)   flags |= StateFlags.Playing;
            if (EditorApplication.isPaused)    flags |= StateFlags.Paused;
            if (EditorApplication.isCompiling) flags |= StateFlags.Compiling;
            if (EditorApplication.isUpdating)  flags |= StateFlags.Updating;
            if (Lightmapping.isRunning)        flags |= StateFlags.BakingLighting;
            if (InternalEditorUtility.isApplicationActive) flags |= StateFlags.Focused;

            Volatile.Write(ref _flags, (int)flags);
            Interlocked.Exchange(ref _lastTickUtcTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>Milliseconds since the main thread last ticked. Any thread.</summary>
        public static long MainThreadStallMs => StallMsSince(LastTickUtcTicks);

        /// <summary>UTC ticks of the last main-thread tick — the start of the current stall. Any thread.</summary>
        public static long LastTickUtcTicks => Interlocked.Read(ref _lastTickUtcTicks);

        private static long StallMsSince(long lastTickUtcTicks)
        {
            if (lastTickUtcTicks == 0) return 0;
            return Math.Max(0, (DateTime.UtcNow.Ticks - lastTickUtcTicks) / TimeSpan.TicksPerMillisecond);
        }

        /// <summary>Answer the dialog blocking the main thread. Any thread (see MCPDialogProbe.Click).</summary>
        public static MCPDialogProbe.ClickResult ClickDialog(string dialogId, string button, string agentId)
        {
            long lastTick = LastTickUtcTicks;
            return MCPDialogProbe.Click(dialogId, button, agentId, StallMsSince(lastTick), lastTick);
        }

        /// <summary>
        /// Full ping payload: identity + live state. Any thread. Field names are a superset of the
        /// historical main-thread ping, so older servers keep working unchanged.
        /// </summary>
        public static Dictionary<string, object> PingPayload()
        {
            var identity = Volatile.Read(ref _identity);
            var payload = identity != null
                ? new Dictionary<string, object>(identity)
                : new Dictionary<string, object> { { "status", "ok" }, { "epoch", Epoch } };
            AppendLiveState(payload);
            payload["queue"] = new Dictionary<string, object>
            {
                { "queued", MCPRequestQueue.TotalQueuedCount },
                { "executing", MCPRequestQueue.CurrentActionName },
            };
            return payload;
        }

        /// <summary>
        /// Compact live state attached to every queue/status response, so a poller can tell
        /// "waiting its turn" from "the editor is stuck" without an extra round-trip.
        /// </summary>
        public static Dictionary<string, object> CompactSnapshot()
        {
            var snapshot = new Dictionary<string, object> { { "epoch", Epoch } };
            AppendLiveState(snapshot);
            return snapshot;
        }

        private static void AppendLiveState(Dictionary<string, object> target)
        {
            var flags = (StateFlags)Volatile.Read(ref _flags);
            long lastTick = LastTickUtcTicks;
            long stallMs = StallMsSince(lastTick);
            string executing = MCPRequestQueue.CurrentActionName;
            long executingTicketId = MCPRequestQueue.ExecutingTicketId;
            var dialog = MCPDialogProbe.Current(stallMs, lastTick);

            target["isPlaying"]          = (flags & StateFlags.Playing) != 0;
            target["isPaused"]           = (flags & StateFlags.Paused) != 0;
            target["isCompiling"]        = (flags & StateFlags.Compiling) != 0;
            target["isUpdating"]         = (flags & StateFlags.Updating) != 0;
            target["isBakingLighting"]   = (flags & StateFlags.BakingLighting) != 0;
            target["applicationFocused"] = (flags & StateFlags.Focused) != 0;
            target["mainThreadStallMs"]  = stallMs;
            target["executing"]          = executing;
            // Lets a poller tell "MY command raised this dialog" from "I am queued behind it".
            if (executing != null && executingTicketId > 0) target["executingTicketId"] = executingTicketId;
            if (dialog != null) target["dialog"] = dialog.ToWire(stallMs);

            string reason = BusyReason(flags, stallMs, executing, dialog);
            target["busy"] = reason != null;
            target["busyReason"] = reason;
        }

        private static string BusyReason(StateFlags flags, long stallMs, string executing, MCPDialogProbe.DialogInfo dialog)
        {
            // A named dialog is the most precise answer, even inside an executing MCP command
            // (usually that command raised it).
            if (dialog != null)
                return dialog.Kind == MCPDialogProbe.KindProgress
                    ? $"long editor operation (progress window \"{dialog.Title}\")"
                    : $"blocked by dialog \"{dialog.Title}\"";
            if (executing != null && stallMs >= BusyThresholdMs)
                return $"executing MCP command '{executing}'";
            if ((flags & StateFlags.Compiling) != 0) return "compiling scripts";
            if ((flags & StateFlags.Updating) != 0)  return "importing assets";
            if (stallMs < BusyThresholdMs) return null;
            return (flags & StateFlags.Focused) != 0
                ? "main thread blocked (modal dialog or long synchronous editor operation)"
                : "main thread blocked or throttled while the editor is unfocused (modal dialog, long operation, or a Multiplayer Play Mode window has focus)";
        }
    }
}
