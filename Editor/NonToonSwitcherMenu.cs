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

        // ------------------------------------------------------------------ 转换方式（三选一）
        //
        // 1. 保留 lilToon 材质 + 建切换开关（默认）
        // 2. 原地直接替换成 NonToon（不回退）
        // 3. 复制一份 <名字>_nontoon，换在副本上，原对象取消勾选（可回退）

        private const string ModeSwitchPath = "Tools/LilToNonToon Switcher/转换方式/保留原材质 + 建切换开关（默认）";
        private const string ModeReplacePath = "Tools/LilToNonToon Switcher/转换方式/直接替换成 NonToon（原地替换）";
        private const string ModeDuplicatePath = "Tools/LilToNonToon Switcher/转换方式/复制一份 _nontoon 后替换（原对象取消勾选）";

        [MenuItem(ModeSwitchPath, false, 150)]
        private static void UseSwitchMode() { SetReplaceMode(ReplaceMode.Switch); }

        [MenuItem(ModeReplacePath, false, 151)]
        private static void UseReplaceMode() { SetReplaceMode(ReplaceMode.ReplaceInPlace); }

        [MenuItem(ModeDuplicatePath, false, 152)]
        private static void UseDuplicateMode() { SetReplaceMode(ReplaceMode.DuplicateThenReplace); }

        private static void SetReplaceMode(ReplaceMode mode)
        {
            NonToonSwitcherSettings.instance.ReplaceMode = mode;
            MarkReplaceMode();
            switch (mode)
            {
                case ReplaceMode.ReplaceInPlace:
                    Debug.Log("[LilToNonToon] 转换后会把选中对象上的材质原地换成 NonToon，不建切换开关。" +
                              "（想回退只能靠 Ctrl+Z 或版本管理；建议改用「复制一份 _nontoon 后替换」）");
                    break;
                case ReplaceMode.DuplicateThenReplace:
                    Debug.Log("[LilToNonToon] 转换后会复制一份 " + NonToonConverter.DuplicateSuffix +
                              "，材质换在副本上，原对象自动取消勾选（想回退就把它勾回来）—— 推荐。");
                    break;
                default:
                    Debug.Log("[LilToNonToon] 转换后保留原来的 lilToon 材质，并新建 " + NonToonConverter.SwitcherObjectName +
                              " 切换开关（默认）。");
                    break;
            }
        }

        [MenuItem(ModeSwitchPath, true, 150)]
        private static bool UseSwitchModeValidate() { return MarkReplaceMode(); }

        [MenuItem(ModeReplacePath, true, 151)]
        private static bool UseReplaceModeValidate() { return MarkReplaceMode(); }

        [MenuItem(ModeDuplicatePath, true, 152)]
        private static bool MarkReplaceModeValidate() { return MarkReplaceMode(); }

        private static bool MarkReplaceMode()
        {
            var mode = NonToonSwitcherSettings.instance.ReplaceMode;
            Menu.SetChecked(ModeSwitchPath, mode == ReplaceMode.Switch);
            Menu.SetChecked(ModeReplacePath, mode == ReplaceMode.ReplaceInPlace);
            Menu.SetChecked(ModeDuplicatePath, mode == ReplaceMode.DuplicateThenReplace);
            return true;
        }

        // ------------------------------------------------------------------ 描边后移倍数
        //
        // lilToon 的描边宽度贴图 NonToon 没有对应功能，转换时会把描边整体往后推
        // 「描边宽度 × 0.01 × 倍数」。默认 1；觉得描边被推得太淡就调小，0 = 完全不推。

        private const string OutlineFactorRoot = "Tools/LilToNonToon Switcher/描边后移倍数/";
        private static readonly float[] OutlineFactors = { 0f, 0.5f, 1f, 1.5f, 2f };
        private static readonly string[] OutlineFactorNames = { "0（不处理）", "0.5", "1（默认）", "1.5", "2" };

        [MenuItem(OutlineFactorRoot + "0（不处理）", false, 160)]
        private static void OutlineFactor0() { SetOutlineFactor(0f); }

        [MenuItem(OutlineFactorRoot + "0.5", false, 161)]
        private static void OutlineFactor05() { SetOutlineFactor(0.5f); }

        [MenuItem(OutlineFactorRoot + "1（默认）", false, 162)]
        private static void OutlineFactor1() { SetOutlineFactor(1f); }

        [MenuItem(OutlineFactorRoot + "1.5", false, 163)]
        private static void OutlineFactor15() { SetOutlineFactor(1.5f); }

        [MenuItem(OutlineFactorRoot + "2", false, 164)]
        private static void OutlineFactor2() { SetOutlineFactor(2f); }

        private static void SetOutlineFactor(float value)
        {
            NonToonSwitcherSettings.instance.OutlineZOffsetFactor = value;
            Debug.Log("[LilToNonToon] 描边后移倍数 = " + value.ToString("0.##") +
                      "（描边整体后移 = 描边宽度 × 0.01 × 倍数，只对用了描边宽度贴图的材质生效）");
        }

        [MenuItem(OutlineFactorRoot + "0（不处理）", true, 160)]
        private static bool OutlineFactor0Validate() { return MarkOutlineFactor(); }

        [MenuItem(OutlineFactorRoot + "0.5", true, 161)]
        private static bool OutlineFactor05Validate() { return MarkOutlineFactor(); }

        [MenuItem(OutlineFactorRoot + "1（默认）", true, 162)]
        private static bool OutlineFactor1Validate() { return MarkOutlineFactor(); }

        [MenuItem(OutlineFactorRoot + "1.5", true, 163)]
        private static bool OutlineFactor15Validate() { return MarkOutlineFactor(); }

        [MenuItem(OutlineFactorRoot + "2", true, 164)]
        private static bool OutlineFactor2Validate() { return MarkOutlineFactor(); }

        private static bool MarkOutlineFactor()
        {
            var current = NonToonSwitcherSettings.instance.OutlineZOffsetFactor;
            for (var i = 0; i < OutlineFactors.Length; i++)
            {
                Menu.SetChecked(OutlineFactorRoot + OutlineFactorNames[i],
                    Mathf.Abs(current - OutlineFactors[i]) < 0.001f);
            }
            return true;
        }

        // ------------------------------------------------------------------ 描边宽度倍数
        //
        // 自动部分：NonToon 的描边偏移在世界空间、lilToon 在物体空间，转换时会按
        // 「使用该材质的对象世界缩放」折算。这里是在那之上再乘一次，用来整体调描边粗细。

        private const string OutlineWidthRoot = "Tools/LilToNonToon Switcher/描边宽度倍数/";
        private static readonly float[] OutlineWidths = { 0.25f, 0.5f, 0.75f, 1f, 1.5f };
        private static readonly string[] OutlineWidthNames = { "0.25", "0.5", "0.75", "1（默认）", "1.5" };

        [MenuItem(OutlineWidthRoot + "0.25", false, 170)]
        private static void OutlineWidth025() { SetOutlineWidth(0.25f); }

        [MenuItem(OutlineWidthRoot + "0.5", false, 171)]
        private static void OutlineWidth05() { SetOutlineWidth(0.5f); }

        [MenuItem(OutlineWidthRoot + "0.75", false, 172)]
        private static void OutlineWidth075() { SetOutlineWidth(0.75f); }

        [MenuItem(OutlineWidthRoot + "1（默认）", false, 173)]
        private static void OutlineWidth1() { SetOutlineWidth(1f); }

        [MenuItem(OutlineWidthRoot + "1.5", false, 174)]
        private static void OutlineWidth15() { SetOutlineWidth(1.5f); }

        private static void SetOutlineWidth(float value)
        {
            NonToonSwitcherSettings.instance.OutlineWidthFactor = value;
            Debug.Log("[LilToNonToon] 描边宽度倍数 = " + value.ToString("0.##") +
                      "（转换时描边宽度 = 原值 × 对象世界缩放 × 这个倍数）。重新转换后生效。");
        }

        [MenuItem(OutlineWidthRoot + "0.25", true, 170)]
        private static bool OutlineWidth025Validate() { return MarkOutlineWidth(); }

        [MenuItem(OutlineWidthRoot + "0.5", true, 171)]
        private static bool OutlineWidth05Validate() { return MarkOutlineWidth(); }

        [MenuItem(OutlineWidthRoot + "0.75", true, 172)]
        private static bool OutlineWidth075Validate() { return MarkOutlineWidth(); }

        [MenuItem(OutlineWidthRoot + "1（默认）", true, 173)]
        private static bool OutlineWidth1Validate() { return MarkOutlineWidth(); }

        [MenuItem(OutlineWidthRoot + "1.5", true, 174)]
        private static bool OutlineWidth15Validate() { return MarkOutlineWidth(); }

        private static bool MarkOutlineWidth()
        {
            var current = NonToonSwitcherSettings.instance.OutlineWidthFactor;
            for (var i = 0; i < OutlineWidths.Length; i++)
            {
                Menu.SetChecked(OutlineWidthRoot + OutlineWidthNames[i],
                    Mathf.Abs(current - OutlineWidths[i]) < 0.001f);
            }
            return true;
        }

        public static ConvertRequest BuildRequestFromSettings(NonToonSwitcherSettings settings, bool createSwitcher)
        {
            var mode = settings.ReplaceMode;
            var wantsSwitcher = createSwitcher && mode == ReplaceMode.Switch;
            return new ConvertRequest
            {
                Targets = Selection.gameObjects,
                OutputFolder = settings.OutputFolder,
                ApplyToSelectionImmediately = true,
                CreateSwitcher = wantsSwitcher,
                CreateMenuToggle = settings.CreateMenuToggle && wantsSwitcher,
                NonToonOnByDefault = settings.SetNonToonOnByDefault,
                BakeSharedMask = settings.BakeSharedMask,
                BakeBaseTexture = settings.BakeBaseTexture,
                BakeGradients = settings.BakeGradients,
                SwitcherMode = settings.SwitcherMode,
                ReuseExistingSwitcher = settings.ReuseExistingSwitcher,
                MenuParameter = settings.MenuParameterName,
                MenuLabel = settings.MenuLabel,
                ReplaceMode = mode,
                ReplaceMaterialsOnRenderers = mode == ReplaceMode.ReplaceInPlace,
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
