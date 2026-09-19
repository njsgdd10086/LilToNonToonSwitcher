// 织物模块的接入点。
//
// 从 1.1.15 起，织物模块**不再放在本插件里**，而是放在独立的模块包 com.nontoon.modules 里
// （仓库 NonToon Modules，本插件通过 VPM 依赖「订阅」它）。这样：
//   · 模块可以单独安装使用（勾选式菜单 Tools/NonToon 模块）；
//   · 本插件里不再包含任何 Shader Core 模块文件；
//   · 插件与模块解耦 —— 这里用**反射**调用模块包的 API，所以模块包万一没装，本插件仍能编译，
//     只是转换时会给一条明确的提示。

using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    internal static class FabricModuleInstaller
    {
        /// <summary>模块在 .scmodule 里声明的 uniqueID（模块包里的 Fabric 模块）。</summary>
        public const string ModuleId = "jp.nontoon.switcher.fabric";

        /// <summary>模块包里的公共 API 类。</summary>
        private const string RegistryTypeName = "NonToonModules.NonToonModuleRegistry";

        /// <summary>模块包在工程里的包名（用于提示）。</summary>
        public const string ModulesPackageName = "com.nontoon.modules";

        private static Type RegistryType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(RegistryTypeName, false);
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>模块包装了没有。</summary>
        public static bool ModulesPackagePresent() => RegistryType() != null;

        /// <summary>模块有没有被勾选（登记进 NonToon 的模块列表）。</summary>
        public static bool IsInstalled(out string shaderPath)
        {
            shaderPath = null;
            var type = RegistryType();
            if (type == null) return false;
            var pathMethod = type.GetMethod("FindNonToonShaderPath", BindingFlags.Public | BindingFlags.Static);
            if (pathMethod != null) shaderPath = pathMethod.Invoke(null, null) as string;
            var isEnabled = type.GetMethod("IsEnabled", BindingFlags.Public | BindingFlags.Static);
            return isEnabled != null && (bool)isEnabled.Invoke(null, new object[] { ModuleId });
        }

        /// <summary>确保模块被勾选（转换时调用；幂等）。</summary>
        public static bool EnsureInstalled(ConversionLog log)
        {
            var type = RegistryType();
            if (type == null)
            {
                if (log != null)
                    log.Warn("没有安装模块包 " + ModulesPackageName + "，织物/法线细节模块不可用（布料会偏塑料感）。\n" +
                             "在 VCC/ALCOM 里更新本插件即可自动装好，或手动安装 NonToon Modules。");
                return false;
            }

            var ensure = type.GetMethod("EnsureEnabled", BindingFlags.Public | BindingFlags.Static);
            if (ensure == null) return false;
            try
            {
                var result = (bool)ensure.Invoke(null, new object[] { ModuleId, "织物/法线细节模块" });
                if (result && log != null) log.Mapped("织物模块", "已在 NonToon 模块列表里勾选（" + ModuleId + "）");
                else if (!result && log != null) log.Warn("勾选织物模块失败，可以打开 Tools/NonToon 模块/模块管理… 手动勾选。");
                return result;
            }
            catch (Exception exception)
            {
                var inner = exception.InnerException ?? exception;
                if (log != null) log.Warn("勾选织物模块时出错：" + inner.Message);
                return false;
            }
        }

        /// <summary>
        /// 模块的属性在材质上的实际名字。Shader Core 会给属性加上包名前缀
        /// （例如 `_jp_nontoon_switcher_fabric_Enable`），前缀规则不假定，按"以 suffix 结尾且包含 fabric"找。
        /// </summary>
        public static string FindPropertyName(Shader shader, string suffix)
        {
            if (shader == null) return null;
            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (name.IndexOf("fabric", StringComparison.OrdinalIgnoreCase) < 0) continue;
                return name;
            }
            return null;
        }

        /// <summary>重新生成 NonToon 的 shader（优先走模块包的实现）。</summary>
        public static void RegenerateShader(string shaderPath)
        {
            var type = RegistryType();
            var regenerate = type?.GetMethod("RegenerateShader", BindingFlags.Public | BindingFlags.Static);
            if (regenerate != null)
            {
                regenerate.Invoke(null, new object[] { shaderPath });
                return;
            }

            // 模块包没装时的兜底实现
            var full = System.IO.Path.GetFullPath(shaderPath);
            string original = null;
            try
            {
                original = System.IO.File.ReadAllText(full);
                System.IO.File.WriteAllText(full, original + "\n");
                AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[LilToNonToon Switcher] 重新生成 shader 失败：" + exception.Message);
            }
            finally
            {
                if (original != null)
                {
                    try
                    {
                        System.IO.File.WriteAllText(full, original);
                        AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceUpdate);
                        AssetDatabase.Refresh();
                    }
                    catch (Exception)
                    {
                        // 忽略
                    }
                }
            }
        }

        // ------------------------------------------------------------------ 菜单
        [MenuItem("Tools/LilToNonToon Switcher/打开 NonToon 模块管理", false, 920)]
        private static void OpenModuleManager()
        {
            var type = RegistryType();
            if (type == null)
            {
                EditorUtility.DisplayDialog("NonToon 模块",
                    "还没有安装模块包 " + ModulesPackageName + "。\n\n" +
                    "在 VCC/ALCOM 里更新本插件会自动装好；也可以单独安装 NonToon Modules，" +
                    "然后在 Tools/NonToon 模块 里勾选需要的模块。", "好");
                return;
            }
            EditorApplication.ExecuteMenuItem("Tools/NonToon 模块/模块管理…");
        }
    }
}
