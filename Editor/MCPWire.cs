using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Token-dense response shapes shared by the read endpoints.
    ///
    /// Every response is read by an LLM, so bytes are tokens. The shapes here drop only what
    /// the reader can reconstruct — never information:
    /// <list type="bullet">
    /// <item>vectors/colors as arrays: <c>[x,y,z]</c>, <c>[r,g,b,a]</c> (inputs accept both forms, see MCPArgs);</item>
    /// <item><see cref="Table"/>: a row list as <c>columns</c> + tuples, with columns that hold the same
    /// value on every row hoisted once into <c>common</c>, and optionally grouped by path prefix.</item>
    /// </list>
    /// Endpoints that adopt a dense shape keep the previous one behind <c>verbose:true</c>.
    /// </summary>
    internal static class MCPWire
    {
        /// <summary>The per-request escape hatch back to the pre-dense response shape.</summary>
        public static bool IsVerbose(Dictionary<string, object> args) => MCPArgs.GetBool(args, "verbose", false);

        // ─── Vectors ───

        public static float[] Vec(Vector2 v) => new[] { v.x, v.y };
        public static float[] Vec(Vector3 v) => new[] { v.x, v.y, v.z };
        public static float[] Vec(Vector4 v) => new[] { v.x, v.y, v.z, v.w };
        public static float[] Vec(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
        public static float[] Vec(Color c) => new[] { c.r, c.g, c.b, c.a };

        /// <summary>Array form when dense, the legacy {x,y,z} object when verbose.</summary>
        public static object Vec(Vector3 v, bool verbose) =>
            verbose ? new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } } : (object)Vec(v);

        // ─── Hierarchy paths ───

        /// <summary>"Root/Child/Leaf" — one allocation instead of a string concat per ancestor.</summary>
        public static string HierarchyPath(Transform t)
        {
            if (t.parent == null) return t.name;
            var names = new List<string>(8);
            for (var cur = t; cur != null; cur = cur.parent) names.Add(cur.name);
            var sb = new StringBuilder(names.Count * 12);
            for (int i = names.Count - 1; i >= 0; i--)
            {
                sb.Append(names[i]);
                if (i > 0) sb.Append('/');
            }
            return sb.ToString();
        }

        // ─── Tables ───

        /// <summary>
        /// Encode homogeneous rows losslessly as
        /// <c>{ columns:[...], common?:{col:value}, rows:[[...]] }</c>, or — with
        /// <paramref name="groupKey"/> — <c>{ columns, common?, groupedBy, groups:{prefix:[[...]]} }</c>
        /// where each row's <paramref name="groupKey"/> value is split at its last '/' into the group
        /// (prefix) and the first column <paramref name="leafColumn"/> (the part after it).
        /// The full value is therefore <c>prefix + "/" + leaf</c>, or just the leaf when the prefix is "".
        /// </summary>
        /// <param name="rows">Rows to encode; a row missing a column encodes null in that cell.</param>
        /// <param name="groupKey">Optional path-valued column to group by (removed from the columns).</param>
        /// <param name="leafColumn">Column that receives the last path segment when grouping.</param>
        /// <param name="groupedBy">Label emitted as <c>groupedBy</c>, telling the reader what the group keys are.</param>
        public static Dictionary<string, object> Table(
            IReadOnlyList<Dictionary<string, object>> rows,
            string groupKey = null, string leafColumn = "name", string groupedBy = null)
        {
            bool grouping = groupKey != null;

            // Column order = first-seen order; the leaf column leads when grouping.
            var columns = new List<string>();
            var seen = new HashSet<string>();
            if (grouping) { columns.Add(leafColumn); seen.Add(leafColumn); seen.Add(groupKey); }
            foreach (var row in rows)
                foreach (var key in row.Keys)
                    if (seen.Add(key)) columns.Add(key);

            // Hoist columns whose (scalar) value is identical on every row. Needs >= 2 rows, or a
            // single row would be hoisted away entirely. The leaf column is always per-row.
            var common = new Dictionary<string, object>();
            if (rows.Count >= 2)
            {
                foreach (var col in columns)
                {
                    if (grouping && col == leafColumn) continue;
                    if (!rows[0].TryGetValue(col, out object first) || !IsScalar(first)) continue;
                    bool constant = true;
                    for (int i = 1; i < rows.Count && constant; i++)
                        constant = rows[i].TryGetValue(col, out object v) && Equals(first, v);
                    if (constant) common[col] = first;
                }
                columns.RemoveAll(common.ContainsKey);
            }

            var result = new Dictionary<string, object> { { "columns", columns } };
            if (common.Count > 0) result["common"] = common;

            if (!grouping)
            {
                var tuples = new List<object>(rows.Count);
                foreach (var row in rows) tuples.Add(Cells(row, columns, null));
                result["rows"] = tuples;
                return result;
            }

            var groups = new Dictionary<string, object>();
            foreach (var row in rows)
            {
                string full = row.TryGetValue(groupKey, out object p) && p != null ? p.ToString() : "";
                string prefix, leaf;
                // Prefer the row's own leaf value: a name containing '/' must not be split.
                if (row.TryGetValue(leafColumn, out object own) && own is string ownLeaf && ownLeaf.Length > 0
                    && (full == ownLeaf || full.EndsWith("/" + ownLeaf)))
                {
                    leaf = ownLeaf;
                    prefix = full.Length == ownLeaf.Length ? "" : full.Substring(0, full.Length - ownLeaf.Length - 1);
                }
                else
                {
                    int slash = full.LastIndexOf('/');
                    prefix = slash < 0 ? "" : full.Substring(0, slash);
                    leaf = slash < 0 ? full : full.Substring(slash + 1);
                }
                if (!groups.TryGetValue(prefix, out object bucket))
                    groups[prefix] = bucket = new List<object>();
                ((List<object>)bucket).Add(Cells(row, columns, leaf));
            }
            result["groupedBy"] = groupedBy ?? groupKey;
            result["groups"] = groups;
            return result;

            object[] Cells(Dictionary<string, object> row, List<string> cols, string leaf)
            {
                var cells = new object[cols.Count];
                for (int i = 0; i < cols.Count; i++)
                {
                    if (leaf != null && i == 0) { cells[i] = leaf; continue; }
                    row.TryGetValue(cols[i], out cells[i]);
                }
                return cells;
            }
        }

        private static bool IsScalar(object v) => v == null || v is string || v is bool || v.GetType().IsPrimitive;
    }
}
