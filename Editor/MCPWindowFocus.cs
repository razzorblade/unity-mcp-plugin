using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Brings the editor to the foreground on request, and hands the foreground back afterwards.
    ///
    /// Why: an editor that is not the foreground window is throttled. The Game view repaints at a
    /// few frames per second and Play Mode crawls, so an agent playtesting or capturing frames
    /// sees stale, choppy output. Focusing the window restores full frame rate.
    ///
    /// How (Windows): runs on the HTTP pool threads, because the throttled main thread is the
    /// thing being rescued. Windows only lets the foreground process (or the one that received the
    /// last input) take the foreground, so a plain SetForegroundWindow from a background editor
    /// usually just flashes its taskbar button. Escalation, stopping at the first that works:
    ///   1. SetForegroundWindow;
    ///   2. attach to the foreground window's input queue, then BringWindowToTop + SetForegroundWindow;
    ///   3. inject a zero-distance mouse move (makes this process the last input provider), retry.
    /// The result is verified with GetForegroundWindow, never assumed. Focusing the Game view tab
    /// is a main-thread step the caller schedules separately (<see cref="FocusGameView"/>).
    /// Other platforms: unsupported.
    /// </summary>
    public static class MCPWindowFocus
    {
        public enum FocusOutcome
        {
            Focused,          // the editor was brought to the foreground
            AlreadyFocused,   // the editor already had it
            Refused,          // Windows kept another window in front
            Returned,         // the foreground went back to the window that had it before
            NothingToReturn,  // no earlier focus call left a window to return to
            UserMovedOn,      // the user switched windows since; the foreground is left alone
            Disabled,         // the project's dashboard setting forbids it
            NoWindow,         // the main editor window could not be resolved
            Unsupported,      // not the Windows editor
        }

        public sealed class FocusResult
        {
            public readonly FocusOutcome Outcome;
            public readonly string Method;
            public readonly string Error;

            public FocusResult(FocusOutcome outcome, string method = null, string error = null)
            {
                Outcome = outcome;
                Method  = method;
                Error   = error;
            }

            /// <summary>Whether the editor holds the foreground after the call.</summary>
            public bool EditorInFront => Outcome == FocusOutcome.Focused || Outcome == FocusOutcome.AlreadyFocused;
        }

        /// <summary>Longest supported automatic hand-back delay.</summary>
        public const int MaxReturnAfterMs = 600_000;

        private static int _enabled = 1;

        /// <summary>
        /// Mirror of the project setting, readable off the main thread (EditorPrefs is main-thread
        /// only). Written by <see cref="MCPSettingsManager.AllowWindowFocus"/>.
        /// </summary>
        public static bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            internal set => Volatile.Write(ref _enabled, value ? 1 : 0);
        }

#if UNITY_EDITOR_WIN
        public static bool Supported => true;
#else
        public static bool Supported => false;
#endif

        private static readonly ConcurrentQueue<MCPActionRecord> _pendingAudit = new ConcurrentQueue<MCPActionRecord>();

        /// <summary>Feed queued focus records into the action history. Main thread (editor update).</summary>
        public static void FlushAudit()
        {
            if (_pendingAudit.IsEmpty) return;
            while (_pendingAudit.TryDequeue(out var record))
            {
                try { MCPActionHistory.RecordAction(record); }
                catch (Exception ex) { Debug.LogWarning($"[AB-UMCP] Failed to record focus change: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Focus the Game view tab (opening one when none exists), so it is the view that renders.
        /// Main thread only. Returns "focused" or "opened".
        /// </summary>
        public static string FocusGameView()
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null) return "unavailable";

            var existing = Resources.FindObjectsOfTypeAll(gameViewType);
            var view = existing.Length > 0 ? (EditorWindow)existing[0] : null;
            bool opened = view == null;
            if (opened) view = EditorWindow.GetWindow(gameViewType);

            // Focus also selects a docked tab hidden behind another one; a hidden tab never renders.
            view.Focus();
            view.Repaint();
            return opened ? "opened" : "focused";
        }

        private static void Audit(string action, FocusResult result, string agentId, string detail)
        {
            bool ok = result.Outcome != FocusOutcome.Refused;
            _pendingAudit.Enqueue(new MCPActionRecord
            {
                Timestamp    = DateTime.UtcNow,
                AgentId      = agentId ?? "anonymous",
                ActionName   = action,
                Category     = "editor",
                Status       = ok ? "Completed" : "Failed",
                ErrorMessage = ok ? null : result.Error,
                TargetPath   = detail ?? "",
                TargetType   = "Window",
                Parameters   = new Dictionary<string, string>
                {
                    { "outcome", result.Outcome.ToString() },
                    { "method",  result.Method ?? "" },
                },
            });
        }

#if UNITY_EDITOR_WIN
        // ═══════════════════════════════════════════════════════
        //  Windows
        // ═══════════════════════════════════════════════════════

        private static readonly object _gate = new object();
        private static readonly uint _pid = (uint)Process.GetCurrentProcess().Id;

        // The window that was in front before the editor took the foreground; focus returns to it.
        private static IntPtr _returnTo;
        private static string _returnToTitle;
        private static Timer _returnTimer;

        /// <summary>
        /// Bring the main editor window to the foreground. Any thread. With returnAfterMs > 0 the
        /// foreground is handed back to the previous window after that delay, unless the user has
        /// switched windows in the meantime.
        /// </summary>
        public static FocusResult Focus(int returnAfterMs, string agentId)
        {
            if (!Enabled)
                return new FocusResult(FocusOutcome.Disabled, error:
                    "Bringing the editor to the front is disabled for this project (Dashboard > Settings > " +
                    "\"Let AI Bring the Editor to the Front\"). Ask the user to click into Unity.");

            lock (_gate)
            {
                CancelReturnTimer();

                IntPtr editor = MainWindow();
                if (editor == IntPtr.Zero)
                    return new FocusResult(FocusOutcome.NoWindow, error: "Could not resolve the main editor window.");

                FocusResult result;
                IntPtr previous = GetForegroundWindow();
                if (IsOwnWindow(previous))
                {
                    result = new FocusResult(FocusOutcome.AlreadyFocused);
                }
                else
                {
                    if (previous != IntPtr.Zero)
                    {
                        _returnTo = previous;
                        _returnToTitle = TitleOf(previous);
                    }
                    string method = ForceForeground(editor);
                    result = method != null
                        ? new FocusResult(FocusOutcome.Focused, method)
                        : new FocusResult(FocusOutcome.Refused, error:
                            "Windows kept another window in front (foreground lock). Ask the user to click into Unity.");
                    Audit("editor/focus", result, agentId, _returnToTitle);
                    if (result.Outcome == FocusOutcome.Focused)
                        Debug.Log($"[AB-UMCP] Editor brought to the front via {method} (agent {agentId ?? "anonymous"}" +
                                  (string.IsNullOrEmpty(_returnToTitle) ? ")" : $", was \"{_returnToTitle}\")"));
                }

                if (result.EditorInFront && returnAfterMs > 0 && _returnTo != IntPtr.Zero)
                {
                    int delay = Math.Min(returnAfterMs, MaxReturnAfterMs);
                    _returnTimer = new Timer(_ => ReturnFocus(agentId), null, delay, Timeout.Infinite);
                }
                return result;
            }
        }

        /// <summary>
        /// Hand the foreground back to the window that had it before the last focus call. Any
        /// thread. Does nothing when the user has switched windows since (never steals it twice).
        /// </summary>
        public static FocusResult ReturnFocus(string agentId)
        {
            if (!Enabled)
                return new FocusResult(FocusOutcome.Disabled, error:
                    "Changing the foreground window is disabled for this project (Dashboard > Settings).");

            lock (_gate)
            {
                CancelReturnTimer();
                IntPtr target = _returnTo;
                string title = _returnToTitle;
                _returnTo = IntPtr.Zero;
                _returnToTitle = null;

                if (target == IntPtr.Zero || !IsWindow(target))
                    return new FocusResult(FocusOutcome.NothingToReturn);
                if (!IsOwnWindow(GetForegroundWindow()))
                    return new FocusResult(FocusOutcome.UserMovedOn);

                // The editor owns the foreground now, so Windows lets it pass the foreground on.
                FocusResult result = SetForegroundWindow(target) && HandedOff()
                    ? new FocusResult(FocusOutcome.Returned, "direct")
                    : new FocusResult(FocusOutcome.Refused, error: "Windows refused to hand the foreground back.");
                Audit("editor/focus-return", result, agentId, title);
                return result;
            }
        }

        /// <summary>Title of the window focus will return to, or null.</summary>
        public static string ReturnTargetTitle
        {
            get { lock (_gate) return _returnTo != IntPtr.Zero ? _returnToTitle ?? "" : null; }
        }

        private static void CancelReturnTimer()
        {
            _returnTimer?.Dispose();
            _returnTimer = null;
        }

        private static IntPtr MainWindow()
        {
            using (var process = Process.GetCurrentProcess()) return process.MainWindowHandle;
        }

        private static bool IsOwnWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == _pid;
        }

        /// <summary>Returns the method that worked, or null when Windows refused every attempt.</summary>
        private static string ForceForeground(IntPtr hwnd)
        {
            // Restore only a minimized window: SW_RESTORE would also un-maximize a maximized one.
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

            if (SetForegroundWindow(hwnd) && InFront()) return "direct";

            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
            uint self = GetCurrentThreadId();
            if (foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true))
            {
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    AttachThreadInput(self, foregroundThread, false);
                }
                if (InFront()) return "attachThreadInput";
            }

            // Windows lets the process that injected the last input take the foreground. A
            // zero-distance relative mouse move is input with no visible effect.
            var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } };
            if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1)
            {
                SetForegroundWindow(hwnd);
                if (InFront()) return "inputUnlock";
            }
            return null;
        }

        /// <summary>Whether one of the editor's windows is in front; activation can trail the call briefly.</summary>
        private static bool InFront() => WaitForForeground(true);

        /// <summary>Whether another process's window took the foreground.</summary>
        private static bool HandedOff() => WaitForForeground(false);

        private static bool WaitForForeground(bool own)
        {
            for (int i = 0; i < 20; i++)
            {
                if (IsOwnWindow(GetForegroundWindow()) == own) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        private static string TitleOf(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            if (length <= 0) return "";
            var sb = new System.Text.StringBuilder(Math.Min(length, 255) + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private const int  SW_RESTORE       = 9;
        private const uint INPUT_MOUSE      = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        // MOUSEINPUT is the largest member of the native union, so this has the native INPUT size.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
#else
        private const string UnsupportedError =
            "Bringing the editor to the front is only supported in the Windows editor. Ask the user to click into Unity.";

        public static FocusResult Focus(int returnAfterMs, string agentId) =>
            new FocusResult(FocusOutcome.Unsupported, error: UnsupportedError);

        public static FocusResult ReturnFocus(string agentId) =>
            new FocusResult(FocusOutcome.Unsupported, error: UnsupportedError);

        public static string ReturnTargetTitle => null;
#endif
    }
}
