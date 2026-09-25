using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPAssetCommands
    {
        public static object List(Dictionary<string, object> args)
        {
            string folder = args.ContainsKey("folder") ? args["folder"].ToString() : "Assets";
            string typeFilter = args.ContainsKey("type") ? args["type"].ToString() : null;
            string search = args.ContainsKey("search") ? args["search"].ToString() : null;
            bool recursive = !args.ContainsKey("recursive") || Convert.ToBoolean(args["recursive"]);
            // maxResults was advertised by the MCP tool but never read: every call returned
            // the whole folder, however large.
            int maxResults = Math.Max(0, MCPArgs.GetInt(args, "maxResults", 500));
            int offset = Math.Max(0, MCPArgs.GetInt(args, "offset", 0));
            bool includeGuid = MCPArgs.GetBool(args, "includeGuid", true);
            bool verbose = MCPWire.IsVerbose(args);

            string searchQuery = "";
            if (!string.IsNullOrEmpty(search))
                searchQuery = search;
            if (!string.IsNullOrEmpty(typeFilter))
                searchQuery += $" t:{typeFilter}";

            string[] guids;
            if (!string.IsNullOrEmpty(searchQuery))
            {
                guids = AssetDatabase.FindAssets(searchQuery.Trim(), new[] { folder });
            }
            else
            {
                guids = AssetDatabase.FindAssets("", new[] { folder });
            }

            // Filter first (cheap string work), then page, then pay for type lookups only on
            // the page actually returned.
            string normalizedFolder = folder.TrimEnd('/');
            var matches = new List<(string guid, string path)>(guids.Length);
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!recursive)
                {
                    int slash = path.LastIndexOf('/');
                    if (slash < 0 || !string.Equals(path.Substring(0, slash), normalizedFolder, StringComparison.Ordinal))
                        continue;
                }
                matches.Add((guid, path));
            }

            int end = (int)Math.Min((long)offset + maxResults, matches.Count);
            var assets = new List<Dictionary<string, object>>(Math.Max(0, end - offset));
            for (int i = offset; i < end; i++)
            {
                var (guid, path) = matches[i];
                bool isFolder = AssetDatabase.IsValidFolder(path);
                if (verbose)
                {
                    var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                    assets.Add(new Dictionary<string, object>
                    {
                        { "path", path },
                        { "name", Path.GetFileName(path) },
                        { "type", assetType?.Name ?? "Unknown" },
                        { "guid", guid },
                        { "isFolder", isFolder },
                    });
                    continue;
                }
                // Dense: name is the path's last segment and isFolder is type "Folder".
                var row = new Dictionary<string, object>
                {
                    { "path", path },
                    { "type", isFolder ? "Folder" : AssetDatabase.GetMainAssetTypeAtPath(path)?.Name ?? "Unknown" },
                };
                if (includeGuid) row["guid"] = guid;
                assets.Add(row);
            }

            var result = new Dictionary<string, object>
            {
                { "folder", folder },
                { "totalFound", matches.Count },
                { "count", assets.Count },
            };
            if (offset > 0) result["offset"] = offset;
            if (end < matches.Count)
            {
                result["truncated"] = true;
                result["nextOffset"] = end;
            }
            if (verbose)
                result["assets"] = assets;
            else
                foreach (var kv in MCPWire.Table(assets, groupKey: "path", leafColumn: "file", groupedBy: "folder"))
                    result[kv.Key] = kv.Value;
            return result;
        }

        public static object Import(Dictionary<string, object> args)
        {
            string source = args.ContainsKey("sourcePath") ? args["sourcePath"].ToString() : "";
            string dest = args.ContainsKey("destinationPath") ? args["destinationPath"].ToString() : "";

            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
                return new { error = "sourcePath and destinationPath are required" };

            if (!File.Exists(source))
                return new { error = $"Source file not found: {source}" };

            // Confine the destination under the project (correct root, no traversal escape).
            if (!MCPAssetSafety.TryResolveProjectPath(dest, out string fullDest, out string pathError))
                return new { error = pathError };

            // Don't silently overwrite an existing project asset unless asked.
            var overwriteError = MCPAssetSafety.OverwriteGuard(dest, args);
            if (overwriteError != null)
                return overwriteError;

            string destDir = Path.GetDirectoryName(fullDest);
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Copy(source, fullDest, true);
            AssetDatabase.ImportAsset(MCPAssetSafety.ToAssetDatabasePath(dest));

            return new { success = true, importedPath = dest };
        }

        /// <summary>
        /// Import every change made to project files outside Unity (IDE, agent file tools, git)
        /// and compile scripts when any changed — the explicit Assets > Refresh (Ctrl+R).
        ///
        /// Unity's Auto Refresh only scans on focus regain; an explicit refresh has no focus
        /// requirement and runs as soon as the main thread ticks, which it keeps doing while the
        /// editor is in the background. Compilation and the domain reload that follows finish
        /// AFTER this returns: callers watch ping (isCompiling, epoch) to know when they are done.
        /// </summary>
        public static object Refresh(Dictionary<string, object> args)
        {
            bool forceRecompile = MCPArgs.GetBool(args, "forceRecompile", false);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            AssetDatabase.Refresh();
            if (forceRecompile)
                CompilationPipeline.RequestScriptCompilation();
            timer.Stop();

            // RequestScriptCompilation starts on the next tick, so isCompiling can still be false.
            bool compiling = EditorApplication.isCompiling || forceRecompile;
            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "compiling", compiling },
                { "refreshMs", timer.ElapsedMilliseconds },
                { "epoch", MCPEditorHealth.Epoch },
            };
            if (compiling)
            {
                result["next"] = "Scripts are compiling; a domain reload follows on success. Done when ping shows " +
                                 "isCompiling=false and a new epoch; if the epoch is unchanged, check compilation/errors.";
            }
            else if (EditorApplication.isPlaying)
            {
                result["note"] = "In Play Mode, changed scripts compile according to Preferences > General > " +
                                 "Script Changes While Playing (by default after Play Mode exits).";
            }
            return result;
        }

        public static object Delete(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            // Confine to the project like every other asset writer — a traversal/absolute path
            // reached AssetDatabase.DeleteAsset raw before this.
            if (!MCPAssetSafety.TryResolveProjectPath(path, out _, out var pathError))
                return new { error = pathError };
            string assetPath = MCPAssetSafety.ToAssetDatabasePath(path);

            // Existence check that holds on the whole supported range (AssetPathExists is 2023.2+,
            // this package targets 2021.3): a real asset loads, a folder answers IsValidFolder.
            bool exists = AssetDatabase.IsValidFolder(assetPath)
                || AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null;
            if (!exists)
                return new { error = $"Asset not found: {assetPath}" };

            // DeleteAsset on a FOLDER is silently recursive: one truncated path
            // ('Assets/Art/Materials' instead of the .mat) takes the whole tree. Asset deletion
            // also registers nothing on the undo stack, so undo/last cannot bring it back —
            // require an explicit opt-in and disclose the blast radius first.
            bool isFolder = AssetDatabase.IsValidFolder(assetPath);
            if (isFolder)
            {
                var contained = AssetDatabase.FindAssets("", new[] { assetPath });
                if (!(args.ContainsKey("recursive") && Convert.ToBoolean(args["recursive"])))
                    return new
                    {
                        error = $"'{assetPath}' is a FOLDER containing {contained.Length} asset(s). Deleting it removes them all and is not undoable. Pass recursive:true to confirm.",
                        requiresRecursive = true,
                        isFolder = true,
                        assetCount = contained.Length,
                    };
            }

            // Default to the OS trash so a mistake stays recoverable; permanent:true keeps the
            // old hard-delete behaviour for callers that really mean it.
            bool permanent = args.ContainsKey("permanent") && Convert.ToBoolean(args["permanent"]);
            bool deleted = permanent
                ? AssetDatabase.DeleteAsset(assetPath)
                : AssetDatabase.MoveAssetToTrash(assetPath);

            return new
            {
                success = deleted,
                path = assetPath,
                isFolder,
                recoverable = !permanent,
                method = permanent ? "deleted permanently" : "moved to OS trash",
            };
        }

        public static object CreatePrefab(Dictionary<string, object> args)
        {
            string goPath = args.ContainsKey("gameObjectPath") ? args["gameObjectPath"].ToString() : "";
            string savePath = args.ContainsKey("savePath") ? args["savePath"].ToString() : "";

            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            if (string.IsNullOrEmpty(savePath))
                return new { error = "savePath is required" };

            // Same confinement + clobber guard CreateMaterial already uses below. Without it,
            // saving over an existing prefab kept the .meta GUID, so every scene reference
            // silently re-bound to the new asset instead of erroring.
            if (!MCPAssetSafety.TryResolveProjectPath(savePath, out _, out var prefabPathError))
                return new { error = prefabPathError };
            var prefabOverwrite = MCPAssetSafety.OverwriteGuard(savePath, args);
            if (prefabOverwrite != null) return prefabOverwrite;

            // Ensure directory exists
            string dir = Path.GetDirectoryName(savePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string[] parts = dir.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, savePath);
            return new Dictionary<string, object>
            {
                { "success", prefab != null },
                { "path", savePath },
                { "name", prefab?.name },
            };
        }

        public static object InstantiatePrefab(Dictionary<string, object> args)
        {
            string prefabPath = args.ContainsKey("prefabPath") ? args["prefabPath"].ToString() : "";
            if (string.IsNullOrEmpty(prefabPath))
                return new { error = "prefabPath is required" };

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found at {prefabPath}" };

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null) return new { error = "Failed to instantiate prefab" };

            if (args.ContainsKey("name"))
                instance.name = args["name"].ToString();

            if (args.ContainsKey("position"))
                instance.transform.position = MCPGameObjectCommands.DictToVector3(args["position"]);

            if (args.ContainsKey("rotation"))
                instance.transform.eulerAngles = MCPGameObjectCommands.DictToVector3(args["rotation"]);

            if (args.ContainsKey("parent"))
            {
                var parent = GameObject.Find(args["parent"].ToString());
                if (parent != null) instance.transform.SetParent(parent.transform);
            }

            Undo.RegisterCreatedObjectUndo(instance, $"Instantiate {prefab.name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", instance.name },
                { "instanceId", MCPObjectId.Get(instance) },
                { "position", MCPWire.Vec(instance.transform.position) },
            };
        }

        public static object CreateMaterial(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string shaderName = args.ContainsKey("shader") ? args["shader"].ToString() : "Standard";

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var shader = Shader.Find(shaderName);
            if (shader == null) return new { error = $"Shader '{shaderName}' not found" };

            // Don't reset an existing tuned material back to defaults.
            var materialOverwrite = MCPAssetSafety.OverwriteGuard(path, args);
            if (materialOverwrite != null)
                return materialOverwrite;

            var material = new Material(shader);

            if (args.ContainsKey("color"))
            {
                var cd = args["color"] as Dictionary<string, object>;
                if (cd != null)
                {
                    material.color = new Color(
                        Convert.ToSingle(cd.GetValueOrDefault("r", 1f)),
                        Convert.ToSingle(cd.GetValueOrDefault("g", 1f)),
                        Convert.ToSingle(cd.GetValueOrDefault("b", 1f)),
                        Convert.ToSingle(cd.GetValueOrDefault("a", 1f))
                    );
                }
            }

            // Ensure directory exists (normalize backslashes from Path.GetDirectoryName on Windows)
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string[] parts = dir.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();

            return new { success = true, path, shader = shaderName };
        }
    }
}
