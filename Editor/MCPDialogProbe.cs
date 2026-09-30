using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Names — and answers — the native modal dialog that is blocking the editor's main thread,
    /// from the HTTP pool threads, which are the only managed threads still running while Unity
    /// sits in a modal loop.
    ///
    /// Why: a dialog raised while an MCP command runs (the "Scene(s) Have Been Modified" prompt
    /// behind File > New Scene, a DisplayDialog in user code, a package's confirmation…) parks the
    /// main thread on a human click. The health snapshot could only guess "modal dialog or long
    /// operation"; this reports the dialog's title, message and buttons so the agent can decide,
    /// and clicks the chosen button for it.
    ///
    /// How (Windows): Unity's dialogs are standard Win32 dialogs (class #32770, DialogBoxParamW).
    /// Buttons are Button controls with fixed ids (1 primary, 2 cancel/Esc, 3 alternate) and the
    /// message sits in a read-only Edit control. Top-level windows of the editor's UI thread are
    /// walked with FindWindowEx — no EnumWindows reverse-P/Invoke callback on a pool thread — and
    /// text is read without sending window messages wherever Win32 allows it (the Edit control is
    /// the exception, read with a bounded SendMessageTimeout). The probe only runs while the main
    /// thread is stalled and its result is cached per stall, so a healthy editor pays nothing.
    /// Other platforms: unsupported — callers keep the stall-only report.
    /// </summary>
    public static class MCPDialogProbe
    {
        public const string KindDialog    = "dialog";    // clickable native dialog
        public const string KindFilePanel = "filePanel"; // native open/save panel: cancel only
        public const string KindProgress  = "progress";  // progress window: never clicked, never a blocker
        public const string KindWindow    = "window";    // modal window drawn by Unity (IMGUI/UI Toolkit): title only

        public const string RolePrimary   = "primary";   // control id 1 (IDOK)
        public const string RoleCancel    = "cancel";    // control id 2 (IDCANCEL) — what Esc / the close box do
        public const string RoleAlternate = "alternate"; // any other id (3 on Unity's three-button dialogs)

        /// <summary>Stall below which the main thread is still pumping its own loop: nothing to probe.</summary>
        public const long ProbeThresholdMs = 1000;

        private const long CacheLifetimeTicks = 500 * TimeSpan.TicksPerMillisecond;
        private const int  ClickDismissTimeoutMs = 2000;
        private const int  ClickDismissPollMs    = 25;
        private const int  MaxMessageChars       = 2000;

        // ═══════════════════════════════════════════════════════
        //  Snapshot types
        // ═══════════════════════════════════════════════════════

        public sealed class DialogButton
        {
            public string Label     { get; }
            public int    ControlId { get; }
            public string Role      { get; }
            public bool   IsDefault { get; }
            internal IntPtr Hwnd    { get; }
            internal int    Left    { get; }

            internal DialogButton(string label, int controlId, bool isDefault, IntPtr hwnd, int left)
            {
                Label     = label;
                ControlId = controlId;
                Role      = controlId == 1 ? RolePrimary : controlId == 2 ? RoleCancel : RoleAlternate;
                IsDefault = isDefault;
                Hwnd      = hwnd;
                Left      = left;
            }
        }

        /// <summary>Immutable description of one blocking window.</summary>
        public sealed class DialogInfo
        {
            /// <summary>Stable for the dialog's lifetime: window handle + the stall it appeared in.</summary>
            public string Id      { get; }
            public string Kind    { get; }
            public string Title   { get; }
            public string Message { get; }
            /// <summary>Label of an opt-out checkbox ("Do not show again"), reported but never toggled.</summary>
            public string OptOut  { get; }
            /// <summary>Left-to-right, as the user sees them.</summary>
            public IReadOnlyList<DialogButton> Buttons { get; }
            internal IntPtr Hwnd  { get; }

            internal DialogInfo(string id, string kind, string title, string message, string optOut,
                                IReadOnlyList<DialogButton> buttons, IntPtr hwnd)
            {
                Id = id; Kind = kind; Title = title; Message = message; OptOut = optOut;
                Buttons = buttons; Hwnd = hwnd;
            }

            /// <summary>Whether the window has buttons MCP may press at all (ignores the user setting).</summary>
            public bool HasClickableButtons =>
                (Kind == KindDialog || Kind == KindFilePanel) && Buttons.Count > 0;

            /// <summary>Wire shape, attached to ping and every queue/status snapshot.</summary>
            /// <param name="blockedMs">How long the main thread has been blocked (the current stall).</param>
            public Dictionary<string, object> ToWire(long blockedMs)
            {
                var labels = new List<object>(Buttons.Count);
                string defaultButton = null, cancelButton = null;
                foreach (var b in Buttons)
                {
                    labels.Add(b.Label);
                    if (b.IsDefault) defaultButton = b.Label;
                    if (b.Role == RoleCancel) cancelButton = b.Label;
                }

                var wire = new Dictionary<string, object>
                {
                    { "id",        Id },
                    { "kind",      Kind },
                    { "title",     Title },
                    { "message",   Message },
                    { "buttons",   labels },
                    { "clickable", HasClickableButtons && ClickEnabled },
                    { "blockedMs", blockedMs },
                };
                if (defaultButton != null) wire["defaultButton"] = defaultButton;
                if (cancelButton != null)  wire["cancelButton"]  = cancelButton;
                if (OptOut != null)        wire["optOut"]        = OptOut;
                if (HasClickableButtons && !ClickEnabled) wire["clickDisabled"] = true;
                return wire;
            }
        }

        public enum ClickOutcome { Clicked, Unsupported, Disabled, NoDialog, Mismatch, NotClickable, UnknownButton, Failed }

        public sealed class ClickResult
        {
            public ClickOutcome Outcome { get; }
            public string       Error   { get; }
            /// <summary>The dialog as probed at click time (null when none was found).</summary>
            public DialogInfo   Dialog  { get; }
            public DialogButton Button  { get; }
            /// <summary>The dialog window closed within the wait after the click.</summary>
            public bool         Dismissed { get; }

            internal ClickResult(ClickOutcome outcome, string error, DialogInfo dialog = null,
                                 DialogButton button = null, bool dismissed = false)
            {
                Outcome = outcome; Error = error; Dialog = dialog; Button = button; Dismissed = dismissed;
            }
        }

        // ═══════════════════════════════════════════════════════
        //  State
        // ═══════════════════════════════════════════════════════

        private sealed class ProbeResult
        {
            public readonly long StallStartTicks;
            public readonly long ProbedAtTicks;
            public readonly DialogInfo Dialog;

            public ProbeResult(long stallStartTicks, long probedAtTicks, DialogInfo dialog)
            {
                StallStartTicks = stallStartTicks; ProbedAtTicks = probedAtTicks; Dialog = dialog;
            }
        }

        private static ProbeResult _cache;
        private static int _probing;   // single-flight guard: concurrent pollers share one probe
        private static int _uiThreadId;
        private static int _clickEnabled = 1;

        // Clicks happen on pool threads; MCPActionHistory reads EditorPrefs and must be fed from
        // the main thread, which resumes once the dialog is gone.
        private static readonly ConcurrentQueue<MCPActionRecord> _pendingAudit = new ConcurrentQueue<MCPActionRecord>();

        /// <summary>
        /// Mirror of <see cref="MCPSettingsManager.AllowDialogClick"/> readable from any thread
        /// (EditorPrefs is main-thread only). Written on the main thread at bridge start and by the setter.
        /// </summary>
        public static bool ClickEnabled
        {
            get => Volatile.Read(ref _clickEnabled) != 0;
            internal set => Volatile.Write(ref _clickEnabled, value ? 1 : 0);
        }

#if UNITY_EDITOR_WIN
        public static bool Supported => true;
#else
        public static bool Supported => false;
#endif

        /// <summary>Remember the editor's UI thread. Main thread only; idempotent.</summary>
        public static void CaptureMainThread()
        {
#if UNITY_EDITOR_WIN
            Volatile.Write(ref _uiThreadId, (int)GetCurrentThreadId());
#endif
        }

        /// <summary>Drop the cached probe (after a click, so the next poll sees the new state).</summary>
        public static void Invalidate() => Volatile.Write(ref _cache, null);

        // ═══════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// The window blocking the main thread, or null. Any thread. Cheap when the editor is
        /// healthy (returns before touching Win32) and cached per stall otherwise.
        /// </summary>
        /// <param name="stallMs">Current main-thread stall.</param>
        /// <param name="stallStartTicks">UTC ticks of the last main-thread tick — identifies the stall.</param>
        public static DialogInfo Current(long stallMs, long stallStartTicks)
        {
            if (!Supported || stallMs < ProbeThresholdMs || Volatile.Read(ref _uiThreadId) == 0) return null;

            var cached = Volatile.Read(ref _cache);
            bool sameStall = cached != null && cached.StallStartTicks == stallStartTicks;
            if (sameStall && DateTime.UtcNow.Ticks - cached.ProbedAtTicks < CacheLifetimeTicks)
                return cached.Dialog;

            // Another thread is probing right now: serve what we have rather than pile on.
            if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0)
                return sameStall ? cached.Dialog : null;
            try
            {
                var dialog = Probe(stallStartTicks);
                Volatile.Write(ref _cache, new ProbeResult(stallStartTicks, DateTime.UtcNow.Ticks, dialog));
                return dialog;
            }
            catch (Exception)
            {
                // Diagnostics must never break ping/queue-status.
                return sameStall ? cached.Dialog : null;
            }
            finally
            {
                Volatile.Write(ref _probing, 0);
            }
        }

        /// <summary>
        /// Press <paramref name="button"/> on the dialog identified by <paramref name="dialogId"/>.
        /// Any thread (runs on the HTTP pool thread). Refuses when the live dialog is not the one the
        /// caller saw, so a chained dialog is never answered unread.
        /// </summary>
        public static ClickResult Click(string dialogId, string button, string agentId, long stallMs, long stallStartTicks)
        {
#if UNITY_EDITOR_WIN
            if (!ClickEnabled)
                return new ClickResult(ClickOutcome.Disabled,
                    "Clicking dialogs through MCP is disabled in this project (Window > AB Unity MCP > Dashboard > Settings). Ask the user to answer the dialog.");

            Invalidate();
            DialogInfo dialog = stallMs >= ProbeThresholdMs && Volatile.Read(ref _uiThreadId) != 0
                ? Probe(stallStartTicks)
                : null;
            if (dialog == null)
                return new ClickResult(ClickOutcome.NoDialog, "No dialog is blocking the editor (it may already have been answered).");
            if (!string.Equals(dialog.Id, dialogId, StringComparison.Ordinal))
                return new ClickResult(ClickOutcome.Mismatch,
                    $"The blocking dialog is now \"{dialog.Title}\" (id {dialog.Id}), not {dialogId}. Read it before answering.", dialog);
            if (!dialog.HasClickableButtons)
                return new ClickResult(ClickOutcome.NotClickable, dialog.Kind == KindProgress
                    ? $"\"{dialog.Title}\" is a progress window, not a question. Wait for the operation to finish."
                    : $"\"{dialog.Title}\" is drawn by Unity itself and cannot be clicked through MCP. Ask the user to close it.", dialog);

            var target = ResolveButton(dialog, button);
            if (target == null)
                return new ClickResult(ClickOutcome.UnknownButton,
                    $"\"{dialog.Title}\" has no button \"{button}\". Pick one of: {string.Join(", ", ButtonLabels(dialog))}.", dialog);
            if (!IsWindowVisible(target.Hwnd) || !IsWindowEnabled(target.Hwnd))
                return new ClickResult(ClickOutcome.NotClickable, $"Button \"{target.Label}\" is currently disabled.", dialog, target);

            // WM_COMMAND/BN_CLICKED is what the dialog procedure itself receives on a click, and
            // unlike BM_CLICK it does not depend on the dialog being the active window.
            IntPtr wParam = (IntPtr)(((BN_CLICKED & 0xFFFF) << 16) | (target.ControlId & 0xFFFF));
            if (!PostMessage(dialog.Hwnd, WM_COMMAND, wParam, target.Hwnd))
                return new ClickResult(ClickOutcome.Failed,
                    $"Posting the click to \"{dialog.Title}\" failed (Win32 error {Marshal.GetLastWin32Error()}).", dialog, target);

            long startedAt = DateTime.UtcNow.Ticks;
            bool dismissed = false;
            for (int waited = 0; waited < ClickDismissTimeoutMs; waited += ClickDismissPollMs)
            {
                if (!IsWindow(dialog.Hwnd) || !IsWindowVisible(dialog.Hwnd)) { dismissed = true; break; }
                Thread.Sleep(ClickDismissPollMs);
            }
            Invalidate();

            long elapsedMs = (DateTime.UtcNow.Ticks - startedAt) / TimeSpan.TicksPerMillisecond;
            Audit(dialog, target, agentId, dismissed, elapsedMs);
            return new ClickResult(ClickOutcome.Clicked, null, dialog, target, dismissed);
#else
            return new ClickResult(ClickOutcome.Unsupported,
                $"Reading and clicking dialogs is only supported in the Windows editor (this is {Application.platform}). Ask the user to answer the dialog.");
#endif
        }

        /// <summary>Feed queued click records into the action history. Main thread (editor update).</summary>
        public static void FlushAudit()
        {
            if (_pendingAudit.IsEmpty) return;
            while (_pendingAudit.TryDequeue(out var record))
            {
                try { MCPActionHistory.RecordAction(record); }
                catch (Exception ex) { Debug.LogWarning($"[AB-UMCP] Failed to record dialog click: {ex.Message}"); }
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════

        private static IEnumerable<string> ButtonLabels(DialogInfo dialog)
        {
            foreach (var b in dialog.Buttons) yield return $"\"{b.Label}\"";
        }

        /// <summary>Exact label, then case-insensitive label, then role ("primary", "cancel", "alternate", "default").</summary>
        internal static DialogButton ResolveButton(DialogInfo dialog, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return null;
            string want = StripMnemonic(requested).Trim();

            foreach (var b in dialog.Buttons)
                if (string.Equals(b.Label, want, StringComparison.Ordinal)) return b;
            foreach (var b in dialog.Buttons)
                if (string.Equals(b.Label, want, StringComparison.OrdinalIgnoreCase)) return b;
            foreach (var b in dialog.Buttons)
                if (string.Equals(b.Role, want, StringComparison.OrdinalIgnoreCase)) return b;
            if (string.Equals(want, "default", StringComparison.OrdinalIgnoreCase))
                foreach (var b in dialog.Buttons)
                    if (b.IsDefault) return b;
            return null;
        }

        /// <summary>Win32 mnemonic markers: "&amp;Save" → "Save", "A &amp;&amp; B" → "A &amp; B".</summary>
        internal static string StripMnemonic(string label)
        {
            if (string.IsNullOrEmpty(label) || label.IndexOf('&') < 0) return label ?? "";
            var sb = new StringBuilder(label.Length);
            for (int i = 0; i < label.Length; i++)
            {
                if (label[i] == '&')
                {
                    if (i + 1 < label.Length && label[i + 1] == '&') { sb.Append('&'); i++; }
                    continue;
                }
                sb.Append(label[i]);
            }
            return sb.ToString();
        }

        private static void Audit(DialogInfo dialog, DialogButton button, string agentId, bool dismissed, long elapsedMs)
        {
            Debug.Log($"[AB-UMCP] Dialog \"{dialog.Title}\": clicked \"{button.Label}\" (agent {agentId ?? "anonymous"})" +
                      (dismissed ? "" : " — the dialog did not close within 2s"));
            _pendingAudit.Enqueue(new MCPActionRecord
            {
                Timestamp       = DateTime.UtcNow,
                AgentId         = agentId ?? "anonymous",
                ActionName      = "dialog/click",
                Category        = "dialog",
                Status          = dismissed ? "Completed" : "Failed",
                ExecutionTimeMs = elapsedMs,
                ErrorMessage    = dismissed ? null : "Dialog did not close after the click.",
                TargetPath      = dialog.Title,
                TargetType      = "Dialog",
                Parameters      = new Dictionary<string, string>
                {
                    { "dialog",  dialog.Title ?? "" },
                    { "message", dialog.Message ?? "" },
                    { "button",  button.Label },
                },
            });
        }

#if UNITY_EDITOR_WIN
        // ═══════════════════════════════════════════════════════
        //  Windows probe
        // ═══════════════════════════════════════════════════════

        private const int MaxTopLevelWindows = 10000;
        private const int MaxChildWindows    = 256;

        // Edit-control text needs WM_GETTEXT. It is read once per dialog, and a window that keeps
        // timing out is not asked again (each timeout leaks its buffer — see ReadEditText).
        private static readonly object _editTextLock = new object();
        private static IntPtr _editTextHwnd;
        private static string _editText;
        private static int    _editTextFailures;
        private const  int    MaxEditTextFailures = 3;

        private static DialogInfo Probe(long stallStartTicks)
        {
            uint uiThread = (uint)Volatile.Read(ref _uiThreadId);
            IntPtr active = ActiveWindowOf(uiThread);

            IntPtr best = IntPtr.Zero;
            int bestRank = -1;
            IntPtr h = IntPtr.Zero;
            for (int i = 0; i < MaxTopLevelWindows; i++)
            {
                h = FindWindowEx(IntPtr.Zero, h, null, null);
                if (h == IntPtr.Zero) break;
                if (GetWindowThreadProcessId(h, out _) != uiThread) continue;
                if (!IsWindowVisible(h) || !IsWindowEnabled(h)) continue;

                bool isDialogClass = ClassOf(h) == "#32770";
                bool isActive = h == active;
                if (!isDialogClass)
                {
                    // Unity-drawn windows (floating panels, ShowModal windows) share one class. A
                    // modal one is the active window and its owner is disabled; a floating panel
                    // behind a native dialog is neither active nor worth reporting.
                    IntPtr owner = GetWindow(h, GW_OWNER);
                    if (!isActive || owner == IntPtr.Zero || IsWindowEnabled(owner)) continue;
                }

                int rank = (isDialogClass ? 2 : 0) + (isActive ? 1 : 0);
                if (rank > bestRank) { bestRank = rank; best = h; }
            }

            return best == IntPtr.Zero ? null : Describe(best, stallStartTicks);
        }

        private static DialogInfo Describe(IntPtr hwnd, long stallStartTicks)
        {
            string title = InternalText(hwnd);
            var buttons = new List<DialogButton>();
            var message = new StringBuilder();
            string optOut = null;
            bool progress = false, filePanel = false;

            IntPtr c = IntPtr.Zero;
            for (int i = 0; i < MaxChildWindows; i++)
            {
                c = FindWindowEx(hwnd, c, null, null);
                if (c == IntPtr.Zero) break;
                bool visible = IsWindowVisible(c);
                string cls = ClassOf(c);
                switch (cls)
                {
                    case "Button":
                    {
                        if (!visible) break;
                        int type = GetWindowLong(c, GWL_STYLE) & BS_TYPEMASK;
                        string label = StripMnemonic(InternalText(c)).Trim();
                        if (type == BS_PUSHBUTTON || type == BS_DEFPUSHBUTTON)
                        {
                            if (label.Length > 0 && IsWindowEnabled(c))
                                buttons.Add(new DialogButton(label, GetDlgCtrlID(c), type == BS_DEFPUSHBUTTON, c, LeftOf(c)));
                        }
                        else if ((type == BS_CHECKBOX || type == BS_AUTOCHECKBOX) && label.Length > 0)
                        {
                            optOut = label;
                        }
                        break;
                    }
                    case "Static":
                    {
                        if (!visible) break;
                        int type = GetWindowLong(c, GWL_STYLE) & SS_TYPEMASK;
                        if (type == SS_LEFT || type == SS_CENTER || type == SS_RIGHT || type == SS_SIMPLE || type == SS_LEFTNOWORDWRAP)
                            AppendLine(message, InternalText(c));
                        break;
                    }
                    case "Edit":
                        if (visible) AppendLine(message, ReadEditText(c));
                        break;
                    case "msctls_progress32":
                        progress |= visible;
                        break;
                    // Common Item Dialog (IFileDialog) building blocks — never present in a message dialog.
                    case "DUIViewWndClassName":
                    case "DirectUIHWND":
                    case "SHELLDLL_DefView":
                    case "ComboBoxEx32":
                        filePanel = true;
                        break;
                }
            }

            string kind = progress ? KindProgress
                        : filePanel ? KindFilePanel
                        : buttons.Count > 0 ? KindDialog
                        : KindWindow;

            // A file panel needs a path the agent cannot type; it may only be dismissed.
            if (kind == KindFilePanel) buttons.RemoveAll(b => b.Role != RoleCancel);
            buttons.Sort((a, b) => a.Left.CompareTo(b.Left));

            string text = message.ToString().Trim();
            if (text.Length > MaxMessageChars) text = text.Substring(0, MaxMessageChars) + "…";

            string id = $"{hwnd.ToInt64():x}-{stallStartTicks:x}";
            return new DialogInfo(id, kind, title, text, optOut, buttons.AsReadOnly(), hwnd);
        }

        private static void AppendLine(StringBuilder sb, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text.Replace("\r\n", "\n").Trim());
        }

        private static IntPtr ActiveWindowOf(uint threadId)
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
            return GetGUIThreadInfo(threadId, ref info) ? info.hwndActive : IntPtr.Zero;
        }

        private static int LeftOf(IntPtr hwnd) => GetWindowRect(hwnd, out var r) ? r.left : 0;

        private static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(64);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        /// <summary>Window text straight from the window structure: never sends a message, so it
        /// cannot hang on a UI thread that is not pumping. Valid for titles, Button and Static.</summary>
        private static string InternalText(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            return InternalGetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        /// <summary>
        /// Edit controls keep their text in their own buffer, so WM_GETTEXT is the only way to read
        /// it. Bounded by SendMessageTimeout; on timeout the unmanaged buffer is deliberately NOT
        /// freed — the message may still be processed later and would write into freed memory.
        /// </summary>
        private static string ReadEditText(IntPtr hwnd)
        {
            lock (_editTextLock)
            {
                if (_editTextHwnd == hwnd)
                {
                    if (_editText != null) return _editText;
                    if (_editTextFailures >= MaxEditTextFailures) return "";
                }
                else
                {
                    _editTextHwnd = hwnd; _editText = null; _editTextFailures = 0;
                }
            }

            const int capacityChars = MaxMessageChars + 1;
            IntPtr buffer = Marshal.AllocHGlobal(capacityChars * sizeof(char));
            Marshal.WriteInt16(buffer, 0);
            IntPtr sent = SendMessageTimeout(hwnd, WM_GETTEXT, (IntPtr)capacityChars, buffer,
                SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, 250, out IntPtr copied);
            if (sent == IntPtr.Zero)
            {
                lock (_editTextLock) if (_editTextHwnd == hwnd) _editTextFailures++;
                return "";
            }

            string text = Marshal.PtrToStringUni(buffer, (int)Math.Min(copied.ToInt64(), capacityChars - 1));
            Marshal.FreeHGlobal(buffer);
            lock (_editTextLock) if (_editTextHwnd == hwnd) _editText = text;
            return text;
        }

        // ── Win32 ──
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_COMMAND = 0x0111;
        private const int  BN_CLICKED = 0;
        private const uint GW_OWNER   = 4;
        private const int  GWL_STYLE  = -16;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint SMTO_ERRORONEXIT = 0x0020;

        private const int BS_TYPEMASK      = 0xF;
        private const int BS_PUSHBUTTON    = 0x0;
        private const int BS_DEFPUSHBUTTON = 0x1;
        private const int BS_CHECKBOX      = 0x2;
        private const int BS_AUTOCHECKBOX  = 0x3;

        private const int SS_TYPEMASK       = 0x1F;
        private const int SS_LEFT           = 0x0;
        private const int SS_CENTER         = 0x1;
        private const int SS_RIGHT          = 0x2;
        private const int SS_SIMPLE         = 0xB;
        private const int SS_LEFTNOWORDWRAP = 0xC;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowTitle);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
#else
        private static DialogInfo Probe(long stallStartTicks) => null;
#endif
    }
}
