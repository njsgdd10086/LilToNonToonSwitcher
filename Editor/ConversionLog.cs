// LilToNonToon Switcher
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NonToonSwitcher
{
    /// <summary>Collects everything that happened while converting one material.</summary>
    public sealed class ConversionLog
    {
        public Material Source;
        public Material Destination;

        /// <summary>Output folder, kept here so the second conversion pass (baking) knows where to write.</summary>
        public string Folder;
        public bool BakeBaseTexture;
        public bool BakeSharedMask;

        public readonly List<string> Mappings = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Errors = new List<string>();

        /// <summary>
        /// Properties whose value has nowhere to go on the NonToon side, with the reason. The caller decides
        /// whether the loss still matters (an unsupported main colour, for example, is baked into the texture).
        /// </summary>
        public readonly List<KeyValuePair<string, string>> Unconverted =
            new List<KeyValuePair<string, string>>();

        private readonly HashSet<string> _unsupported = new HashSet<string>();

        public void Mapped(string from, string to)
        {
            Mappings.Add(from + "  ->  " + to);
        }

        public void Unsupported(string what)
        {
            if (string.IsNullOrEmpty(what)) return;
            if (_unsupported.Add(what)) Warnings.Add(what);
        }

        public void RecordUnconverted(string property, string reason)
        {
            foreach (var pair in Unconverted)
            {
                if (pair.Key == property) return;
            }
            Unconverted.Add(new KeyValuePair<string, string>(property, reason));
        }

        public void Warn(string message)
        {
            if (!string.IsNullOrEmpty(message)) Warnings.Add(message);
        }

        public void Error(string message)
        {
            if (!string.IsNullOrEmpty(message)) Errors.Add(message);
        }

        public bool HasProblems { get { return Warnings.Count > 0 || Errors.Count > 0; } }

        public string BuildText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== " + (Source != null ? Source.name : "(材质)") + " ===");
            if (Destination != null) sb.AppendLine("  -> " + Destination.name + "  [" + Destination.shader.name + "]");
            foreach (var error in Errors) sb.AppendLine("  [错误] " + error);
            foreach (var warning in Warnings) sb.AppendLine("  [警告] " + warning);
            foreach (var mapping in Mappings) sb.AppendLine("  [ OK ] " + mapping);
            return sb.ToString();
        }
    }

    /// <summary>Result of one convert / build-switch run.</summary>
    public sealed class ConversionResult
    {
        public readonly List<ConversionLog> Logs = new List<ConversionLog>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<Object> CreatedObjects = new List<Object>();
        public readonly List<string> NotConverted = new List<string>();

        public string OutputFolder;
        public GameObject SwitchObject;

        /// <summary>true 表示复用了同一个 avatar 下已有的切换对象，没有新建。</summary>
        public bool SwitchReused;

        /// <summary>切换对象里累计的条目数（复用时会累加）。</summary>
        public int SwitchEntryCount;

        /// <summary>true 表示这次把渲染器上的材质直接换成了 NonToon（没有切换开关）。</summary>
        public bool ReplacedOnRenderers;

        /// <summary>「复制一份再换」时生成的副本对象（形如 <名字>_nontoon）。</summary>
        public readonly List<string> DuplicatedObjects = new List<string>();

        public bool Success { get { return Errors.Count == 0; } }

        public void Warn(string message) { if (!string.IsNullOrEmpty(message)) Warnings.Add(message); }
        public void Error(string message) { if (!string.IsNullOrEmpty(message)) Errors.Add(message); }

        public string BuildText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("LilToNonToon Switcher");
            sb.AppendLine("输出文件夹  : " + OutputFolder);
            sb.AppendLine("已转换材质  : " + Logs.Count + " 个");
            if (SwitchObject != null)
            {
                sb.AppendLine(SwitchReused
                    ? "切换对象    : " + SwitchObject.name + "（复用已有开关，本次新增 " + SwitchEntryCount + " 条）"
                    : "切换对象    : " + SwitchObject.name + "（新建，共 " + SwitchEntryCount + " 条）");
            }
            else if (ReplacedOnRenderers)
            {
                sb.AppendLine("转换方式    : 直接把渲染器上的材质换成了 NonToon（没有建切换开关）");
            }

            if (DuplicatedObjects.Count > 0)
            {
                sb.AppendLine("副本对象    : " + DuplicatedObjects.Count + " 个");
                foreach (var name in DuplicatedObjects) sb.AppendLine("              " + name);
                sb.AppendLine("              想换回 lilToon 就把原对象重新勾上（场景里会同时存在两个 avatar 描述符，");
                sb.AppendLine("              上传时选 _nontoon 那一份即可）。");
            }

            sb.AppendLine();

            foreach (var error in Errors) sb.AppendLine("[错误] " + error);
            foreach (var warning in Warnings) sb.AppendLine("[警告] " + warning);
            if (Errors.Count > 0 || Warnings.Count > 0) sb.AppendLine();

            foreach (var log in Logs)
            {
                if (!log.HasProblems && log.Mappings.Count == 0) continue;
                sb.Append(log.BuildText());
                sb.AppendLine();
            }

            foreach (var name in NotConverted) sb.AppendLine("[跳过] " + name);
            return sb.ToString();
        }
    }
}
