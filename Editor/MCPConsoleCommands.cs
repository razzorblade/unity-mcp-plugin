using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityMCP.Editor
{
    [InitializeOnLoad]
    public static class MCPConsoleCommands
    {
        // Store log messages via Application.logMessageReceivedThreaded
        private static readonly List<LogEntry> _logEntries = new List<LogEntry>();
        private static bool _isListening = false;
        private const int MaxEntries = 1000;

        private struct LogEntry
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public DateTime timestamp;
        }

        // ─── Compilation error buffer (independent of console log) ───
        // Populated via CompilationPipeline.assemblyCompilationFinished.
        // Cleared automatically at the start of each new compilation cycle.
        // Not affected by console Clear().
        private static readonly List<CompilationError> _compilationErrors = new List<CompilationError>();
        private static bool _compilationHooked = false;

        private struct CompilationError
        {
            public string file;
            public int line;
            public int column;
            public string message;
            public string severity; // "error" or "warning"
            public string assembly;
            public DateTime timestamp;
        }

        // Static constructor — runs at editor load thanks to [InitializeOnLoad]
        static MCPConsoleCommands()
        {
            EnsureListening();
            EnsureCompilationHook();
        }

        /// <summary>
        /// Start capturing console messages. Safe to call multiple times.
        /// Called automatically at editor load AND when the bridge server starts.
        /// </summary>
        public static void EnsureListening()
        {
            if (_isListening) return;
            // Use logMessageReceivedThreaded to capture messages from ALL threads,
            // not just the main thread. This catches async compilation errors,
            // background job failures, etc.
            Application.logMessageReceivedThreaded += OnLogMessage;
            _isListening = true;
        }

        /// <summary>
        /// Hook into CompilationPipeline to capture compiler messages (errors/warnings)
        /// independently from the console log buffer. Safe to call multiple times.
        /// </summary>
        public static void EnsureCompilationHook()
        {
            if (_compilationHooked) return;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            _compilationHooked = true;
        }

        private static void OnCompilationStarted(object context)
        {
            // Fresh compilation cycle — clear previous results
            lock (_compilationErrors) { _compilationErrors.Clear(); }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            // Extract assembly name from path (e.g. "Library/ScriptAssemblies/Assembly-CSharp.dll" → "Assembly-CSharp")
            string asmName = System.IO.Path.GetFileNameWithoutExtension(assemblyPath);

            lock (_compilationErrors)
            {
                foreach (var msg in messages)
                {
                    // Only capture errors and warnings, skip info
                    if (msg.type != CompilerMessageType.Error && msg.type != CompilerMessageType.Warning)
                        continue;

                    _compilationErrors.Add(new CompilationError
                    {
                        file = msg.file ?? "",
                        line = msg.line,
                        column = msg.column,
                        message = msg.message ?? "",
                        severity = msg.type == CompilerMessageType.Error ? "error" : "warning",
                        assembly = asmName,
                        timestamp = DateTime.Now,
                    });
                }
            }
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            lock (_logEntries)
            {
                _logEntries.Add(new LogEntry
                {
                    message = message,
                    stackTrace = stackTrace,
                    type = type,
                    timestamp = DateTime.Now,
                });

                // Keep max entries capped
                if (_logEntries.Count > MaxEntries)
                    _logEntries.RemoveRange(0, _logEntries.Count - MaxEntries);
            }
        }

        /// <summary>
        /// Most recent console entries (chronological). By default identical entries — same type,
        /// message and stack trace — are collapsed like the Console window's Collapse toggle:
        /// one entry with <c>repeats</c> (when &gt; 1), its latest <c>timestamp</c> and its
        /// <c>firstTimestamp</c>. <c>count</c> then bounds the number of DISTINCT entries, so one
        /// error spamming every frame can no longer crowd every other message out of the window.
        /// collapse:false (or verbose:true) returns one entry per log call, as before.
        /// </summary>
        public static object GetLog(Dictionary<string, object> args)
        {
            EnsureListening();

            int count = args.ContainsKey("count") ? Convert.ToInt32(args["count"]) : 50;
            string typeFilter = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "all";
            bool verbose = MCPWire.IsVerbose(args);
            bool collapse = !verbose && MCPArgs.GetBool(args, "collapse", true);

            var entries = new List<Dictionary<string, object>>();
            var byKey = collapse ? new Dictionary<(LogType, string, string), Dictionary<string, object>>() : null;
            int scanned = 0;
            lock (_logEntries)
            {
                // Walk backwards so the result is the most recent N matches of the filter, not a
                // filter over the last N entries (which missed most errors).
                for (int i = _logEntries.Count - 1; i >= 0; i--)
                {
                    var entry = _logEntries[i];
                    if (!MatchesFilter(entry.type, typeFilter)) continue;

                    if (collapse && byKey.TryGetValue((entry.type, entry.message, entry.stackTrace), out var seen))
                    {
                        scanned++;
                        seen["repeats"] = (int)seen["repeats"] + 1;
                        seen["firstTimestamp"] = entry.timestamp.ToString("HH:mm:ss.fff");
                        continue;
                    }
                    // Full window: stop, unless collapsing (older copies of the entries
                    // already taken still add to their repeat counts; the buffer is capped).
                    if (entries.Count >= count) { if (collapse) continue; break; }
                    scanned++;

                    var dict = new Dictionary<string, object>
                    {
                        { "message", entry.message },
                        { "type", entry.type.ToString().ToLower() },
                        { "timestamp", entry.timestamp.ToString("HH:mm:ss.fff") },
                    };
                    if (verbose || !string.IsNullOrEmpty(entry.stackTrace))
                        dict["stackTrace"] = entry.stackTrace ?? "";
                    if (collapse)
                    {
                        dict["repeats"] = 1;
                        byKey[(entry.type, entry.message, entry.stackTrace)] = dict;
                    }
                    entries.Add(dict);
                }
            }

            // Reverse so entries are in chronological order (oldest first)
            entries.Reverse();

            if (collapse)
            {
                foreach (var dict in entries)
                {
                    if ((int)dict["repeats"] == 1)
                    {
                        dict.Remove("repeats");
                        dict.Remove("firstTimestamp");
                    }
                }
            }

            var result = new Dictionary<string, object>
            {
                { "count", entries.Count },
                { "entries", entries },
            };
            if (collapse && scanned > entries.Count) result["collapsedFrom"] = scanned;
            return result;
        }

        private static bool MatchesFilter(LogType type, string typeFilter)
        {
            switch (typeFilter)
            {
                case "error": return type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
                case "warning": return type == LogType.Warning;
                case "info": return type == LogType.Log;
                default: return true;
            }
        }

        /// <summary>
        /// Get compilation errors/warnings captured via CompilationPipeline.
        /// Independent of the console log buffer — not affected by Clear().
        /// </summary>
        public static object GetCompilationErrors(Dictionary<string, object> args)
        {
            EnsureCompilationHook();

            int count = args.ContainsKey("count") ? Convert.ToInt32(args["count"]) : 50;
            string severityFilter = args.ContainsKey("severity") ? args["severity"].ToString().ToLower() : "all";

            var entries = new List<Dictionary<string, object>>();
            lock (_compilationErrors)
            {
                // Walk backwards to get most recent first, then reverse for chronological order
                for (int i = _compilationErrors.Count - 1; i >= 0 && entries.Count < count; i--)
                {
                    var err = _compilationErrors[i];

                    if (severityFilter != "all" && err.severity != severityFilter)
                        continue;

                    entries.Add(new Dictionary<string, object>
                    {
                        { "file", err.file },
                        { "line", err.line },
                        { "column", err.column },
                        { "message", err.message },
                        { "severity", err.severity },
                        { "assembly", err.assembly },
                        { "timestamp", err.timestamp.ToString("HH:mm:ss.fff") },
                    });
                }
            }

            entries.Reverse();

            return new Dictionary<string, object>
            {
                { "count", entries.Count },
                { "isCompiling", EditorApplication.isCompiling },
                { "entries", entries },
            };
        }

        public static object Clear()
        {
            EnsureListening();
            lock (_logEntries) { _logEntries.Clear(); }
            return new { success = true, message = "Console log buffer cleared" };
        }
    }
}
