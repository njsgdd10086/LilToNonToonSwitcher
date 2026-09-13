// LilToNonToon Switcher
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    /// <summary>
    /// Re-packs this tool as a .unitypackage (handy when the sources are kept inside a project).
    /// </summary>
    public static class NonToonPackager
    {
        private const string SourceFolder = "Assets/LilToNonToonSwitcher";
        private const string MenuPath = "Tools/LilToNonToon Switcher/导出为 unitypackage";

        [MenuItem(MenuPath, false, 200)]
        private static void Export()
        {
            if (!AssetDatabase.IsValidFolder(SourceFolder))
            {
                EditorUtility.DisplayDialog("LilToNonToon Switcher",
                    "只有当脚本放在 '" + SourceFolder + "' 下时才能用这个功能。\n" +
                    "如果是用 unitypackage 导入的，则不需要它。", "好");
                return;
            }

            var path = EditorUtility.SaveFilePanel("保存 unitypackage", "", "LilToNonToonSwitcher", "unitypackage");
            if (string.IsNullOrEmpty(path)) return;

            if (File.Exists(path)) File.Delete(path);
            AssetDatabase.ExportPackage(SourceFolder, path,
                ExportPackageOptions.Recurse | ExportPackageOptions.IncludeDependencies);

            if (File.Exists(path))
            {
                Debug.Log("[LilToNonToon] 已导出：" + path);
                EditorUtility.RevealInFinder(path);
            }
            else
            {
                Debug.LogError("[LilToNonToon] 导出失败。");
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ExportValidate()
        {
            return AssetDatabase.IsValidFolder(SourceFolder);
        }
    }
}
