// LilToNonToon Switcher
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    /// <summary>
    /// Settings + conversion window. The right click menu uses exactly the settings edited here.
    /// </summary>
    public class NonToonSwitcherWindow : EditorWindow
    {
        internal static ConversionResult LastResult;

        private Vector2 _scroll;
        private Vector2 _logScroll;
        private bool _showLog = true;
        private bool _showAdvanced;

        [MenuItem("Tools/LilToNonToon Switcher/设置与转换窗口", false, 101)]
        public static void Open()
        {
            var window = GetWindow<NonToonSwitcherWindow>(false, "LilToNonToon", true);
            window.minSize = new Vector2(460f, 460f);
            window.Show();
        }

        private static NonToonSwitcherSettings Settings { get { return NonToonSwitcherSettings.instance; } }

        private void OnGUI()
        {
            var settings = Settings;
            EditorGUIUtility.labelWidth = 210f;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("LilToNonToon Switcher", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "在 Hierarchy 里选中对象 → 右键 → LilToNonToon → 「将选中对象转换为 NonToon」。\n\n" +
                "· 把选中对象里的 lilToon 材质转成 NonToon，保存到指定文件夹\n" +
                "· 自动创建 Modular Avatar 的 Material Swap + 菜单开关（一键切换 shader）",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("设置", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            var folder = EditorGUILayout.TextField("输出文件夹", settings.OutputFolder);
            if (folder != settings.OutputFolder) settings.OutputFolder = folder;
            if (GUILayout.Button("选择", GUILayout.Width(56f)))
            {
                var start = AssetDatabase.IsValidFolder(settings.OutputFolder) ? settings.OutputFolder : "Assets";
                var chosen = EditorUtility.OpenFolderPanel("选择输出文件夹", start, string.Empty);
                if (!string.IsNullOrEmpty(chosen)) settings.OutputFolder = ShaderUtility.ToAssetPath(chosen);
            }
            if (GUILayout.Button("打开", GUILayout.Width(56f)))
            {
                NonToonTextureBaker.EnsureFolder(settings.OutputFolder);
                var asset = AssetDatabase.LoadAssetAtPath<Object>(settings.OutputFolder);
                if (asset != null) EditorGUIUtility.PingObject(asset);
                EditorUtility.RevealInFinder(settings.OutputFolder);
            }
            EditorGUILayout.EndHorizontal();

            settings.SetNonToonOnByDefault = EditorGUILayout.Toggle(
                new GUIContent("将 NonToon 设为默认",
                    "开：转换后直接显示 NonToon，勾选开关变回 lilToon（相当于还原开关）。\n" +
                    "关：保持 lilToon 显示，勾选开关才变 NonToon。"),
                settings.SetNonToonOnByDefault);

            settings.CreateMenuToggle = EditorGUILayout.Toggle(
                new GUIContent("创建菜单开关", "创建 Modular Avatar 的菜单项（Expression Menu）。"),
                settings.CreateMenuToggle);

            using (new EditorGUI.DisabledScope(!settings.CreateMenuToggle))
            {
                settings.MenuLabel = EditorGUILayout.TextField("菜单显示名", settings.MenuLabel);
                settings.MenuParameterName = EditorGUILayout.TextField("参数名", settings.MenuParameterName);
            }

            var modeLabels = new[] { "MA Material Setter（对象+材质槽，推荐）", "MA Material Swap（按材质）" };
            var modeIndex = settings.SwitcherMode == SwitcherMode.MaterialSetter ? 0 : 1;
            var newModeIndex = EditorGUILayout.Popup(
                new GUIContent("切换组件类型",
                    "Material Setter：每条记录「对象 + 材质槽 + 材质」，与多数 lilToon→NonToon 工具的输出一致。\n" +
                    "Material Swap：只按「原材质 → 新材质」成对替换。"),
                modeIndex, modeLabels);
            if (newModeIndex != modeIndex)
                settings.SwitcherMode = newModeIndex == 0 ? SwitcherMode.MaterialSetter : SwitcherMode.MaterialSwap;

            if (settings.SwitcherMode == SwitcherMode.MaterialSetter)
            {
                EditorGUILayout.HelpBox(
                    "Material Setter 模式下，选中对象的 Renderer 会保留原来的 lilToon 材质，" +
                    "菜单开关负责把它切成 NonToon（开关默认就是开的）。\n" +
                    "这样开关才有意义：否则槽里已经是 NonToon，开关等于没有效果。",
                    MessageType.Info);
            }

            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "高级设置");
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                settings.BakeBaseTexture = EditorGUILayout.Toggle(
                    new GUIContent("烘焙基础贴图",
                        "把 _MainTex × _Color 输出成新的 PNG（NonToon 0.1.x 没有主颜色属性）。"),
                    settings.BakeBaseTexture);
                settings.BakeSharedMask = EditorGUILayout.Toggle(
                    new GUIContent("生成共享遮罩",
                        "把 lilToon 的各个遮罩（α / 逆光 / 边缘 / 高光 / MatCap）重新打包进 NonToon 的 _SharedMask。"),
                    settings.BakeSharedMask);
                settings.BakeGradients = EditorGUILayout.Toggle(
                    new GUIContent("生成阴影渐变",
                        "由 lilToon 的阴影色、边缘阴影色生成 Shader Core 的 Gradient（.scgradients）。"),
                    settings.BakeGradients);
                settings.LogToConsole = EditorGUILayout.Toggle("输出日志到 Console", settings.LogToConsole);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            DrawEnvironment();

            EditorGUILayout.Space();
            DrawSelection();

            EditorGUILayout.Space();
            var hasSelection = Selection.gameObjects != null && Selection.gameObjects.Length > 0;

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!hasSelection))
            {
                if (GUILayout.Button("转换选中对象", GUILayout.Height(30f)))
                {
                    NonToonConverterMenu.Run(NonToonSwitcherMenu.BuildRequestFromSettings(settings, true));
                    GUI.FocusControl(null);
                }
            }
            using (new EditorGUI.DisabledScope(!hasSelection))
            {
                if (GUILayout.Button("只转换材质", GUILayout.Height(30f), GUILayout.Width(140f)))
                {
                    var request = NonToonSwitcherMenu.BuildRequestFromSettings(settings, false);
                    request.CreateMenuToggle = false;
                    NonToonConverterMenu.Run(request);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!hasSelection)
                EditorGUILayout.HelpBox("还没有选中任何对象。", MessageType.Warning);

            if (LastResult != null)
            {
                EditorGUILayout.Space();
                _showLog = EditorGUILayout.Foldout(_showLog, "转换结果", true);
                if (_showLog)
                {
                    _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(120f), GUILayout.MaxHeight(300f));
                    EditorGUILayout.TextArea(LastResult.BuildText(), GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                    if (GUILayout.Button("清空日志")) LastResult = null;
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private static void DrawEnvironment()
        {
            var lilToon = NonToonEnvironmentCheck.LilToonInstalled;
            var nonToon = NonToonEnvironmentCheck.NonToonInstalled;
            var modularAvatar = NonToonEnvironmentCheck.ModularAvatarInstalled;

            if (lilToon && nonToon && modularAvatar)
            {
                EditorGUILayout.HelpBox("已检测到 lilToon / NonToon / Modular Avatar。", MessageType.Info);
                return;
            }

            var message = string.Empty;
            if (!lilToon) message += "未找到 lilToon (jp.lilxyzw.liltoon)\n";
            if (!nonToon) message += "未找到 NonToon (jp.lilxyzw.nontoon + jp.lilxyzw.shadercore)\n";
            if (!modularAvatar) message += "未找到 Modular Avatar (nadena.dev.modular-avatar)：将不会创建切换开关\n";
            EditorGUILayout.HelpBox(message.TrimEnd(), MessageType.Warning);
        }

        private void DrawSelection()
        {
            var targets = Selection.gameObjects;
            if (targets == null || targets.Length == 0)
            {
                EditorGUILayout.LabelField("选中对象：（无）");
                return;
            }

            EditorGUILayout.LabelField("选中对象", EditorStyles.boldLabel);
            var renderers = new List<Renderer>();
            foreach (var target in targets) NonToonConverter.CollectRenderers(target, true, renderers);

            var lilToonCount = 0;
            var otherCount = 0;
            var seen = new HashSet<Material>();
            foreach (var renderer in renderers)
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || !seen.Add(material)) continue;
                    if (ShaderUtility.IsLilToon(material.shader)) lilToonCount++;
                    else otherCount++;
                }
            }

            EditorGUI.indentLevel++;
            foreach (var target in targets)
                EditorGUILayout.LabelField(target != null ? target.name : "(已丢失)", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;

            EditorGUILayout.LabelField("可转换的 lilToon 材质", lilToonCount.ToString());
            EditorGUILayout.LabelField("非 lilToon 材质（会跳过）", otherCount.ToString());
        }
    }
}
