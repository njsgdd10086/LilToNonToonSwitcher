// LilToNonToon Switcher
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace NonToonSwitcher
{
    /// <summary>
    /// 版本号与更新检查：读自己的 package.json 版本，去 VPM 索引里比对最新版本。
    /// 菜单里加「关于与更新检查…」，另外在编辑器启动后每天自动检查一次（失败静默）。
    /// </summary>
    internal static class UpdateChecker
    {
        public const string PackageId = "com.nontoon.switcher";
        public const string DisplayName = "LilToNonToon Switcher";
        public const string IndexUrl = "https://njsgdd10086.github.io/vpm-listing/index.json";
        public const string ReleasesUrl = "https://github.com/njsgdd10086/LilToNonToonSwitcher/releases";

        private const string LastCheckKey = "NonToonSwitcher.UpdateCheck.LastUtcTicks";
        private const string LatestKey = "NonToonSwitcher.UpdateCheck.LatestVersion";
        private const double CheckIntervalHours = 24.0;

        private static UnityWebRequest _request;
        private static bool _interactive;

        /// <summary>安装的版本（读包里的 package.json）。</summary>
        public static string InstalledVersion
        {
            get
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UpdateChecker).Assembly);
                if (package != null && !string.IsNullOrEmpty(package.version)) return package.version;

                // 回退：直接读 package.json（例如被直接拷进 Assets 的情况）
                var assetPath = FindPackageJson();
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var match = Regex.Match(File.ReadAllText(assetPath), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (match.Success) return match.Groups[1].Value;
                }
                return "未知";
            }
        }

        private static string FindPackageJson()
        {
            foreach (var folder in new[] { "Packages/" + PackageId + "/package.json", "Assets/" + PackageId + "/package.json" })
                if (File.Exists(folder)) return folder;

            var guids = AssetDatabase.FindAssets("NonToonSwitcherVersion t:TextAsset");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase)) return path;
            }
            return null;
        }

        /// <summary>上次检查到的最新版本（没检查过就是空）。</summary>
        public static string KnownLatestVersion
        {
            get { return EditorPrefs.GetString(LatestKey, string.Empty); }
        }

        public static bool IsChecking
        {
            get { return _request != null; }
        }

        [MenuItem("Tools/LilToNonToon Switcher/关于与更新检查…", false, 900)]
        private static void ShowAbout()
        {
            var installed = InstalledVersion;
            var latest = KnownLatestVersion;
            var status = string.IsNullOrEmpty(latest)
                ? "还没检查过更新。"
                : Compare(latest, installed) > 0
                    ? "发现新版本：v" + latest + "（当前 v" + installed + "）"
                    : "已是最新版本。";

            var open = EditorUtility.DisplayDialogComplex(DisplayName + "  v" + installed,
                "当前版本：v" + installed + "\n最新版本：" + (string.IsNullOrEmpty(latest) ? "（未检查）" : "v" + latest) + "\n\n" + status +
                "\n\n更新方式：在 VCC / ALCOM 里更新（本插件走 VPM 索引），或从发布页下载 unitypackage。",
                "检查更新", "打开发布页", "关闭");
            if (open == 0) Check(true);
            else if (open == 1) Application.OpenURL(ReleasesUrl);
        }

        [MenuItem("Tools/LilToNonToon Switcher/检查更新", false, 901)]
        private static void CheckFromMenu()
        {
            Check(true);
        }

        /// <summary>发起一次检查。<paramref name="interactive"/> 为真时无论结果如何都弹窗。</summary>
        public static void Check(bool interactive)
        {
            if (_request != null)
            {
                if (interactive) EditorUtility.DisplayDialog(DisplayName, "正在检查更新，请稍候…", "好");
                return;
            }

            _interactive = interactive;
            _request = UnityWebRequest.Get(IndexUrl);
            _request.timeout = 10;
            var operation = _request.SendWebRequest();
            operation.completed += _ => Finish();
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (_request != null && _request.isDone) Finish();
        }

        private static void Finish()
        {
            EditorApplication.update -= Poll;
            var request = _request;
            _request = null;
            if (request == null) return;

            string error = request.error;
            var text = string.IsNullOrEmpty(error) ? request.downloadHandler.text : null;
            request.Dispose();

            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(text))
            {
                if (_interactive)
                    EditorUtility.DisplayDialog(DisplayName, "检查更新失败：连不上索引地址。\n\n" + IndexUrl +
                        (string.IsNullOrEmpty(error) ? "" : "\n\n" + error), "好");
                return;
            }

            var latest = FindLatestVersion(text, PackageId);
            if (string.IsNullOrEmpty(latest))
            {
                if (_interactive) EditorUtility.DisplayDialog(DisplayName, "检查更新失败：索引里没找到 " + PackageId + "。", "好");
                return;
            }

            EditorPrefs.SetString(LatestKey, latest);
            EditorPrefs.SetString(LastCheckKey, DateTime.UtcNow.Ticks.ToString());
            var installed = InstalledVersion;

            if (Compare(latest, installed) > 0)
            {
                if (_interactive)
                {
                    if (EditorUtility.DisplayDialog(DisplayName + " 有更新",
                            "当前版本：v" + installed + "\n最新版本：v" + latest + "\n\n在 VCC / ALCOM 里更新，或从发布页下载。",
                            "打开发布页", "以后再说"))
                        Application.OpenURL(ReleasesUrl);
                }
                else
                {
                    Debug.Log("[" + DisplayName + "] 有新版本：v" + latest + "（当前 v" + installed + "）。" +
                              "可在 VCC / ALCOM 里更新，或看 " + ReleasesUrl);
                }
            }
            else if (_interactive)
            {
                EditorUtility.DisplayDialog(DisplayName, "已是最新版本（v" + installed + "）。", "好");
            }
        }

        /// <summary>从 VPM 索引里抓某个包的最新版本号（不引入 JSON 库，直接按包块扫）。</summary>
        internal static string FindLatestVersion(string json, string packageId)
        {
            var index = json.IndexOf("\"" + packageId + "\"", StringComparison.Ordinal);
            if (index < 0) return null;

            // 包块的范围：从本包 id 开始，到下一个 "com.xxx": { 之前（不依赖缩进，压缩过的索引也能用）
            var end = json.Length;
            foreach (Match other in Regex.Matches(json, "\"(com\\.[A-Za-z0-9._-]+)\"\\s*:\\s*\\{"))
            {
                if (other.Index <= index) continue;
                if (other.Groups[1].Value == packageId) continue;
                end = other.Index;
                break;
            }
            var block = json.Substring(index, end - index);

            string best = null;
            foreach (Match match in Regex.Matches(block, "\"((\\d+)\\.(\\d+)\\.(\\d+))\"\\s*:"))
            {
                var version = match.Groups[1].Value;
                if (best == null || Compare(version, best) > 0) best = version;
            }
            return best;
        }

        /// <summary>版本比较：v1 &gt; v2 返回正数，解析不了就返回 0。</summary>
        internal static int Compare(string left, string right)
        {
            Version a, b;
            if (!Version.TryParse(Normalize(left), out a)) return 0;
            if (!Version.TryParse(Normalize(right), out b)) return 0;
            return a.CompareTo(b);
        }

        private static string Normalize(string version)
        {
            if (string.IsNullOrEmpty(version)) return "0.0.0";
            version = version.TrimStart('v', 'V');
            var dash = version.IndexOf('-');
            if (dash >= 0) version = version.Substring(0, dash);
            return version;
        }

        // ------------------------------------------------------------------ 启动后自动检查
        [InitializeOnLoadMethod]
        private static void ScheduleAutoCheck()
        {
            if (Application.isBatchMode) return;
            var last = EditorPrefs.GetString(LastCheckKey, string.Empty);
            long ticks;
            if (long.TryParse(last, out ticks) &&
                (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalHours < CheckIntervalHours)
                return;

            // 等编辑器完全就绪再发请求，避免拖慢启动
            EditorApplication.delayCall += () => Check(false);
        }
    }
}
