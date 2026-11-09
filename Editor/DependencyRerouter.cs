using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DependencyRerouterTool
{
    public enum AssetCategory
    {
        Prefab, Scene, Model, Material, Texture, Animation, Animator, AvatarMask,
        VRChat, Shader, Script, Audio, Other
    }

    public enum TransferMode { Move, Copy }
    public enum ConflictMode { Skip, Rename }

    [Serializable]
    public class DepEntry
    {
        public string path;
        public string guid;
        public AssetCategory category;
        public long size;
        public bool selected;
        public bool isPackage;
        public int usedByOthers = -1; // -1 = not scanned
    }

    public class PlanItem
    {
        public DepEntry entry;
        public string newPath;
        public string note; // null = ok
        public bool skip;
    }

    public class TransferResult
    {
        public int done, skipped, failed;
        public readonly List<string> log = new List<string>();
        public readonly Dictionary<string, string> guidMap = new Dictionary<string, string>(); // old guid -> new guid (copy)
        public readonly Dictionary<string, string> pathMap = new Dictionary<string, string>(); // old path -> new path
    }

    public static class DependencyRerouter
    {
        static readonly HashSet<string> YamlExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".mat", ".controller", ".overrideController", ".anim", ".mask",
            ".asset", ".playable", ".physicMaterial", ".physicsMaterial2D", ".renderTexture",
            ".cubemap", ".flare", ".guiskin", ".fontsettings", ".lighting", ".signal", ".preset"
        };

        static readonly Regex GuidRegex = new Regex(@"guid: ([0-9a-fA-F]{32})", RegexOptions.Compiled);

        // ---------- Gathering ----------

        public static List<string> CollectPaths(Object source, bool recursive)
        {
            var result = new HashSet<string>();
            if (source == null) return new List<string>();

            if (EditorUtility.IsPersistent(source))
            {
                string p = AssetDatabase.GetAssetPath(source);
                if (!string.IsNullOrEmpty(p))
                    foreach (var d in AssetDatabase.GetDependencies(p, recursive)) result.Add(d);
            }
            else
            {
                // Scene object: collect every asset its components reference, then expand.
                var roots = EditorUtility.CollectDependencies(new[] { source });
                var direct = roots.Where(o => o != null && EditorUtility.IsPersistent(o))
                                  .Select(AssetDatabase.GetAssetPath)
                                  .Where(p => !string.IsNullOrEmpty(p))
                                  .Distinct().ToArray();
                foreach (var d in direct) result.Add(d);
                if (recursive && direct.Length > 0)
                    foreach (var d in AssetDatabase.GetDependencies(direct, true)) result.Add(d);
            }

            return result.Where(p => (p.StartsWith("Assets/") || p.StartsWith("Packages/"))
                                     && !AssetDatabase.IsValidFolder(p))
                         .OrderBy(p => p).ToList();
        }

        public static List<DepEntry> BuildEntries(IEnumerable<string> paths)
        {
            var list = new List<DepEntry>();
            foreach (var p in paths)
            {
                long size = 0;
                try { var fi = new FileInfo(Path.GetFullPath(p)); if (fi.Exists) size = fi.Length; } catch { }
                bool pkg = p.StartsWith("Packages/");
                list.Add(new DepEntry
                {
                    path = p,
                    guid = AssetDatabase.AssetPathToGUID(p),
                    category = Classify(p),
                    size = size,
                    isPackage = pkg,
                    selected = !pkg
                });
            }
            return list;
        }

        public static AssetCategory Classify(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".prefab": return AssetCategory.Prefab;
                case ".unity": return AssetCategory.Scene;
                case ".fbx": case ".obj": case ".blend": case ".dae": case ".3ds": case ".max": case ".mesh":
                    return AssetCategory.Model;
                case ".mat": return AssetCategory.Material;
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd": case ".tif": case ".tiff":
                case ".exr": case ".hdr": case ".bmp": case ".gif": case ".rendertexture": case ".cubemap":
                    return AssetCategory.Texture;
                case ".anim": return AssetCategory.Animation;
                case ".controller": case ".overridecontroller": return AssetCategory.Animator;
                case ".mask": return AssetCategory.AvatarMask;
                case ".shader": case ".cginc": case ".hlsl": case ".glslinc": case ".shadergraph":
                case ".shadersubgraph": case ".compute": case ".orlshader": case ".poiyomi":
                    return AssetCategory.Shader;
                case ".cs": case ".asmdef": case ".asmref": case ".dll": return AssetCategory.Script;
                case ".wav": case ".mp3": case ".ogg": case ".aif": case ".aiff": case ".flac":
                    return AssetCategory.Audio;
            }

            var t = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (t == null) return AssetCategory.Other;
            if (t.Name.Contains("VRCExpression") || (t.Namespace != null && t.Namespace.StartsWith("VRC")))
                return AssetCategory.VRChat;
            if (typeof(Texture).IsAssignableFrom(t)) return AssetCategory.Texture;
            if (typeof(Mesh).IsAssignableFrom(t)) return AssetCategory.Model;
            if (typeof(AnimationClip).IsAssignableFrom(t)) return AssetCategory.Animation;
            if (typeof(RuntimeAnimatorController).IsAssignableFrom(t)) return AssetCategory.Animator;
            if (typeof(AvatarMask).IsAssignableFrom(t)) return AssetCategory.AvatarMask;
            if (typeof(Material).IsAssignableFrom(t)) return AssetCategory.Material;
            if (typeof(AudioClip).IsAssignableFrom(t)) return AssetCategory.Audio;
            return AssetCategory.Other;
        }

        /// <summary>How many assets outside the dependency set use each entry. Slow: scans whole project.</summary>
        public static bool ScanSharedUsage(List<DepEntry> entries, string sourcePath)
        {
            var set = new HashSet<string>(entries.Select(e => e.path));
            var counts = entries.ToDictionary(e => e.path, e => 0);
            var all = AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/")).ToArray();
            try
            {
                for (int i = 0; i < all.Length; i++)
                {
                    string user = all[i];
                    if (i % 50 == 0 && EditorUtility.DisplayCancelableProgressBar(
                            "Scanning shared usage", user, (float)i / all.Length))
                        return false;
                    if (set.Contains(user) || user == sourcePath || AssetDatabase.IsValidFolder(user)) continue;
                    foreach (var d in AssetDatabase.GetDependencies(user, false))
                        if (d != user && counts.ContainsKey(d)) counts[d]++;
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            foreach (var e in entries) e.usedByOthers = counts[e.path];
            return true;
        }

        // ---------- Planning ----------

        public static bool IsLocked(DepEntry e, TransferMode mode)
        {
            if (e.isPackage) return true;
            // Copying scripts duplicates class names -> compile errors.
            if (mode == TransferMode.Copy && e.category == AssetCategory.Script) return true;
            return false;
        }

        public static string LockReason(DepEntry e, TransferMode mode)
        {
            if (e.isPackage) return "Lives in Packages/ (managed by VCC) - can't be moved or copied.";
            if (mode == TransferMode.Copy && e.category == AssetCategory.Script)
                return "Scripts can't be copied (duplicate class names break compilation). Use Move instead.";
            return null;
        }

        public static string CommonFolder(IEnumerable<string> paths)
        {
            string[] common = null;
            foreach (var p in paths)
            {
                var parts = Path.GetDirectoryName(p).Replace('\\', '/').Split('/');
                if (common == null) { common = parts; continue; }
                int n = 0;
                while (n < common.Length && n < parts.Length && common[n] == parts[n]) n++;
                common = common.Take(n).ToArray();
            }
            if (common == null || common.Length == 0) return "Assets";
            return string.Join("/", common);
        }

        public static List<PlanItem> BuildPlan(List<DepEntry> entries, string dest, string root,
            TransferMode mode, ConflictMode conflict, bool skipAlreadyInDest)
        {
            var plan = new List<PlanItem>();
            dest = dest.TrimEnd('/');
            root = root.TrimEnd('/');
            var claimed = new HashSet<string>();

            foreach (var e in entries)
            {
                if (!e.selected || IsLocked(e, mode)) continue;
                var item = new PlanItem { entry = e };

                string rel = e.path.StartsWith(root + "/")
                    ? e.path.Substring(root.Length + 1)
                    : e.path.Substring(e.path.IndexOf('/') + 1); // outside root: keep full path minus "Assets/"
                item.newPath = dest + "/" + rel;

                if (skipAlreadyInDest && e.path.StartsWith(dest + "/"))
                {
                    item.skip = true; item.note = "Already in destination";
                }
                else if (item.newPath == e.path)
                {
                    item.skip = true; item.note = "Same location";
                }
                else if (File.Exists(item.newPath) || claimed.Contains(item.newPath))
                {
                    if (conflict == ConflictMode.Skip)
                    {
                        item.skip = true; item.note = "File exists - skipped";
                    }
                    else
                    {
                        item.newPath = UniquePath(item.newPath, claimed);
                        item.note = "Renamed (file existed)";
                    }
                }
                if (!item.skip) claimed.Add(item.newPath);
                plan.Add(item);
            }
            return plan;
        }

        static string UniquePath(string path, HashSet<string> claimed)
        {
            string dir = Path.GetDirectoryName(path).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string candidate = $"{dir}/{name} {i}{ext}";
                if (!File.Exists(candidate) && !claimed.Contains(candidate)) return candidate;
            }
        }

        // ---------- Execution ----------

        static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        public static TransferResult Execute(List<PlanItem> plan, TransferMode mode)
        {
            var res = new TransferResult();
            var work = plan.Where(p => !p.skip).ToList();
            res.skipped = plan.Count - work.Count;
            foreach (var s in plan.Where(p => p.skip)) res.log.Add($"SKIP  {s.entry.path}  ({s.note})");

            AssetDatabase.SaveAssets(); // make sure on-disk files are current before moving/copying

            foreach (var dir in work.Select(w => Path.GetDirectoryName(w.newPath).Replace('\\', '/')).Distinct())
                EnsureFolder(dir);

            if (mode == TransferMode.Move) DoMove(work, res);
            else DoCopy(work, res);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return res;
        }

        static void DoMove(List<PlanItem> work, TransferResult res)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < work.Count; i++)
                {
                    var w = work[i];
                    EditorUtility.DisplayProgressBar("Moving assets", w.entry.path, (float)i / work.Count);
                    string err = AssetDatabase.MoveAsset(w.entry.path, w.newPath);
                    if (string.IsNullOrEmpty(err))
                    {
                        res.done++;
                        res.pathMap[w.entry.path] = w.newPath;
                        res.log.Add($"MOVE  {w.entry.path}  ->  {w.newPath}");
                    }
                    else
                    {
                        res.failed++;
                        res.log.Add($"FAIL  {w.entry.path}: {err}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }
        }

        static void DoCopy(List<PlanItem> work, TransferResult res)
        {
            var copied = new List<string>();
            try
            {
                // 1. Copy files (not batched, so new GUIDs are available immediately).
                for (int i = 0; i < work.Count; i++)
                {
                    var w = work[i];
                    EditorUtility.DisplayProgressBar("Copying assets", w.entry.path, (float)i / work.Count * 0.7f);
                    if (AssetDatabase.CopyAsset(w.entry.path, w.newPath))
                    {
                        string newGuid = AssetDatabase.AssetPathToGUID(w.newPath);
                        if (!string.IsNullOrEmpty(w.entry.guid) && !string.IsNullOrEmpty(newGuid))
                            res.guidMap[w.entry.guid] = newGuid;
                        res.pathMap[w.entry.path] = w.newPath;
                        copied.Add(w.newPath);
                        res.done++;
                        res.log.Add($"COPY  {w.entry.path}  ->  {w.newPath}");
                    }
                    else
                    {
                        res.failed++;
                        res.log.Add($"FAIL  {w.entry.path}: CopyAsset failed");
                    }
                }

                // 2. Rewrite GUID references inside the copies (and their .meta, for model material remaps).
                var touched = new List<string>();
                for (int i = 0; i < copied.Count; i++)
                {
                    string p = copied[i];
                    EditorUtility.DisplayProgressBar("Rewiring references", p, 0.7f + (float)i / copied.Count * 0.3f);
                    bool changed = false;
                    if (YamlExts.Contains(Path.GetExtension(p))) changed |= RewriteGuids(p, res.guidMap);
                    changed |= RewriteGuids(p + ".meta", res.guidMap, skipFirstGuid: true);
                    if (changed) touched.Add(p);
                }

                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var p in touched) AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
                }
                finally { AssetDatabase.StopAssetEditing(); }
                res.log.Add($"Rewired references in {touched.Count} copied file(s).");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static bool RewriteGuids(string file, Dictionary<string, string> map, bool skipFirstGuid = false)
        {
            string full = Path.GetFullPath(file);
            if (!File.Exists(full) || map.Count == 0) return false;
            string text;
            try { text = File.ReadAllText(full); } catch { return false; }
            if (text.IndexOf("guid:", StringComparison.Ordinal) < 0) return false; // no references

            bool first = true, changed = false;
            string output = GuidRegex.Replace(text, m =>
            {
                if (skipFirstGuid && first) { first = false; return m.Value; } // the .meta's own guid
                first = false;
                if (map.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var ng))
                {
                    changed = true;
                    return "guid: " + ng;
                }
                return m.Value;
            });
            if (changed) File.WriteAllText(full, output, new UTF8Encoding(false));
            return changed;
        }

        /// <summary>
        /// For a scene-object source in Copy mode: duplicate it and point every asset reference at the copies.
        /// </summary>
        public static GameObject DuplicateAndRewire(GameObject source, TransferResult res)
        {
            var clone = Object.Instantiate(source, source.transform.parent);
            clone.name = source.name + " (Rerouted)";
            clone.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            Undo.RegisterCreatedObjectUndo(clone, "Duplicate rerouted object");

            var cache = new Dictionary<string, Dictionary<long, Object>>();
            int swapped = 0;

            foreach (var comp in clone.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue; // missing script
                var so = new SerializedObject(comp);
                var it = so.GetIterator();
                bool any = false;
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var obj = it.objectReferenceValue;
                    if (obj == null || !EditorUtility.IsPersistent(obj)) continue;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long lid)) continue;
                    if (!res.guidMap.TryGetValue(guid, out string newGuid)) continue;

                    string newPath = AssetDatabase.GUIDToAssetPath(newGuid);
                    if (!cache.TryGetValue(newPath, out var byId))
                    {
                        byId = new Dictionary<long, Object>();
                        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(newPath))
                            if (a != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out _, out long l))
                                byId[l] = a;
                        cache[newPath] = byId;
                    }
                    if (byId.TryGetValue(lid, out var replacement) && replacement.GetType() == obj.GetType())
                    {
                        it.objectReferenceValue = replacement;
                        any = true;
                        swapped++;
                    }
                }
                if (any) so.ApplyModifiedPropertiesWithoutUndo();
            }

            res.log.Add($"Created '{clone.name}' in scene with {swapped} reference(s) pointed at the copies.");
            return clone;
        }
    }
}
