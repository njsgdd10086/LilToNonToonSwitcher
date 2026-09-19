// LilToNonToon Switcher
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    /// <summary>
    /// 把插件自带的「织物/细节法线」模块登记进 NonToon 的 Shader Core 模块列表。
    ///
    /// 背景：Shader Core 的模块是**按 shader 记录在 ProjectSettings 里**的
    ///（`ProjectSettings/jp.lilxyzw.shadercore.asset`，类 `jp.lilxyzw.shadercore.ProjectSettings` ——
    /// 是 internal，所以这里用反射读写），改完要重新导入 shader 才会把模块编进去。
    ///
    /// 模块文件放在本插件里（`Shaders/Modules/Fabric`），**不动 NonToon 本体**；
    /// 模块自己的 `_Enable` 默认 0，所以只有转换过的材质会用到它，其他材质不受影响。
    /// </summary>
    internal static class FabricModuleInstaller
    {
        public const string ModuleId = "jp.nontoon.switcher.fabric";
        public const string ModuleFolder = "Shaders/Modules/Fabric";
        private const string ModuleIdKey = "NonToonSwitcher.FabricModule.Installed";
        private const string ModuleVersionKey = "NonToonSwitcher.FabricModule.Version";

        /// <summary>
        /// 模块内容的版本。**每次改动 Shader/Modules/Fabric 里的 hlsl 都要 +1** ——
        /// Shader Core 的导入器在 `.scshader` 内容没变时不会重新生成 shader（光 ImportAsset 不够），
        /// 所以靠这个版本戳在转换时补一次强制重新生成，否则用户的模块改动不会生效。
        /// </summary>
        private const string ModuleVersion = "5";

        /// <summary>模块 id 有没有登记进 NonToon 的模块列表。</summary>
        public static bool IsInstalled(out string shaderPath)
        {
            shaderPath = null;
            var shader = FindNonToonShader();
            if (shader == null) return false;
            shaderPath = AssetDatabase.GetAssetPath(shader);
            var modules = GetModuleList(shaderPath);
            return modules != null && modules.Contains(ModuleId);
        }

        /// <summary>确保模块已登记；返回是否成功（失败时给日志）。</summary>
        public static bool EnsureInstalled(ConversionLog log)
        {
            string shaderPath;
            var installed = IsInstalled(out shaderPath);
            if (string.IsNullOrEmpty(shaderPath))
            {
                if (log != null) log.Warn("找不到 NonToon 的 shader，无法登记织物模块（法线细节不会生效）。");
                return false;
            }

            if (!installed && !AddModule(shaderPath, ModuleId))
            {
                if (log != null)
                    log.Warn("无法把织物模块登记进 NonToon 的模块列表（Shader Core 的 ProjectSettings 结构可能变了）。" +
                             "可以手动在 Shader Core 的模块列表里勾上 " + ModuleId + "。");
                return false;
            }

            // 已经登记也可能还没编进 shader（登记之后没重新导入过）——所以这里按"shader 里有没有模块属性"判断，
            // 缺了就强制重新导入一次让 Shader Core 重新生成。
            var stamped = EditorPrefs.GetString(ModuleVersionKey, "");
            if (ModulePropertiesPresent() && stamped == ModuleVersion)
            {
                if (!installed) EditorPrefs.SetBool(ModuleIdKey, true);
                return true;
            }

            EditorPrefs.SetBool(ModuleIdKey, true);
            RegenerateShader(shaderPath);
            EditorPrefs.SetString(ModuleVersionKey, ModuleVersion);
            if (log != null) log.Mapped("织物模块", "已登记并重新生成 NonToon shader（下次转换即可生效）");
            return true;
        }

        /// <summary>
        /// 强制 Shader Core 重新生成 shader。
        /// Shader Core 的导入器在 `.scshader` 内容没变时会跳过重新生成（实测：只 ImportAsset 完全不生效），
        /// 所以这里先临时改动文件内容再导入，然后还原。
        /// </summary>
        public static void RegenerateShader(string shaderPath)
        {
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
                Debug.LogWarning("[LilToNonToon Switcher] 强制重新生成 shader 失败：" + exception.Message);
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
                        // 还原失败不影响功能，忽略
                    }
                }
            }
        }

        /// <summary>NonToon 的 shader 里现在有没有织物模块的属性。</summary>
        public static bool ModulePropertiesPresent()
        {
            var shader = Shader.Find("NonToon");
            return shader != null && FindPropertyName(shader, "FabricNormalMap") != null;
        }

        /// <summary>从 NonToon 的模块列表里移除（卸载用）。</summary>
        public static bool Uninstall()
        {
            string shaderPath;
            if (!IsInstalled(out shaderPath)) return true;
            if (!RemoveModule(shaderPath, ModuleId)) return false;
            EditorPrefs.SetBool(ModuleIdKey, false);
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceUpdate);
            return true;
        }

        // ------------------------------------------------------------------ Shader Core 侧
        private static Shader FindNonToonShader()
        {
            var guids = AssetDatabase.FindAssets("NonToon t:Shader");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != "NonToon") continue;
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader != null && shader.name == "NonToon") return shader;
            }
            return Shader.Find("NonToon");
        }

        private static Type ProjectSettingsType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("jp.lilxyzw.shadercore.ProjectSettings", false);
                if (type != null) return type;
            }
            return null;
        }

        private static object Instance(Type settingsType)
        {
            // `instance` 是 ScriptableSingleton<T> 基类上的静态属性 → 必须带 FlattenHierarchy 才找得到
            var property = settingsType.GetProperty("instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property != null) return property.GetValue(null);

            foreach (var candidate in settingsType.GetProperties(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (candidate.Name.IndexOf("instance", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (candidate.PropertyType != settingsType) continue;
                return candidate.GetValue(null);
            }
            return null;
        }

        /// <summary>读到某个 shader 的模块 id 列表。</summary>
        private static List<string> GetModuleList(string shaderPath)
        {
            var settingsType = ProjectSettingsType();
            if (settingsType == null) return null;
            var type = settingsType;
            var instance = Instance(type);
            if (instance == null) return null;

            var field = type.GetField("shaderSettings", BindingFlags.NonPublic | BindingFlags.Instance);
            var list = field != null ? field.GetValue(instance) as System.Collections.IEnumerable : null;
            if (list == null) return null;

            var shaderName = ShaderNameOf(shaderPath);
            foreach (var entry in list)
            {
                if (entry == null) continue;
                var entryType = entry.GetType();
                var nameField = entryType.GetField("shadername");
                if (nameField == null || (string)nameField.GetValue(entry) != shaderName) continue;
                var modulesField = entryType.GetField("modules");
                return modulesField != null ? modulesField.GetValue(entry) as List<string> : null;
            }
            return null;
        }

        private static bool AddModule(string shaderPath, string moduleId)
        {
            var shaderName = ShaderNameOf(shaderPath);
            var settingsType = ProjectSettingsType();
            if (settingsType == null) return false;
            var instance = Instance(settingsType);
            if (instance == null) return false;

            var field = settingsType.GetField("shaderSettings", BindingFlags.NonPublic | BindingFlags.Instance);
            var list = field != null ? field.GetValue(instance) as System.Collections.IList : null;
            if (list == null) return false;

            foreach (var entry in list)
            {
                if (entry == null) continue;
                var entryType = entry.GetType();
                var nameField = entryType.GetField("shadername");
                if (nameField == null || (string)nameField.GetValue(entry) != shaderName) continue;
                var modulesField = entryType.GetField("modules");
                var modules = modulesField != null ? modulesField.GetValue(entry) as List<string> : null;
                if (modules == null) return false;
                if (!modules.Contains(moduleId)) modules.Add(moduleId);
                Save(settingsType, instance);
                return true;
            }

            // 这个 shader 还没有记录：照着 Shader Core 的做法现场建一条
            var newEntry = Activator.CreateInstance(settingsType.GetNestedType("ShaderSettings", BindingFlags.NonPublic));
            if (newEntry == null) return false;
            newEntry.GetType().GetField("shadername").SetValue(newEntry, shaderName);
            var newModules = new List<string> { moduleId };
            newEntry.GetType().GetField("modules").SetValue(newEntry, newModules);
            list.Add(newEntry);
            Save(settingsType, instance);
            return true;
        }

        private static bool RemoveModule(string shaderPath, string moduleId)
        {
            var modules = GetModuleList(shaderPath);
            if (modules == null || !modules.Remove(moduleId)) return false;
            var settingsType = ProjectSettingsType();
            var instance = settingsType != null ? Instance(settingsType) : null;
            if (instance != null) Save(settingsType, instance);
            return true;
        }

        private static void Save(Type settingsType, object instance)
        {
            var save = settingsType.GetMethod("Save", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (save != null) save.Invoke(instance, null);
            else EditorUtility.SetDirty(instance as UnityEngine.Object);
        }

        private static string ShaderNameOf(string shaderPath)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            return shader != null ? shader.name : "NonToon";
        }

        // ------------------------------------------------------------------ 属性名查找
        /// <summary>
        /// 模块的属性在材质上的实际名字。Shader Core 会给属性加上包前缀（例如
        /// `_jp_nontoon_switcher_fabric_Enable`），前缀规则不是我们能假定的，所以按"名字以 suffix 结尾
        /// 且包含 fabric"来找。
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

        // ------------------------------------------------------------------ 菜单
        [MenuItem("Tools/LilToNonToon Switcher/安装织物法线模块", false, 920)]
        private static void InstallFromMenu()
        {
            string shaderPath;
            if (IsInstalled(out shaderPath))
            {
                EditorUtility.DisplayDialog("织物法线模块", "已经装好了（NonToon 的模块列表里有 " + ModuleId + "）。", "好");
                return;
            }
            var log = new ConversionLog();
            var ok = EnsureInstalled(log);
            EditorUtility.DisplayDialog("织物法线模块",
                ok ? "已装好 ✓\n\nNonToon 的 shader 已重新生成；重新转换材质后织物纹理就会生效。"
                   : "安装失败 ✗\n\n可以手动在 Shader Core 的模块列表里勾上 " + ModuleId + "。", "好");
        }

        [MenuItem("Tools/LilToNonToon Switcher/卸载织物法线模块", false, 921)]
        private static void UninstallFromMenu()
        {
            var ok = Uninstall();
            EditorUtility.DisplayDialog("织物法线模块",
                ok ? "已卸载 ✓（NonToon 的 shader 已重新生成，材质上的织物参数会失效。）" : "卸载失败 ✗", "好");
        }
    }
}
