// LilToNonToon Switcher
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    public static class NonToonSwitcherMenu
    {
        private const int Priority = 50;

        [MenuItem("GameObject/LilToNonToon/将选中对象转换为 NonToon %#t", false, Priority)]
        private static void ConvertSelection()
        {
            var settings = NonToonSwitcherSettings.instance;
            NonToonConverterMenu.Run(BuildRequestFromSettings(settings, true));
        }

        [MenuItem("GameObject/LilToNonToon/转换并打开设置窗口", false, Priority + 1)]
        private static void ConvertSelectionWithWindow()
        {
            NonToonSwitcherWindow.Open();
        }

        [MenuItem("GameObject/LilToNonToon/只转换材质（不建开关）", false, Priority + 2)]
        private static void ConvertSelectionWithoutSwitcher()
        {
            var settings = NonToonSwitcherSettings.instance;
            var request = BuildRequestFromSettings(settings, true);
            request.CreateSwitcher = false;
            request.CreateMenuToggle = false;
            NonToonConverterMenu.Run(request);
        }

        [MenuItem("GameObject/LilToNonToon/查看转换日志", false, Priority + 3)]
        private static void OpenLastLog()
        {
            NonToonSwitcherWindow.Open();
        }

        [MenuItem("Tools/LilToNonToon Switcher/转换选中对象为 NonToon", false, 100)]
        private static void ConvertFromToolsMenu()
        {
            var settings = NonToonSwitcherSettings.instance;
            NonToonConverterMenu.Run(BuildRequestFromSettings(settings, true));
        }

        [MenuItem("Tools/LilToNonToon Switcher/设置与转换窗口", false, 101)]
        private static void OpenWindow()
        {
            NonToonSwitcherWindow.Open();
        }

        [MenuItem("Tools/LilToNonToon Switcher/环境检查", false, 120)]
        private static void CheckSetup()
        {
            NonToonEnvironmentCheck.LogReport();
        }

        // ------------------------------------------------------------------ 复用开关的选项
        //
        // Unity 没有复选菜单项，所以按惯例做成两项互斥 + 打勾标记。

        private const string ReuseOnPath = "Tools/LilToNonToon Switcher/转换时复用已有的 _NonToonSwitch";
        private const string ReuseOffPath = "Tools/LilToNonToon Switcher/转换时总是新建 _NonToonSwitch";

        [MenuItem(ReuseOnPath, false, 140)]
        private static void EnableReuse()
        {
            var settings = NonToonSwitcherSettings.instance;
            settings.ReuseExistingSwitcher = true;
            Menu.SetChecked(ReuseOnPath, true);
            Menu.SetChecked(ReuseOffPath, false);
            Debug.Log("[LilToNonToon] 转换时会复用同一个 avatar 下已有的 " + NonToonConverter.SwitcherObjectName +
                      "，新材质会追加进去。");
        }

        [MenuItem(ReuseOnPath, true)]
        private static bool EnableReuseValidate()
        {
            Menu.SetChecked(ReuseOnPath, NonToonSwitcherSettings.instance.ReuseExistingSwitcher);
            return true;
        }

        [MenuItem(ReuseOffPath, false, 141)]
        private static void DisableReuse()
        {
            var settings = NonToonSwitcherSettings.instance;
            settings.ReuseExistingSwitcher = false;
            Menu.SetChecked(ReuseOnPath, false);
            Menu.SetChecked(ReuseOffPath, true);
            Debug.Log("[LilToNonToon] 转换时总是新建 " + NonToonConverter.SwitcherObjectName +
                      "（每次转换都会产生一个新的开关）。");
        }

        [MenuItem(ReuseOffPath, true)]
        private static bool DisableReuseValidate()
        {
            Menu.SetChecked(ReuseOffPath, !NonToonSwitcherSettings.instance.ReuseExistingSwitcher);
            return true;
        }

        public static ConvertRequest BuildRequestFromSettings(NonToonSwitcherSettings settings, bool createSwitcher)
        {
            return new ConvertRequest
            {
                Targets = Selection.gameObjects,
                OutputFolder = settings.OutputFolder,
                ApplyToSelectionImmediately = true,
                CreateSwitcher = createSwitcher,
                CreateMenuToggle = settings.CreateMenuToggle,
                NonToonOnByDefault = settings.SetNonToonOnByDefault,
                BakeSharedMask = settings.BakeSharedMask,
                BakeBaseTexture = settings.BakeBaseTexture,
                BakeGradients = settings.BakeGradients,
                SwitcherMode = settings.SwitcherMode,
                ReuseExistingSwitcher = settings.ReuseExistingSwitcher,
                MenuParameter = settings.MenuParameterName,
                MenuLabel = settings.MenuLabel,
            };
        }
    }

    public static class NonToonConverterMenu
    {
        public static void Run(ConvertRequest request)
        {
            if (request == null) return;
            if (request.Targets == null || request.Targets.Length == 0)
            {
                EditorUtility.DisplayDialog("LilToNonToon Switcher",
                    "请先在 Hierarchy 里选中要转换的对象。", "好");
                return;
            }

            var result = NonToonConverter.Convert(request);
            NonToonSwitcherWindow.LastResult = result;

            if (!result.Success)
            {
                EditorUtility.DisplayDialog("LilToNonToon Switcher — 转换失败", result.BuildText(), "好");
                NonToonSwitcherWindow.Open();
                return;
            }

            if (result.Warnings.Count > 0)
                Debug.LogWarning("[LilToNonToon] 转换完成，有 " + result.Warnings.Count + " 条警告，详情见窗口。");
            else
                Debug.Log("[LilToNonToon] 已转换 " + result.Logs.Count + " 个材质到 " + result.OutputFolder);
        }
    }

    /// <summary>Reports whether the shaders and Modular Avatar that this tool needs are present.</summary>
    public static class NonToonEnvironmentCheck
    {
        public static bool LilToonInstalled { get { return ShaderUtility.IsLilToonAvailable; } }
        public static bool NonToonInstalled { get { return ShaderUtility.FindNonToonShader() != null; } }
        public static bool ModularAvatarInstalled { get { return NonToonSwitcherBuilder.IsModularAvatarInstalled; } }

        public static string Report()
        {
            var text = "lilToon        : " + (LilToonInstalled ? "已安装" : "未找到 (jp.lilxyzw.liltoon)") + "\n" +
                       "NonToon        : " + (NonToonInstalled ? "已安装" : "未找到 (jp.lilxyzw.nontoon + jp.lilxyzw.shadercore)") + "\n" +
                       "Modular Avatar : " + (ModularAvatarInstalled ? "已安装" : "未找到 (nadena.dev.modular-avatar)") + "\n" +
                       "输出文件夹     : " + NonToonSwitcherSettings.instance.OutputFolder;
            return text;
        }

        public static void LogReport()
        {
            Debug.Log("[LilToNonToon] 环境检查\n" + Report());
        }
    }
}
