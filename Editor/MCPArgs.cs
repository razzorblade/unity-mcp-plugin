using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Typed-first argument readers for command handlers.
    ///
    /// MiniJson delivers JSON numbers as boxed <c>double</c> / <c>long</c>. The old
    /// pattern <c>value.ToString()</c> + invariant TryParse silently DROPPED every
    /// non-integer number on machines whose current culture writes a decimal comma
    /// (18.8 → "18,8" → parse fail → default) — the exact "floats silently ignored"
    /// bug from the battle-test report. Readers here unbox the typed value directly
    /// and only fall back to invariant string parsing for string inputs.
    ///
    /// Design rule (also from the report): a parameter that is PRESENT but not
    /// interpretable throws instead of silently falling back to the default —
    /// a loud failure costs seconds, a silent wrong default costs a debugging session.
    /// </summary>
    internal static class MCPArgs
    {
        public static float GetFloat(Dictionary<string, object> args, string key, float defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;

            switch (value)
            {
                case double d: return (float)d;
                case float f: return f;
                case long l: return l;
                case int i: return i;
                case string s:
                    if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                        return parsed;
                    throw new ArgumentException($"Parameter '{key}' is not a valid number: '{s}'.");
                default:
                    throw new ArgumentException($"Parameter '{key}' must be a number (got {value.GetType().Name}).");
            }
        }

        public static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;

            switch (value)
            {
                case long l: return checked((int)l);
                case int i: return i;
                case double d:
                    // Accept integral doubles (8.0) — reject genuinely fractional values loudly.
                    double rounded = Math.Round(d);
                    if (Math.Abs(d - rounded) < 1e-6) return checked((int)rounded);
                    throw new ArgumentException($"Parameter '{key}' must be an integer (got {d.ToString(CultureInfo.InvariantCulture)}).");
                case string s:
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                        return parsed;
                    throw new ArgumentException($"Parameter '{key}' is not a valid integer: '{s}'.");
                default:
                    throw new ArgumentException($"Parameter '{key}' must be an integer (got {value.GetType().Name}).");
            }
        }

        public static long GetLong(Dictionary<string, object> args, string key, long defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;

            switch (value)
            {
                case long l: return l;
                case int i: return i;
                case double d:
                    double rounded = Math.Round(d);
                    if (Math.Abs(d - rounded) < 1e-6) return checked((long)rounded);
                    throw new ArgumentException($"Parameter '{key}' must be an integer (got {d.ToString(CultureInfo.InvariantCulture)}).");
                case string s:
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                        return parsed;
                    throw new ArgumentException($"Parameter '{key}' is not a valid integer: '{s}'.");
                default:
                    throw new ArgumentException($"Parameter '{key}' must be an integer (got {value.GetType().Name}).");
            }
        }

        public static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;

            switch (value)
            {
                case bool b: return b;
                case string s:
                    string lower = s.ToLowerInvariant();
                    if (lower == "true" || lower == "1") return true;
                    if (lower == "false" || lower == "0") return false;
                    throw new ArgumentException($"Parameter '{key}' is not a valid boolean: '{s}'.");
                case long l: return l != 0;
                default:
                    throw new ArgumentException($"Parameter '{key}' must be a boolean (got {value.GetType().Name}).");
            }
        }

        // ─── Vector-like values ───
        // Accepted in BOTH wire forms: the object form ({x,y,z} / {r,g,b,a}) that tool schemas
        // document, and the array form ([x,y,z]) that dense responses emit. Agents echo back
        // what they read, so a reader that only knew objects silently turned an echoed array
        // into Vector3.zero (the `as Dictionary` cast yields null). Present-but-unreadable
        // values throw, per the design rule above.

        private static readonly string[] XYZW = { "x", "y", "z", "w" };
        private static readonly string[] RGBA = { "r", "g", "b", "a" };

        public static Vector2 ToVector2(object value, string name)
        {
            var c = ReadComponents(value, XYZW, 2, 0f, 0f, name);
            return new Vector2(c[0], c[1]);
        }

        public static Vector3 ToVector3(object value, string name)
        {
            var c = ReadComponents(value, XYZW, 3, 0f, 0f, name);
            return new Vector3(c[0], c[1], c[2]);
        }

        public static Vector4 ToVector4(object value, string name)
        {
            var c = ReadComponents(value, XYZW, 4, 0f, 0f, name);
            return new Vector4(c[0], c[1], c[2], c[3]);
        }

        public static Quaternion ToQuaternion(object value, string name)
        {
            var c = ReadComponents(value, XYZW, 4, 0f, 1f, name);
            return new Quaternion(c[0], c[1], c[2], c[3]);
        }

        /// <param name="rgbDefault">Value for a missing r/g/b channel (alpha always defaults to 1).</param>
        public static Color ToColor(object value, string name, float rgbDefault = 0f)
        {
            var c = ReadComponents(value, RGBA, 4, rgbDefault, 1f, name);
            return new Color(c[0], c[1], c[2], c[3]);
        }

        /// <summary>
        /// Read <paramref name="count"/> float components from an object keyed by <paramref name="keys"/>
        /// or from a numeric array. Missing trailing components take their defaults (the last one
        /// takes <paramref name="lastDefault"/>, so a 3-element color gets alpha 1).
        /// </summary>
        private static float[] ReadComponents(object value, string[] keys, int count, float fill, float lastDefault, string name)
        {
            var result = new float[count];
            for (int i = 0; i < count; i++) result[i] = i == count - 1 ? lastDefault : fill;

            switch (value)
            {
                case Dictionary<string, object> dict:
                    for (int i = 0; i < count; i++)
                        if (dict.TryGetValue(keys[i], out object component) && component != null)
                            result[i] = ToSingle(component, name);
                    return result;
                case System.Collections.IList list:
                    if (list.Count < 2 || list.Count > count)
                        throw new ArgumentException($"'{name}' expects {count} numbers, got an array of {list.Count}.");
                    for (int i = 0; i < list.Count; i++)
                        result[i] = ToSingle(list[i], name);
                    return result;
                case null:
                    throw new ArgumentException($"'{name}' is null; expected [{string.Join(",", keys, 0, count)}] or {{{string.Join(",", keys, 0, count)}}}.");
                default:
                    throw new ArgumentException($"'{name}' expects [{string.Join(",", keys, 0, count)}] or {{{string.Join(",", keys, 0, count)}}}, got {value.GetType().Name}.");
            }
        }

        private static float ToSingle(object value, string name)
        {
            switch (value)
            {
                case double d: return (float)d;
                case float f: return f;
                case long l: return l;
                case int i: return i;
                case string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed):
                    return parsed;
                default:
                    throw new ArgumentException($"'{name}' has a non-numeric component: {value ?? "null"}.");
            }
        }
    }
}
