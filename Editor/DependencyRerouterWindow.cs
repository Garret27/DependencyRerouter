using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DependencyRerouterTool
{
    public class DependencyRerouterWindow : EditorWindow
    {
        enum SortMode { Type, Path, Size, SharedUsage }
        enum ViewMode { Folders, Flat }

        class FolderNode
        {
            public string name, path;
            public readonly SortedDictionary<string, FolderNode> children =
                new SortedDictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            public readonly List<DepEntry> files = new List<DepEntry>();
            public int total, selectable, selected;
            public long size;
        }

        struct Row
        {
            public int depth;
            public FolderNode folder; // folder row when non-null
            public string label;
            public DepEntry entry;   // file row otherwise
        }

        [SerializeField] ViewMode viewMode = ViewMode.Folders;
        [SerializeField] List<string> expanded = new List<string> { "Assets", "Packages" };
        List<Row> rows;
        FolderNode treeRoot;

        [SerializeField] Object source;
        [SerializeField] bool usePrefabAsset = true;
        [SerializeField] bool recursive = true;
        [SerializeField] List<DepEntry> entries;
        [SerializeField] string scannedSourcePath;
        [SerializeField] List<AssetCategory> hiddenCategories = new List<AssetCategory>();
        [SerializeField] string search = "";
        [SerializeField] SortMode sort = SortMode.Type;
        [SerializeField] string destination = "Assets/Rerouted";
        [SerializeField] string root = "Assets";
        [SerializeField] TransferMode mode = TransferMode.Copy;
        [SerializeField] ConflictMode conflict = ConflictMode.Skip;
        [SerializeField] bool skipAlreadyInDest = true;
        [SerializeField] bool duplicateSceneObject = true;
        [SerializeField] bool showPreview;
        [SerializeField] bool sharedScanned;

        Vector2 listScroll, previewScroll, windowScroll;
        List<PlanItem> plan;
        bool planDirty = true;
        int firstRow, lastRow = 40;

        [MenuItem("Tools/Dependency Rerouter")]
        public static void Open() => OpenWith(Selection.activeObject);

        [MenuItem("Assets/Reroute Dependencies...", false, 2000)]
        static void OpenFromAssets() => OpenWith(Selection.activeObject);

        [MenuItem("GameObject/Reroute Dependencies...", false, 49)]
        static void OpenFromHierarchy() => OpenWith(Selection.activeGameObject);

        static void OpenWith(Object obj)
        {
            var w = GetWindow<DependencyRerouterWindow>("Dependency Rerouter");
            w.minSize = new Vector2(620, 480);
            if (obj != null && !AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(obj)))
            {
                w.source = obj;
                w.Scan();
            }
            w.Show();
        }

        // ---------- Helpers ----------

        bool SourceIsSceneObject => source is GameObject go && !EditorUtility.IsPersistent(go);

        bool SourceIsPrefabInstance => SourceIsSceneObject &&
                                       PrefabUtility.IsPartOfPrefabInstance(source);

        Object EffectiveSource()
        {
            if (SourceIsPrefabInstance && usePrefabAsset)
            {
                string p = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
                var asset = AssetDatabase.LoadMainAssetAtPath(p);
                if (asset != null) return asset;
            }
            if (source is Component c) return c.gameObject;
            return source;
        }

        void Scan()
        {
            var src = EffectiveSource();
            if (src == null) return;
            scannedSourcePath = EditorUtility.IsPersistent(src) ? AssetDatabase.GetAssetPath(src) : null;
            entries = DependencyRerouter.BuildEntries(DependencyRerouter.CollectPaths(src, recursive));
            sharedScanned = false;
            planDirty = true;
            rows = null;
            expanded = new List<string> { "Assets", "Packages" };

            if (destination == "Assets/Rerouted" && src != null)
                destination = "Assets/Rerouted/" + SanitizeName(src.name);
        }

        static string SanitizeName(string n)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n.Trim();
        }

        static string FormatSize(long b)
        {
            if (b >= 1 << 20) return (b / (1024f * 1024f)).ToString("0.0") + " MB";
            if (b >= 1 << 10) return (b / 1024f).ToString("0") + " KB";
            return b + " B";
        }

        IEnumerable<DepEntry> Visible()
        {
            IEnumerable<DepEntry> q = entries.Where(e => !hiddenCategories.Contains(e.category));
            if (!string.IsNullOrEmpty(search))
                q = q.Where(e => e.path.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            switch (sort)
            {
                case SortMode.Type: q = q.OrderBy(e => e.category).ThenBy(e => e.path); break;
                case SortMode.Path: q = q.OrderBy(e => e.path); break;
                case SortMode.Size: q = q.OrderByDescending(e => e.size); break;
                case SortMode.SharedUsage: q = q.OrderByDescending(e => e.usedByOthers).ThenBy(e => e.path); break;
            }
            return q;
        }

        // ---------- GUI ----------

        void OnGUI()
        {
            windowScroll = EditorGUILayout.BeginScrollView(windowScroll);

            DrawSource();
            if (entries == null)
            {
                EditorGUILayout.HelpBox("Pick a prefab, avatar in the scene, or any asset, then press Scan.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.Space(4);
            DrawFilters();
            DrawList();
            EditorGUILayout.Space(6);
            DrawDestination();

            if (EditorGUI.EndChangeCheck()) planDirty = true;

            DrawPreviewAndExecute();
            EditorGUILayout.EndScrollView();
        }

        void DrawSource()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                source = EditorGUILayout.ObjectField(source, typeof(Object), true);
                if (GUILayout.Button("Use Selection", GUILayout.Width(100))) source = Selection.activeObject;
                using (new EditorGUI.DisabledScope(source == null))
                    if (GUILayout.Button("Scan", GUILayout.Width(70))) Scan();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                recursive = EditorGUILayout.ToggleLeft("Include nested dependencies", recursive, GUILayout.Width(200));
                if (SourceIsPrefabInstance)
                    usePrefabAsset = EditorGUILayout.ToggleLeft(new GUIContent("Use prefab asset",
                        "On: scan the prefab file this scene object comes from.\nOff: scan the scene object itself (includes scene overrides)."),
                        usePrefabAsset);
            }
        }

        void DrawFilters()
        {
            int total = entries.Count;
            int sel = entries.Count(e => e.selected && !DependencyRerouter.IsLocked(e, mode));
            long selSize = entries.Where(e => e.selected && !DependencyRerouter.IsLocked(e, mode)).Sum(e => e.size);
            EditorGUILayout.LabelField($"Dependencies: {total}   Selected: {sel} ({FormatSize(selSize)})", EditorStyles.boldLabel);

            // Category chips (wrapping)
            var counts = entries.GroupBy(e => e.category).OrderBy(g => g.Key).ToList();
            float avail = position.width - 30, used = 0;
            EditorGUILayout.BeginHorizontal();
            foreach (var g in counts)
            {
                int selInCat = g.Count(e => e.selected && !DependencyRerouter.IsLocked(e, mode));
                var label = new GUIContent($"{g.Key}  {selInCat}/{g.Count()}");
                float w = EditorStyles.miniButton.CalcSize(label).x + 8;
                if (used + w > avail && used > 0)
                {
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    used = 0;
                }
                used += w + 4;
                bool shown = !hiddenCategories.Contains(g.Key);
                bool nowShown = GUILayout.Toggle(shown, label, EditorStyles.miniButton, GUILayout.Width(w));
                if (nowShown != shown)
                {
                    if (nowShown) hiddenCategories.Remove(g.Key); else hiddenCategories.Add(g.Key);
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Show all types", EditorStyles.miniButtonLeft)) hiddenCategories.Clear();
                if (GUILayout.Button("Hide all types", EditorStyles.miniButtonRight))
                    hiddenCategories = Enum.GetValues(typeof(AssetCategory)).Cast<AssetCategory>().ToList();
                GUILayout.Space(10);
                if (GUILayout.Button("Check visible", EditorStyles.miniButtonLeft))
                    foreach (var e in Visible()) e.selected = true;
                if (GUILayout.Button("Uncheck visible", EditorStyles.miniButtonRight))
                    foreach (var e in Visible()) e.selected = false;
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent(sharedScanned ? "Rescan shared usage" : "Scan shared usage",
                        "Scans the whole project to count how many OTHER assets (outside this avatar) use each file. " +
                        "Useful to spot shared textures/shaders before moving."), EditorStyles.miniButton))
                    sharedScanned = DependencyRerouter.ScanSharedUsage(entries, scannedSourcePath);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
                GUILayout.Label("Sort", GUILayout.Width(30));
                sort = (SortMode)EditorGUILayout.EnumPopup(sort, GUILayout.Width(110));
            }
        }

        void DrawList()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("View", GUILayout.Width(34));
                viewMode = (ViewMode)GUILayout.Toolbar((int)viewMode, new[] { "Folders", "Flat list" },
                    EditorStyles.miniButton, GUILayout.Width(150));
                if (viewMode == ViewMode.Folders)
                {
                    GUILayout.Space(10);
                    if (GUILayout.Button("Expand all", EditorStyles.miniButtonLeft, GUILayout.Width(80)) && treeRoot != null)
                    {
                        expanded.Clear();
                        CollectFolderPaths(treeRoot, expanded);
                    }
                    if (GUILayout.Button("Collapse all", EditorStyles.miniButtonRight, GUILayout.Width(80)))
                        expanded.Clear();
                }
                GUILayout.FlexibleSpace();
            }

            if (viewMode == ViewMode.Folders) DrawTree();
            else DrawFlatList();
        }

        // ---------- Folder tree ----------

        FolderNode BuildTree(IEnumerable<DepEntry> list)
        {
            var root = new FolderNode { name = "", path = "" };
            foreach (var e in list)
            {
                var parts = e.path.Split('/');
                var node = root;
                string path = "";
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    path = i == 0 ? parts[0] : path + "/" + parts[i];
                    if (!node.children.TryGetValue(parts[i], out var child))
                    {
                        child = new FolderNode { name = parts[i], path = path };
                        node.children.Add(parts[i], child);
                    }
                    node = child;
                }
                node.files.Add(e);
            }
            Aggregate(root);
            return root;
        }

        void Aggregate(FolderNode n)
        {
            n.total = n.selectable = n.selected = 0;
            n.size = 0;
            foreach (var c in n.children.Values)
            {
                Aggregate(c);
                n.total += c.total; n.selectable += c.selectable; n.selected += c.selected; n.size += c.size;
            }
            foreach (var f in n.files)
            {
                n.total++;
                n.size += f.size;
                if (DependencyRerouter.IsLocked(f, mode)) continue;
                n.selectable++;
                if (f.selected) n.selected++;
            }
        }

        static void CollectFolderPaths(FolderNode n, List<string> into)
        {
            foreach (var c in n.children.Values)
            {
                into.Add(c.path);
                CollectFolderPaths(c, into);
            }
        }

        static void SetFolderSelected(FolderNode n, bool value, TransferMode mode)
        {
            foreach (var f in n.files)
                if (!DependencyRerouter.IsLocked(f, mode)) f.selected = value;
            foreach (var c in n.children.Values) SetFolderSelected(c, value, mode);
        }

        IEnumerable<DepEntry> SortFiles(IEnumerable<DepEntry> files)
        {
            switch (sort)
            {
                case SortMode.Type: return files.OrderBy(e => e.category).ThenBy(e => e.path);
                case SortMode.Size: return files.OrderByDescending(e => e.size);
                case SortMode.SharedUsage: return files.OrderByDescending(e => e.usedByOthers).ThenBy(e => e.path);
                default: return files.OrderBy(e => e.path);
            }
        }

        void Flatten(FolderNode n, int depth, List<Row> into)
        {
            foreach (var child in n.children.Values)
            {
                // Collapse chains of folders that only contain one subfolder: "GoGo/GoLoco/Animations"
                var c = child;
                string label = c.name;
                while (c.files.Count == 0 && c.children.Count == 1)
                {
                    c = c.children.Values.First();
                    label += "/" + c.name;
                }
                into.Add(new Row { depth = depth, folder = c, label = label });
                if (expanded.Contains(c.path)) Flatten(c, depth + 1, into);
            }
            foreach (var f in SortFiles(n.files))
                into.Add(new Row { depth = depth, entry = f, label = Path.GetFileName(f.path) });
        }

        void DrawTree()
        {
            // Rebuild only on Layout so every event in this frame sees the same rows.
            if (rows == null || Event.current.type == EventType.Layout)
            {
                treeRoot = BuildTree(Visible());
                rows = new List<Row>();
                Flatten(treeRoot, 0, rows);
            }

            var hdr = EditorGUILayout.GetControlRect();
            GUI.Label(new Rect(hdr.x + 24, hdr.y, 200, hdr.height), "Name", EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(hdr.xMax - 235, hdr.y, 90, hdr.height), "Type", EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(hdr.xMax - 140, hdr.y, 70, hdr.height), "Size", EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(hdr.xMax - 65, hdr.y, 65, hdr.height), "Shared", EditorStyles.miniBoldLabel);

            float listHeight = Mathf.Clamp(position.height * 0.45f, 160, 600);
            listScroll = EditorGUILayout.BeginScrollView(listScroll, EditorStyles.helpBox, GUILayout.Height(listHeight));

            float rowH = EditorGUIUtility.singleLineHeight + 2;
            if (Event.current.type == EventType.Layout)
            {
                firstRow = Mathf.Max(0, (int)(listScroll.y / rowH) - 2);
                lastRow = Mathf.Min(rows.Count, firstRow + (int)(listHeight / rowH) + 5);
            }
            int first = Mathf.Min(firstRow, rows.Count);
            int last = Mathf.Clamp(lastRow, first, rows.Count);
            GUILayout.Space(first * rowH);

            var folderIcon = EditorGUIUtility.IconContent("Folder Icon").image;
            var greyLabel = new GUIStyle(EditorStyles.label) { normal = { textColor = Color.gray } };
            var greyBold = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Color.gray } };

            for (int i = first; i < last; i++)
            {
                var row = rows[i];
                var r = GUILayoutUtility.GetRect(10, rowH, GUILayout.ExpandWidth(true));
                if (i % 2 == 1) EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.06f));
                float x = r.x + 2 + row.depth * 16;

                if (row.folder != null) DrawFolderRow(r, x, row, folderIcon, greyBold);
                else DrawFileRow(r, x + 14, row, greyLabel);
            }
            GUILayout.Space(Mathf.Max(0, (rows.Count - last) * rowH));
            EditorGUILayout.EndScrollView();

            if (rows.Count == 0) EditorGUILayout.HelpBox("No dependencies match the current filters.", MessageType.None);
        }

        void DrawFolderRow(Rect r, float x, Row row, Texture folderIcon, GUIStyle greyBold)
        {
            var n = row.folder;
            bool isOpen = expanded.Contains(n.path);
            bool nowOpen = EditorGUI.Foldout(new Rect(x, r.y, 14, r.height), isOpen, GUIContent.none, true);

            bool locked = n.selectable == 0;
            bool all = !locked && n.selected == n.selectable;
            bool mixed = n.selected > 0 && n.selected < n.selectable;
            using (new EditorGUI.DisabledScope(locked))
            {
                EditorGUI.showMixedValue = mixed;
                EditorGUI.BeginChangeCheck();
                bool v = EditorGUI.Toggle(new Rect(x + 14, r.y + 1, 18, r.height), all);
                if (EditorGUI.EndChangeCheck())
                {
                    SetFolderSelected(n, mixed || v, mode);
                    planDirty = true;
                }
                EditorGUI.showMixedValue = false;
            }

            if (folderIcon != null) GUI.DrawTexture(new Rect(x + 34, r.y + 1, 16, 16), folderIcon, ScaleMode.ScaleToFit);
            var labelRect = new Rect(x + 52, r.y, r.xMax - 240 - (x + 52), r.height);
            var content = new GUIContent($"{row.label}   ({n.selected}/{n.total})",
                locked ? "Nothing in this folder can be moved/copied." : n.path + "\nClick to expand/collapse");
            if (GUI.Button(labelRect, content, locked ? greyBold : EditorStyles.boldLabel)) nowOpen = !isOpen;

            GUI.Label(new Rect(r.xMax - 140, r.y, 70, r.height), FormatSize(n.size), EditorStyles.miniLabel);

            if (nowOpen != isOpen)
            {
                if (nowOpen) expanded.Add(n.path);
                else expanded.Remove(n.path);
                // Alt-click style: expanding with Shift opens everything below
                if (nowOpen && Event.current.shift) CollectFolderPaths(n, expanded);
            }
        }

        void DrawFileRow(Rect r, float x, Row row, GUIStyle greyLabel)
        {
            var e = row.entry;
            bool locked = DependencyRerouter.IsLocked(e, mode);
            using (new EditorGUI.DisabledScope(locked))
            {
                bool v = EditorGUI.Toggle(new Rect(x, r.y + 1, 18, r.height), !locked && e.selected);
                if (!locked && v != e.selected) { e.selected = v; planDirty = true; }
            }

            var icon = AssetDatabase.GetCachedIcon(e.path);
            if (icon != null) GUI.DrawTexture(new Rect(x + 20, r.y + 1, 16, 16), icon, ScaleMode.ScaleToFit);

            var nameRect = new Rect(x + 38, r.y, r.xMax - 240 - (x + 38), r.height);
            string tip = locked ? DependencyRerouter.LockReason(e, mode) : e.path + "\nClick to ping in Project window";
            if (GUI.Button(nameRect, new GUIContent(row.label, tip), locked ? greyLabel : EditorStyles.label))
                EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(e.path));

            GUI.Label(new Rect(r.xMax - 235, r.y, 90, r.height), e.category.ToString(), EditorStyles.miniLabel);
            GUI.Label(new Rect(r.xMax - 140, r.y, 70, r.height), FormatSize(e.size), EditorStyles.miniLabel);
            DrawShared(new Rect(r.xMax - 65, r.y, 65, r.height), e);
        }

        static void DrawShared(Rect rect, DepEntry e)
        {
            string shared = e.usedByOthers < 0 ? "-" : e.usedByOthers.ToString();
            var style = EditorStyles.miniLabel;
            if (e.usedByOthers > 0)
                style = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = new Color(1f, 0.6f, 0.1f) } };
            GUI.Label(rect, new GUIContent(shared, e.usedByOthers > 0 ? $"Also used by {e.usedByOthers} other asset(s)" : null), style);
        }

        // ---------- Flat list ----------

        void DrawFlatList()
        {
            var visible = Visible().ToList();

            // Header
            var hdr = EditorGUILayout.GetControlRect();
            float x = hdr.x;
            GUI.Label(new Rect(x + 24, hdr.y, 100, hdr.height), "Type", EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(x + 120, hdr.y, 300, hdr.height), "Path", EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(hdr.xMax - 140, hdr.y, 70, hdr.height), "Size", EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(hdr.xMax - 65, hdr.y, 65, hdr.height), "Shared", EditorStyles.miniBoldLabel);

            float listHeight = Mathf.Clamp(position.height * 0.45f, 160, 600);
            listScroll = EditorGUILayout.BeginScrollView(listScroll, EditorStyles.helpBox, GUILayout.Height(listHeight));

            float rowH = EditorGUIUtility.singleLineHeight + 2;
            // simple virtualization: only draw rows in view (range fixed at Layout so events stay consistent)
            if (Event.current.type == EventType.Layout)
            {
                firstRow = Mathf.Max(0, (int)(listScroll.y / rowH) - 2);
                lastRow = Mathf.Min(visible.Count, firstRow + (int)(listHeight / rowH) + 5);
            }
            int first = Mathf.Min(firstRow, visible.Count);
            int last = Mathf.Clamp(lastRow, first, visible.Count);
            GUILayout.Space(first * rowH);

            for (int i = first; i < last; i++)
            {
                var e = visible[i];
                var r = GUILayoutUtility.GetRect(10, rowH, GUILayout.ExpandWidth(true));
                if (i % 2 == 1) EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.06f));

                bool locked = DependencyRerouter.IsLocked(e, mode);
                using (new EditorGUI.DisabledScope(locked))
                {
                    var tr = new Rect(r.x + 2, r.y + 1, 18, r.height);
                    bool v = EditorGUI.Toggle(tr, locked ? false : e.selected);
                    if (!locked) e.selected = v;
                }

                var icon = AssetDatabase.GetCachedIcon(e.path);
                if (icon != null) GUI.DrawTexture(new Rect(r.x + 22, r.y + 1, 16, 16), icon, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(r.x + 40, r.y, 80, r.height), e.category.ToString(), EditorStyles.miniLabel);

                var pathRect = new Rect(r.x + 120, r.y, r.width - 120 - 145, r.height);
                string tip = locked ? DependencyRerouter.LockReason(e, mode) : "Click to ping in Project window";
                var style = locked ? EditorStyles.centeredGreyMiniLabel : EditorStyles.label;
                if (locked) style = new GUIStyle(EditorStyles.label) { normal = { textColor = Color.gray } };
                if (GUI.Button(pathRect, new GUIContent(e.path, tip), style))
                    EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(e.path));

                GUI.Label(new Rect(r.xMax - 140, r.y, 70, r.height), FormatSize(e.size), EditorStyles.miniLabel);
                string shared = e.usedByOthers < 0 ? "-" : e.usedByOthers.ToString();
                var sharedStyle = EditorStyles.miniLabel;
                if (e.usedByOthers > 0) sharedStyle = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = new Color(1f, 0.6f, 0.1f) } };
                GUI.Label(new Rect(r.xMax - 65, r.y, 65, r.height),
                    new GUIContent(shared, e.usedByOthers > 0 ? $"Also used by {e.usedByOthers} other asset(s)" : null), sharedStyle);
            }
            GUILayout.Space(Mathf.Max(0, (visible.Count - last) * rowH));
            EditorGUILayout.EndScrollView();

            if (visible.Count == 0) EditorGUILayout.HelpBox("No dependencies match the current filters.", MessageType.None);
        }

        void DrawDestination()
        {
            EditorGUILayout.LabelField("Destination", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                destination = EditorGUILayout.TextField("Target folder", destination);
                if (GUILayout.Button("Browse...", GUILayout.Width(75)))
                {
                    string abs = EditorUtility.OpenFolderPanel("Choose destination folder", Application.dataPath, "");
                    if (!string.IsNullOrEmpty(abs))
                    {
                        string rel = ToProjectPath(abs);
                        if (rel != null) destination = rel;
                        else EditorUtility.DisplayDialog("Dependency Rerouter", "Destination must be inside this project's Assets folder.", "OK");
                    }
                }
                if (GUILayout.Button(new GUIContent("Selected", "Use the folder selected in the Project window"), GUILayout.Width(70)))
                {
                    string p = AssetDatabase.GetAssetPath(Selection.activeObject);
                    if (AssetDatabase.IsValidFolder(p)) destination = p;
                    else if (!string.IsNullOrEmpty(p)) destination = Path.GetDirectoryName(p).Replace('\\', '/');
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                root = EditorGUILayout.TextField(new GUIContent("Strip prefix",
                    "This part of each original path is removed, the rest is recreated under the target folder.\n" +
                    "'Assets' = mirror the full original structure.\n" +
                    "Common folder = drop the folders every dependency shares."), root);
                if (GUILayout.Button("Assets (full)", EditorStyles.miniButtonLeft, GUILayout.Width(85))) root = "Assets";
                if (GUILayout.Button("Common folder", EditorStyles.miniButtonRight, GUILayout.Width(95)))
                    root = DependencyRerouter.CommonFolder(entries.Where(e => e.selected && !DependencyRerouter.IsLocked(e, mode)).Select(e => e.path));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Mode");
                mode = (TransferMode)GUILayout.Toolbar((int)mode, new[] { "Move originals", "Copy (duplicate & rewire)" });
            }
            conflict = (ConflictMode)EditorGUILayout.EnumPopup(new GUIContent("If file exists", "Skip, or add a number to the new file's name"), conflict);
            skipAlreadyInDest = EditorGUILayout.Toggle("Skip files already in target", skipAlreadyInDest);
            if (mode == TransferMode.Copy && SourceIsSceneObject && !(SourceIsPrefabInstance && usePrefabAsset))
                duplicateSceneObject = EditorGUILayout.Toggle(new GUIContent("Duplicate scene object",
                    "Create a copy of the scene object that uses the copied assets."), duplicateSceneObject);

            if (mode == TransferMode.Move)
                EditorGUILayout.HelpBox("Move relocates the original files. All references (this avatar AND anything else using them) keep working, " +
                                        "but files shared with other avatars will move too. This can't be undone with Ctrl+Z.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Copy duplicates the checked files into the target and rewires the copies to point at each other. " +
                                        "Originals are untouched. Unchecked files stay referenced from their original location.", MessageType.Info);

            if (mode == TransferMode.Copy && EditorSettings.serializationMode != SerializationMode.ForceText)
                EditorGUILayout.HelpBox("Copy mode needs Project Settings > Editor > Asset Serialization = Force Text.", MessageType.Error);
        }

        bool DestinationValid(out string error)
        {
            error = null;
            string d = destination.Replace('\\', '/').TrimEnd('/');
            if (!(d == "Assets" || d.StartsWith("Assets/"))) error = "Target folder must start with 'Assets/'.";
            else if (File.Exists(d)) error = "Target is a file, not a folder.";
            else if (d.IndexOfAny(Path.GetInvalidPathChars()) >= 0) error = "Target folder has invalid characters.";
            return error == null;
        }

        void DrawPreviewAndExecute()
        {
            if (!DestinationValid(out string destErr))
            {
                EditorGUILayout.HelpBox(destErr, MessageType.Error);
                return;
            }

            if (planDirty || plan == null)
            {
                plan = DependencyRerouter.BuildPlan(entries, destination.Replace('\\', '/').TrimEnd('/'),
                    root.Replace('\\', '/'), mode, conflict, skipAlreadyInDest);
                planDirty = false;
            }

            int todo = plan.Count(p => !p.skip);
            int skip = plan.Count - todo;
            showPreview = EditorGUILayout.Foldout(showPreview, $"Preview: {todo} to {mode.ToString().ToLower()}, {skip} skipped", true);
            if (showPreview)
            {
                previewScroll = EditorGUILayout.BeginScrollView(previewScroll, EditorStyles.helpBox, GUILayout.Height(160));
                foreach (var p in plan)
                {
                    var style = p.skip ? new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray } } : EditorStyles.miniLabel;
                    string line = p.skip ? $"{p.entry.path}   [{p.note}]" : $"{p.entry.path}  →  {p.newPath}" + (p.note != null ? $"   [{p.note}]" : "");
                    EditorGUILayout.LabelField(line, style);
                }
                EditorGUILayout.EndScrollView();
            }

            bool blocked = todo == 0 ||
                           (mode == TransferMode.Copy && EditorSettings.serializationMode != SerializationMode.ForceText);
            using (new EditorGUI.DisabledScope(blocked))
            {
                GUI.backgroundColor = mode == TransferMode.Move ? new Color(1f, 0.75f, 0.4f) : new Color(0.5f, 0.85f, 1f);
                if (GUILayout.Button($"{mode} {todo} file(s) to {destination}", GUILayout.Height(30)))
                    RunTransfer(todo);
                GUI.backgroundColor = Color.white;
            }
        }

        void RunTransfer(int count)
        {
            string verb = mode == TransferMode.Move ? "MOVE" : "COPY";
            string msg = $"{verb} {count} file(s) into:\n{destination}\n\n" +
                         (mode == TransferMode.Move
                             ? "Original files will be relocated. This cannot be undone with Ctrl+Z - back up your project first if unsure."
                             : "Originals stay where they are. New copies will be created and rewired.");
            if (!EditorUtility.DisplayDialog("Dependency Rerouter", msg, verb, "Cancel")) return;

            var sceneSource = SourceIsSceneObject && !(SourceIsPrefabInstance && usePrefabAsset) ? source as GameObject : null;
            var res = DependencyRerouter.Execute(plan, mode);

            GameObject clone = null;
            if (mode == TransferMode.Copy && sceneSource != null && duplicateSceneObject && res.guidMap.Count > 0)
                clone = DependencyRerouter.DuplicateAndRewire(sceneSource, res);

            Debug.Log($"[Dependency Rerouter] {verb}: {res.done} done, {res.skipped} skipped, {res.failed} failed.\n" +
                      string.Join("\n", res.log));

            // After a copy, focus the copied source (prefab) or the duplicated scene object.
            if (mode == TransferMode.Copy)
            {
                if (clone != null) { Selection.activeObject = clone; source = clone; }
                else if (scannedSourcePath != null && res.pathMap.TryGetValue(scannedSourcePath, out var newSrc))
                {
                    var o = AssetDatabase.LoadMainAssetAtPath(newSrc);
                    if (o != null) { EditorGUIUtility.PingObject(o); source = o; usePrefabAsset = true; }
                }
            }

            EditorUtility.DisplayDialog("Dependency Rerouter",
                $"{verb} finished.\n\nDone: {res.done}\nSkipped: {res.skipped}\nFailed: {res.failed}\n\nFull log in the Console.", "OK");

            Scan();
            GUIUtility.ExitGUI();
        }

        static string ToProjectPath(string abs)
        {
            abs = abs.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            if (abs == data) return "Assets";
            if (abs.StartsWith(data + "/")) return "Assets" + abs.Substring(data.Length);
            return null;
        }
    }
}
