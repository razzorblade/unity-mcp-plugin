using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Reads SerializedProperty trees into JSON-ready values for component, prefab-asset and
    /// ScriptableObject inspection — one reader, so the three endpoints can't drift apart.
    ///
    /// Arrays and nested serializable structs are read recursively (they used to come back as
    /// the literal string "Generic", losing the data). Recursion is bounded: past
    /// <see cref="MaxDepth"/> a subtree is replaced by <c>{"$more": "&lt;propertyPath&gt;"}</c>, and
    /// arrays longer than <see cref="MaxArrayElements"/> become <c>{"length": n, "items": [first N]}</c>.
    /// Either can be read in full through the endpoints' <c>propertyPath</c> argument.
    ///
    /// Dense (default) property entries are <c>{name, type, value}</c>: <c>displayName</c> only when
    /// it differs from Unity's nicified name, <c>editable</c> only when false, vectors as arrays.
    /// </summary>
    internal sealed class MCPPropertyReader
    {
        public bool Verbose { get; }
        public int MaxDepth { get; }
        public int MaxArrayElements { get; }

        private MCPPropertyReader(bool verbose, int maxDepth, int maxArrayElements)
        {
            Verbose = verbose;
            MaxDepth = maxDepth;
            MaxArrayElements = maxArrayElements;
        }

        public static MCPPropertyReader FromArgs(Dictionary<string, object> args) => new MCPPropertyReader(
            MCPWire.IsVerbose(args),
            Mathf.Clamp(MCPArgs.GetInt(args, "maxDepth", 4), 0, 64),
            Mathf.Clamp(MCPArgs.GetInt(args, "maxArrayElements", 100), 0, 100000));

        /// <summary>
        /// Top-level visible properties of <paramref name="so"/>, or — when <paramref name="propertyPath"/>
        /// is given — just that property (any depth, e.g. "m_Items.Array.data[3].m_Name").
        /// Returns an error object when the path does not resolve.
        /// </summary>
        public Dictionary<string, object> Read(SerializedObject so, string propertyPath, bool skipScript)
        {
            if (!string.IsNullOrEmpty(propertyPath))
            {
                var prop = so.FindProperty(propertyPath);
                if (prop == null)
                    return new Dictionary<string, object> { { "error", $"Property path '{propertyPath}' not found" } };
                var entry = Describe(prop);
                entry["propertyPath"] = prop.propertyPath;
                return new Dictionary<string, object> { { "property", entry } };
            }

            var properties = new List<Dictionary<string, object>>();
            var it = so.GetIterator();
            if (it.NextVisible(true))
            {
                do
                {
                    if (skipScript && it.propertyPath == "m_Script") continue;
                    properties.Add(Describe(it));
                } while (it.NextVisible(false));
            }
            return new Dictionary<string, object> { { "properties", properties } };
        }

        private Dictionary<string, object> Describe(SerializedProperty p)
        {
            var entry = new Dictionary<string, object> { { "name", p.name } };
            if (Verbose || p.displayName != ObjectNames.NicifyVariableName(p.name))
                entry["displayName"] = p.displayName;
            entry["type"] = TypeLabel(p);
            entry["value"] = Value(p, 0);
            if (Verbose || !p.editable)
                entry["editable"] = p.editable;
            return entry;
        }

        /// <summary>
        /// Verbose keeps Unity's SerializedPropertyType name ("Generic" for every array/struct).
        /// Dense names what "Generic" hides: "int[]", "WeaponSettings".
        /// </summary>
        private string TypeLabel(SerializedProperty p)
        {
            if (Verbose || p.propertyType != SerializedPropertyType.Generic) return p.propertyType.ToString();
            return p.isArray ? p.arrayElementType + "[]" : p.type;
        }

        private object Value(SerializedProperty p, int depth)
        {
            // Strings report isArray on some Unity versions; they are leaves.
            if (p.propertyType == SerializedPropertyType.String) return p.stringValue;

            if (p.isArray)
            {
                int length = p.arraySize;
                if (depth >= MaxDepth && length > 0) return More(p);
                int take = Math.Min(length, MaxArrayElements);
                var items = new List<object>(take);
                for (int i = 0; i < take; i++)
                    items.Add(Value(p.GetArrayElementAtIndex(i), depth + 1));
                if (take == length) return items;
                return new Dictionary<string, object> { { "length", length }, { "items", items } };
            }

            if ((p.propertyType == SerializedPropertyType.Generic || p.propertyType == SerializedPropertyType.ManagedReference)
                && p.hasVisibleChildren)
            {
                if (depth >= MaxDepth) return More(p);
                var fields = new Dictionary<string, object>();
                var it = p.Copy();
                var end = p.GetEndProperty();
                if (it.NextVisible(true))
                {
                    while (!SerializedProperty.EqualContents(it, end))
                    {
                        fields[it.name] = Value(it, depth + 1);
                        if (!it.NextVisible(false)) break;
                    }
                }
                return fields;
            }

            return Leaf(p, Verbose);
        }

        private static Dictionary<string, object> More(SerializedProperty p) =>
            new Dictionary<string, object> { { "$more", p.propertyPath } };

        /// <summary>A non-container property's value. Verbose emits vectors in the legacy object form.</summary>
        internal static object Leaf(SerializedProperty prop, bool verbose)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue;
                case SerializedPropertyType.Boolean: return prop.boolValue;
                case SerializedPropertyType.Float: return prop.floatValue;
                case SerializedPropertyType.String: return prop.stringValue;
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return verbose
                        ? new Dictionary<string, object> { { "r", c.r }, { "g", c.g }, { "b", c.b }, { "a", c.a } }
                        : (object)MCPWire.Vec(c);
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return verbose
                        ? new Dictionary<string, object> { { "x", v2.x }, { "y", v2.y } }
                        : (object)MCPWire.Vec(v2);
                case SerializedPropertyType.Vector3:
                    return MCPWire.Vec(prop.vector3Value, verbose);
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return verbose
                        ? new Dictionary<string, object> { { "x", v4.x }, { "y", v4.y }, { "z", v4.z }, { "w", v4.w } }
                        : (object)MCPWire.Vec(v4);
                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return verbose
                        ? new Dictionary<string, object> { { "x", q.x }, { "y", q.y }, { "z", q.z }, { "w", q.w } }
                        : (object)MCPWire.Vec(q);
                case SerializedPropertyType.Vector2Int:
                    var v2i = prop.vector2IntValue;
                    return new[] { v2i.x, v2i.y };
                case SerializedPropertyType.Vector3Int:
                    var v3i = prop.vector3IntValue;
                    return new[] { v3i.x, v3i.y, v3i.z };
                case SerializedPropertyType.Enum:
                    return prop.enumNames.Length > prop.enumValueIndex && prop.enumValueIndex >= 0
                        ? prop.enumNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    var refObj = prop.objectReferenceValue;
                    if (refObj == null) return null;
                    var info = new Dictionary<string, object>
                    {
                        { "name", refObj.name },
                        { "type", refObj.GetType().Name },
                        { "instanceId", MCPObjectId.Get(refObj) },
                    };
                    string assetPath = AssetDatabase.GetAssetPath(refObj);
                    if (!string.IsNullOrEmpty(assetPath))
                        info["assetPath"] = assetPath;
                    if (refObj is GameObject refGo)
                        info["path"] = MCPWire.HierarchyPath(refGo.transform);
                    else if (refObj is Component refComp)
                        info["path"] = MCPWire.HierarchyPath(refComp.transform);
                    return info;
                case SerializedPropertyType.LayerMask:
                    return prop.intValue;
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return new Dictionary<string, object> { { "x", r.x }, { "y", r.y }, { "width", r.width }, { "height", r.height } };
                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return new Dictionary<string, object>
                    {
                        { "center", MCPWire.Vec(b.center, verbose) },
                        { "size", MCPWire.Vec(b.size, verbose) },
                    };
                default:
                    return prop.propertyType.ToString();
            }
        }
    }
}
