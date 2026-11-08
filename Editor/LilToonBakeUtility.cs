// LilToNonToon Switcher
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NonToonSwitcher
{
    /// <summary>
    /// 一键烘焙：把 lilToon 的多层主色（1st `_Color` × `_MainTexHSVG` × 渐变，以及 2nd / 3rd 层）
    /// 合并进 `_MainTex`，让没有多层主色的着色器（NonToon 就是）也能拿到最终颜色。
    ///
    /// **为什么不直接调 lilToon 的 `TextureBake`**：它保存时会调用 `EditorUtility.SaveFilePanel`，
    /// 每个材质弹一次「保存文件」对话框 —— 批量烘焙不可用。这里复用它真正的烘焙内核
    /// （`Hidden/ltsother_baker` 着色器 + `lilToonInspector.RunBake`），只把「保存」换成本地静默写盘，
    /// 烘焙的数学与 lilToon 完全一致。走反射是因为离线编译（_test/*.rsp）不引用 lilToon 程序集，
    /// 和 ModuleInstaller 桥接模块包的思路一致。
    ///
    /// 烘焙会**清空** `_Color` / `_MainTexHSVG` / 渐变 / 2、3 层（与 lilToon 的行为一致），
    /// 所以每次烘焙前先把原值快照进备份文件，供「一键恢复」还原。
    /// </summary>
    public static class LilToonBakeUtility
    {
        public const string BackupFileName = "_LilToonBakeBackup.json";
        private const string BakerShaderName = "Hidden/ltsother_baker";

        /// <summary>lilToon 的 `lilConstants.defaultHSVG`（注意是 (0,1,1,1)，不是 (1,1,1,0)）。</summary>
        private static readonly Vector4 DefaultHsvg = new Vector4(0f, 1f, 1f, 1f);

        // ----- 与 lilToon 的 lilMaterialProperties.cs 一一对应 -----
        private const string PMainTex = "_MainTex";
        private const string PColor = "_Color";
        private const string PHsvg = "_MainTexHSVG";
        private const string PGradationStrength = "_MainGradationStrength";
        private const string PGradationTex = "_MainGradationTex";
        private const string PColorAdjustMask = "_MainColorAdjustMask";
        private const string PUseMain2nd = "_UseMain2ndTex";
        private const string PMain2ndTex = "_Main2ndTex";
        private const string PUseMain3rd = "_UseMain3rdTex";
        private const string PMain3rdTex = "_Main3rdTex";

        /// <summary>2 层要一起送进烘焙器的全部属性（顺序无关，缺的会自动跳过）。</summary>
        private static readonly string[] Main2ndProperties =
        {
            PUseMain2nd, "_Color2nd", PMain2ndTex, "_Main2ndBlendMask",
            "_Main2ndTexAngle", "_Main2ndTexDecalAnimation", "_Main2ndTexDecalSubParam",
            "_Main2ndTexIsDecal", "_Main2ndTexIsLeftOnly", "_Main2ndTexIsRightOnly",
            "_Main2ndTexShouldCopy", "_Main2ndTexShouldFlipMirror", "_Main2ndTexShouldFlipCopy",
            "_Main2ndTexIsMSDF", "_Main2ndTexBlendMode", "_Main2ndTexAlphaMode",
        };

        private static readonly string[] Main3rdProperties =
        {
            PUseMain3rd, "_Color3rd", PMain3rdTex, "_Main3rdBlendMask",
            "_Main3rdTexAngle", "_Main3rdTexDecalAnimation", "_Main3rdTexDecalSubParam",
            "_Main3rdTexIsDecal", "_Main3rdTexIsLeftOnly", "_Main3rdTexIsRightOnly",
            "_Main3rdTexShouldCopy", "_Main3rdTexShouldFlipMirror", "_Main3rdTexShouldFlipCopy",
            "_Main3rdTexIsMSDF", "_Main3rdTexBlendMode", "_Main3rdTexAlphaMode",
        };

        // ------------------------------------------------------------------ 结果

        public sealed class BakeSummary
        {
            public int Baked;
            public int Skipped;
            public int Failed;
            public int Restored;
            public readonly List<string> Messages = new List<string>();

            public bool HasWork { get { return Baked > 0 || Restored > 0; } }

            public string BuildText()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[LilToNonToon Switcher] 烘焙汇总：成功 " + Baked + "，跳过 " + Skipped +
                              "，失败 " + Failed + (Restored > 0 ? "，恢复 " + Restored : ""));
                foreach (var message in Messages) sb.AppendLine("  · " + message);
                return sb.ToString().TrimEnd();
            }
        }

        // ------------------------------------------------------------------ 备份数据（JSON）

        [Serializable]
        private sealed class BackupEntry
        {
            public string materialPath;      // 资产路径，恢复时用它找回材质
            public string mainTexPath;       // 原主贴图
            public float[] mainTexScale;     // ST
            public float[] mainTexOffset;
            public float[] color;
            public float[] hsvg;
            public float gradationStrength;
            public string gradationTexPath;
            public float useMain2nd;
            public string main2ndTexPath;
            public float useMain3rd;
            public string main3rdTexPath;
            public string bakedTexPath;      // 烘焙产物（恢复时只做记录，不删文件）
        }

        [Serializable]
        private sealed class BackupFile
        {
            public List<BackupEntry> entries = new List<BackupEntry>();
        }

        // ------------------------------------------------------------------ 对外入口

        /// <summary>lilToon 的编辑器烘焙器是否可用（没装 lilToon 时给出明确原因）。</summary>
        public static bool IsBakerAvailable(out string reason)
        {
            reason = null;
            if (Shader.Find(BakerShaderName) == null)
            {
                reason = "找不到 lilToon 的烘焙着色器 " + BakerShaderName + "（jp.lilxyzw.liltoon 没装或版本不符）。";
                return false;
            }
            if (FindRunBake() == null)
            {
                reason = "找不到 lilToonInspector.RunBake（lilToon 编辑器脚本没加载）。";
                return false;
            }
            return true;
        }

        /// <summary>当前备份里有几条记录（窗口用来显示「恢复」是否可用）。结果会缓存，避免 OnGUI 每帧读文件。</summary>
        public static int BackupCount
        {
            get
            {
                if (_cachedBackupCount < 0)
                {
                    var file = LoadBackup();
                    _cachedBackupCount = file == null || file.entries == null ? 0 : file.entries.Count;
                }
                return _cachedBackupCount;
            }
        }

        private static int _cachedBackupCount = -1;

        /// <summary>把选中对象上的 lilToon 材质全部烘焙（合并 1/2/3 层主色）。</summary>
        public static BakeSummary BakeSelection(bool includeInactive)
        {
            var summary = new BakeSummary();
            string reason;
            if (!IsBakerAvailable(out reason))
            {
                summary.Failed++;
                summary.Messages.Add(reason);
                return summary;
            }

            var materials = CollectLilToonMaterials(Selection.gameObjects, includeInactive);
            if (materials.Count == 0)
            {
                summary.Skipped++;
                summary.Messages.Add("选中对象里没有 lilToon 材质。");
                return summary;
            }

            var backup = LoadBackup() ?? new BackupFile();
            if (backup.entries == null) backup.entries = new List<BackupEntry>();

            var bakerShader = Shader.Find(BakerShaderName);
            foreach (var material in materials)
            {
                try
                {
                    BakeOne(material, bakerShader, backup, summary);
                }
                catch (Exception exception)
                {
                    summary.Failed++;
                    summary.Messages.Add(material.name + "：烘焙失败 —— " + exception.Message);
                }
            }

            SaveBackup(backup);
            AssetDatabase.SaveAssets();
            return summary;
        }

        /// <summary>把上一次烘焙改动过的材质全部还原成烘焙前的样子。</summary>
        public static BakeSummary Restore()
        {
            var summary = new BakeSummary();
            var backup = LoadBackup();
            if (backup == null || backup.entries == null || backup.entries.Count == 0)
            {
                summary.Skipped++;
                summary.Messages.Add("没有找到备份（" + BackupPath() + "），没有可恢复的内容。");
                return summary;
            }

            // 恢复不成功的条目要留在备份里，否则用户连重试的机会都没有。
            var keep = new List<BackupEntry>();
            foreach (var entry in backup.entries)
            {
                var material = string.IsNullOrEmpty(entry.materialPath)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<Material>(entry.materialPath);
                if (material == null)
                {
                    summary.Skipped++;
                    summary.Messages.Add("找不到材质 " + entry.materialPath + "，已跳过（备份条目保留）。");
                    keep.Add(entry);
                    continue;
                }

                try
                {
                    RestoreOne(material, entry);
                    summary.Restored++;
                    summary.Messages.Add(material.name + "：已还原（主贴图、主色、HSVG、渐变、2/3 层）" + VerifyText(material, entry));
                }
                catch (Exception exception)
                {
                    summary.Failed++;
                    keep.Add(entry);
                    summary.Messages.Add(material.name + "：还原失败 —— " + exception.Message + "（备份条目保留）");
                }
            }

            // 已经转换出来的 NonToon 材质当初是用烘焙后的贴图转的，恢复 lilToon 材质不会回滚它们。
            // 这里**只检测并提示**，绝不自动改：转换时已经把 _Color 等乘进烘焙贴图，直接把 _BaseTexture
            // 指回原贴图会丢掉那部分乘法，画面会变亮 —— 正确做法是恢复后重新转换一次。
            ReportConvertedOutputs(backup.entries, summary);

            backup.entries = keep;
            SaveBackup(backup);
            AssetDatabase.SaveAssets();
            return summary;
        }

        /// <summary>恢复后复核几个关键属性，把没写进去的情况直接报出来。</summary>
        private static string VerifyText(Material material, BackupEntry entry)
        {
            var problems = new List<string>();
            if (HasFloat(material, PUseMain2nd) &&
                Math.Abs(material.GetFloat(PUseMain2nd) - entry.useMain2nd) > 0.001f)
                problems.Add("_UseMain2ndTex");

            if (HasFloat(material, PUseMain3rd) &&
                Math.Abs(material.GetFloat(PUseMain3rd) - entry.useMain3rd) > 0.001f)
                problems.Add("_UseMain3rdTex");

            if (HasColor(material, PColor) && entry.color != null && entry.color.Length == 4)
            {
                var expected = new Color(entry.color[0], entry.color[1], entry.color[2], entry.color[3]);
                if (material.GetColor(PColor) != expected) problems.Add("_Color");
            }

            if (HasTexture(material, PMainTex) && !string.IsNullOrEmpty(entry.mainTexPath))
            {
                var now = PathOf(material.GetTexture(PMainTex));
                if (now != entry.mainTexPath) problems.Add("_MainTex");
            }

            return problems.Count == 0 ? "" : "  ⚠ 复核不一致：" + string.Join("、", problems.ToArray());
        }

        /// <summary>
        /// 统计有多少材质槽还在引用本次烘焙的 `_baked.png`（= 转换出来的 NonToon 材质），只提示不修改。
        /// 原因见调用处：直接改贴图引用会丢掉转换时乘进去的 `_Color`，必须重新转换。
        /// </summary>
        private static int ReportConvertedOutputs(List<BackupEntry> entries, BakeSummary summary)
        {
            var baked = new HashSet<string>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.bakedTexPath) || entry.bakedTexPath == entry.mainTexPath) continue;
                baked.Add(entry.bakedTexPath);
            }
            if (baked.Count == 0) return 0;

            var slots = 0;
            var materials = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null || material.shader == null) continue;
                if (ShaderUtility.IsLilToon(material.shader)) continue;

                var hit = false;
                foreach (var property in new[] { "_BaseTexture", "_MainTex" })
                {
                    if (!HasTexture(material, property)) continue;
                    var texturePath = PathOf(material.GetTexture(property));
                    if (string.IsNullOrEmpty(texturePath) || !baked.Contains(texturePath)) continue;
                    slots++;
                    hit = true;
                }
                if (hit && !materials.Contains(path)) materials.Add(path);
            }

            if (slots == 0) return 0;
            summary.Messages.Add("注意：还有 " + slots + " 个材质槽在用烘焙后的贴图（转换出来的 NonToon 材质，" +
                                 materials.Count + " 个材质）。恢复只回滚 lilToon 材质，不会自动回滚它们 —— " +
                                 "要看到恢复前的效果，请对这些材质「重新执行一次转换」。" +
                                 "涉及：" + string.Join("、", materials.ToArray()));
            return slots;
        }

        // ------------------------------------------------------------------ 烘焙单个材质

        private static void BakeOne(Material material, Shader bakerShader, BackupFile backup, BakeSummary summary)
        {
            if (!material.HasProperty(PMainTex))
            {
                summary.Skipped++;
                summary.Messages.Add(material.name + "：不是 lilToon 材质，跳过。");
                return;
            }

            var materialPath = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(materialPath))
            {
                summary.Skipped++;
                summary.Messages.Add(material.name + "：材质不是项目资产（无法在旁边保存烘焙贴图），跳过。");
                return;
            }

            var originalMainTex = material.GetTexture(PMainTex);
            if (originalMainTex == null)
            {
                summary.Skipped++;
                summary.Messages.Add(material.name + "：没有主贴图（" + PMainTex + " 为空），lilToon 也无法烘焙，跳过。");
                return;
            }

            var bake2nd = HasFloat(material, PUseMain2nd) && material.GetFloat(PUseMain2nd) != 0f;
            var bake3rd = HasFloat(material, PUseMain3rd) && material.GetFloat(PUseMain3rd) != 0f;
            var hasColorShift = HasColor(material, PColor) && material.GetColor(PColor) != Color.white;
            var hasHsvg = HasVector(material, PHsvg) && material.GetVector(PHsvg) != DefaultHsvg;
            var hasGradation = HasFloat(material, PGradationStrength) && material.GetFloat(PGradationStrength) != 0f;

            if (!hasColorShift && !hasHsvg && !hasGradation && !bake2nd && !bake3rd)
            {
                summary.Skipped++;
                summary.Messages.Add(material.name + "：主色 / HSVG / 渐变 / 2、3 层都是默认值，无需烘焙，跳过。");
                return;
            }

            // 先快照，再动手（烘焙会清空这些值）
            var entry = Snapshot(material, materialPath);

            var baker = new Material(bakerShader);
            var temporary = new List<UnityEngine.Object>();
            Texture2D baked = null;
            try
            {
                var source = LoadTexture(originalMainTex);
                if (source != null) temporary.Add(source);

                baker.SetColor(PColor, HasColor(material, PColor) ? material.GetColor(PColor) : Color.white);
                baker.SetVector(PHsvg, HasVector(material, PHsvg) ? material.GetVector(PHsvg) : DefaultHsvg);
                if (HasFloat(material, PGradationStrength))
                    baker.SetFloat(PGradationStrength, material.GetFloat(PGradationStrength));
                if (HasTexture(material, PGradationTex))
                    baker.SetTexture(PGradationTex, material.GetTexture(PGradationTex));
                if (HasTexture(material, PColorAdjustMask))
                    baker.SetTexture(PColorAdjustMask, material.GetTexture(PColorAdjustMask));
                baker.SetTexture(PMainTex, source != null ? (Texture)source : Texture2D.whiteTexture);
                if (HasTexture(material, PMainTex))
                {
                    baker.SetTextureScale(PMainTex, material.GetTextureScale(PMainTex));
                    baker.SetTextureOffset(PMainTex, material.GetTextureOffset(PMainTex));
                }

                if (bake2nd) CopyLayer(material, baker, Main2ndProperties, temporary);
                if (bake3rd) CopyLayer(material, baker, Main3rdProperties, temporary);

                baked = RunBake(source, baker);
                if (baked == null)
                {
                    summary.Failed++;
                    summary.Messages.Add(material.name + "：lilToon 的 RunBake 没有返回结果（烘焙着色器不可用？）。");
                    return;
                }

                var bakedPath = SaveBakedPng(material, materialPath, baked);
                if (string.IsNullOrEmpty(bakedPath))
                {
                    summary.Failed++;
                    summary.Messages.Add(material.name + "：烘焙贴图写盘失败。");
                    return;
                }

                var bakedAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(bakedPath);
                if (bakedAsset == null)
                {
                    summary.Failed++;
                    summary.Messages.Add(material.name + "：烘焙贴图导入失败（" + bakedPath + "）。");
                    return;
                }

                CopyTextureSettings(originalMainTex, bakedAsset);

                // 与 lilToon 烘焙后的状态保持一致：这些值已经烤进贴图，必须清掉，否则会二次生效。
                if (HasVector(material, PHsvg)) material.SetVector(PHsvg, DefaultHsvg);
                if (HasColor(material, PColor)) material.SetColor(PColor, Color.white);
                if (HasFloat(material, PGradationStrength)) material.SetFloat(PGradationStrength, 0f);
                if (HasTexture(material, PGradationTex)) material.SetTexture(PGradationTex, null);
                if (bake2nd)
                {
                    if (HasFloat(material, PUseMain2nd)) material.SetFloat(PUseMain2nd, 0f);
                    if (HasTexture(material, PMain2ndTex)) material.SetTexture(PMain2ndTex, null);
                }
                if (bake3rd)
                {
                    if (HasFloat(material, PUseMain3rd)) material.SetFloat(PUseMain3rd, 0f);
                    if (HasTexture(material, PMain3rdTex)) material.SetTexture(PMain3rdTex, null);
                }

                material.SetTexture(PMainTex, bakedAsset);
                EditorUtility.SetDirty(material);

                entry.bakedTexPath = bakedPath;
                Upsert(backup, entry);

                summary.Baked++;
                summary.Messages.Add(material.name + "：已烘焙" +
                                     (bake2nd ? "（含 2 层）" : "") + (bake3rd ? "（含 3 层）" : "") +
                                     " → " + bakedPath);
            }
            finally
            {
                foreach (var obj in temporary)
                {
                    if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
                }
                if (baked != null) UnityEngine.Object.DestroyImmediate(baked);
                UnityEngine.Object.DestroyImmediate(baker);
            }
        }

        /// <summary>把一层的全部属性按类型复制到烘焙器材质上（贴图会额外读取一份可读写副本）。</summary>
        private static void CopyLayer(Material source, Material baker, string[] names, List<UnityEngine.Object> temporary)
        {
            foreach (var name in names)
            {
                if (!source.HasProperty(name) || !baker.HasProperty(name)) continue;
                var type = PropertyType(baker, name);
                switch (type)
                {
                    case ShaderPropertyType.Texture:
                    {
                        var texture = source.GetTexture(name);
                        if (texture == null)
                        {
                            baker.SetTexture(name, Texture2D.whiteTexture);
                        }
                        else
                        {
                            var loaded = LoadTexture(texture);
                            if (loaded != null) temporary.Add(loaded);
                            baker.SetTexture(name, loaded != null ? (Texture)loaded : texture);
                        }
                        baker.SetTextureScale(name, source.GetTextureScale(name));
                        baker.SetTextureOffset(name, source.GetTextureOffset(name));
                        break;
                    }
                    case ShaderPropertyType.Color:
                        baker.SetColor(name, source.GetColor(name));
                        break;
                    case ShaderPropertyType.Vector:
                        baker.SetVector(name, source.GetVector(name));
                        break;
                    default:
                        baker.SetFloat(name, source.GetFloat(name));
                        break;
                }
            }
        }

        private static void RestoreOne(Material material, BackupEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.mainTexPath))
            {
                var mainTex = AssetDatabase.LoadAssetAtPath<Texture2D>(entry.mainTexPath);
                if (mainTex != null) material.SetTexture(PMainTex, mainTex);
            }
            if (entry.mainTexScale != null && entry.mainTexScale.Length == 2)
                material.SetTextureScale(PMainTex, new Vector2(entry.mainTexScale[0], entry.mainTexScale[1]));
            if (entry.mainTexOffset != null && entry.mainTexOffset.Length == 2)
                material.SetTextureOffset(PMainTex, new Vector2(entry.mainTexOffset[0], entry.mainTexOffset[1]));

            if (entry.color != null && entry.color.Length == 4 && HasColor(material, PColor))
                material.SetColor(PColor, new Color(entry.color[0], entry.color[1], entry.color[2], entry.color[3]));

            if (entry.hsvg != null && entry.hsvg.Length == 4 && HasVector(material, PHsvg))
                material.SetVector(PHsvg, new Vector4(entry.hsvg[0], entry.hsvg[1], entry.hsvg[2], entry.hsvg[3]));

            if (HasFloat(material, PGradationStrength))
                material.SetFloat(PGradationStrength, entry.gradationStrength);
            if (HasTexture(material, PGradationTex))
                material.SetTexture(PGradationTex, LoadTextureAsset(entry.gradationTexPath));

            if (HasFloat(material, PUseMain2nd)) material.SetFloat(PUseMain2nd, entry.useMain2nd);
            if (HasTexture(material, PMain2ndTex))
                material.SetTexture(PMain2ndTex, LoadTextureAsset(entry.main2ndTexPath));

            if (HasFloat(material, PUseMain3rd)) material.SetFloat(PUseMain3rd, entry.useMain3rd);
            if (HasTexture(material, PMain3rdTex))
                material.SetTexture(PMain3rdTex, LoadTextureAsset(entry.main3rdTexPath));

            EditorUtility.SetDirty(material);
        }

        // ------------------------------------------------------------------ 快照 / 备份

        private static BackupEntry Snapshot(Material material, string materialPath)
        {
            var entry = new BackupEntry
            {
                materialPath = materialPath,
                mainTexPath = PathOf(material.GetTexture(PMainTex)),
                color = ToArray(HasColor(material, PColor) ? material.GetColor(PColor) : Color.white),
                hsvg = ToArray(HasVector(material, PHsvg) ? material.GetVector(PHsvg) : DefaultHsvg),
                gradationStrength = HasFloat(material, PGradationStrength) ? material.GetFloat(PGradationStrength) : 0f,
                gradationTexPath = PathOf(GetTexture(material, PGradationTex)),
                useMain2nd = HasFloat(material, PUseMain2nd) ? material.GetFloat(PUseMain2nd) : 0f,
                main2ndTexPath = PathOf(GetTexture(material, PMain2ndTex)),
                useMain3rd = HasFloat(material, PUseMain3rd) ? material.GetFloat(PUseMain3rd) : 0f,
                main3rdTexPath = PathOf(GetTexture(material, PMain3rdTex)),
            };
            if (HasTexture(material, PMainTex))
            {
                var scale = material.GetTextureScale(PMainTex);
                var offset = material.GetTextureOffset(PMainTex);
                entry.mainTexScale = new[] { scale.x, scale.y };
                entry.mainTexOffset = new[] { offset.x, offset.y };
            }
            return entry;
        }

        /// <summary>
        /// 记录快照。**已经有条目就不覆盖**：同一个材质反复烘焙时，只有第一次的快照才是"烘焙前"的真状态，
        /// 后一次快照拍到的已经是烘焙结果（主色白、2/3 层关闭），拿它当备份就等于恢复成烘焙后的样子。
        /// </summary>
        private static void Upsert(BackupFile backup, BackupEntry entry)
        {
            for (var i = 0; i < backup.entries.Count; i++)
            {
                if (backup.entries[i].materialPath != entry.materialPath) continue;
                backup.entries[i].bakedTexPath = entry.bakedTexPath;   // 产物路径要更新成最新的
                return;
            }
            backup.entries.Add(entry);
        }

        private static string BackupPath()
        {
            return NonToonSwitcherSettings.instance.OutputFolder + "/" + BackupFileName;
        }

        private static BackupFile LoadBackup()
        {
            var path = BackupPath();
            if (!File.Exists(Path.GetFullPath(path))) return null;
            try
            {
                var json = File.ReadAllText(Path.GetFullPath(path));
                return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<BackupFile>(json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SaveBackup(BackupFile backup)
        {
            var folder = NonToonSwitcherSettings.instance.OutputFolder;
            if (!AssetDatabase.IsValidFolder(folder)) ShaderUtility.TryCreateFolderRecursive(folder);

            var path = BackupPath();
            try
            {
                File.WriteAllText(Path.GetFullPath(path), JsonUtility.ToJson(backup, true));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                _cachedBackupCount = backup.entries == null ? 0 : backup.entries.Count;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[LilToNonToon Switcher] 备份写入失败：" + exception.Message);
            }
        }

        // ------------------------------------------------------------------ 收集材质

        public static List<Material> CollectLilToonMaterials(GameObject[] targets, bool includeInactive)
        {
            var materials = new List<Material>();
            if (targets == null) return materials;
            var renderers = new List<Renderer>();
            foreach (var target in targets)
            {
                if (target == null) continue;
                NonToonConverter.CollectRenderers(target, includeInactive, renderers);
            }
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || materials.Contains(material)) continue;
                    if (!ShaderUtility.IsLilToon(material.shader)) continue;
                    materials.Add(material);
                }
            }
            return materials;
        }

        // ------------------------------------------------------------------ lilToon 反射桥

        private static MethodInfo FindRunBake()
        {
            // 注意命名空间是 lilToon（大写 T），不是 liltoon。
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("lilToon.lilToonInspector", false) ??
                           assembly.GetType("liltoon.lilToonInspector", false);
                if (type == null) continue;
                var method = type.GetMethod("RunBake", BindingFlags.Public | BindingFlags.Static);
                if (method != null) return method;
            }
            return null;
        }

        /// <summary>调用 lilToon 自己的烘焙内核（`Graphics.Blit` + `Hidden/ltsother_baker`）。</summary>
        private static Texture2D RunBake(Texture2D source, Material baker)
        {
            var method = FindRunBake();
            if (method == null) return null;
            var args = new object[] { null, source, baker, null };
            method.Invoke(null, args);
            return args[0] as Texture2D;
        }

        // ------------------------------------------------------------------ 贴图读写

        /// <summary>烘焙只走 Graphics.Blit，不需要可读写副本，直接用导入后的贴图即可。</summary>
        private static Texture2D LoadTexture(Texture texture)
        {
            if (texture == null) return null;
            var texture2D = texture as Texture2D;
            if (texture2D != null) return texture2D;
            var path = PathOf(texture);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Texture LoadTextureAsset(string path)
        {
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static string SaveBakedPng(Material material, string materialPath, Texture2D baked)
        {
            var folder = Path.GetDirectoryName(materialPath);
            if (string.IsNullOrEmpty(folder)) return null;
            folder = folder.Replace('\\', '/');

            var fileName = SanitizeFileName(material.name) + "_baked.png";
            var assetPath = folder + "/" + fileName;

            try
            {
                var bytes = baked.EncodeToPNG();
                File.WriteAllBytes(Path.GetFullPath(assetPath), bytes);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                return assetPath;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[LilToNonToon Switcher] 写烘焙贴图失败（" + assetPath + "）：" + exception.Message);
                return null;
            }
        }

        /// <summary>把原主贴图的导入设置抄给烘焙贴图（与 lilToon 的 CopyTextureSetting 等价）。</summary>
        private static void CopyTextureSettings(Texture original, Texture2D baked)
        {
            if (original == null || baked == null) return;
            var fromPath = AssetDatabase.GetAssetPath(original);
            var toPath = AssetDatabase.GetAssetPath(baked);
            if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath)) return;

            var from = AssetImporter.GetAtPath(fromPath) as TextureImporter;
            var to = AssetImporter.GetAtPath(toPath) as TextureImporter;
            if (from == null || to == null) return;

            var settings = new TextureImporterSettings();
            from.ReadTextureSettings(settings);
            to.SetTextureSettings(settings);
            to.SetPlatformTextureSettings(from.GetDefaultPlatformTextureSettings());
            AssetDatabase.ImportAsset(toPath);
        }

        // ------------------------------------------------------------------ 小工具

        private static string PathOf(UnityEngine.Object asset)
        {
            return asset == null ? null : AssetDatabase.GetAssetPath(asset);
        }

        private static Texture GetTexture(Material material, string name)
        {
            return HasTexture(material, name) ? material.GetTexture(name) : null;
        }

        private static bool HasTexture(Material material, string name)
        {
            return material.HasProperty(name) && PropertyType(material, name) == ShaderPropertyType.Texture;
        }

        private static bool HasColor(Material material, string name)
        {
            return material.HasProperty(name) && PropertyType(material, name) == ShaderPropertyType.Color;
        }

        private static bool HasVector(Material material, string name)
        {
            var type = PropertyType(material, name);
            return material.HasProperty(name) && (type == ShaderPropertyType.Vector || type == ShaderPropertyType.Color);
        }

        private static bool HasFloat(Material material, string name)
        {
            return material.HasProperty(name) && PropertyType(material, name) == ShaderPropertyType.Float;
        }

        private static ShaderPropertyType PropertyType(Material material, string name)
        {
            return material.shader == null ? ShaderPropertyType.Float : PropertyType(material.shader, name);
        }

        private static ShaderPropertyType PropertyType(Shader shader, string name)
        {
            var index = shader.FindPropertyIndex(name);
            return index < 0 ? ShaderPropertyType.Float : shader.GetPropertyType(index);
        }

        private static float[] ToArray(Color color)
        {
            return new[] { color.r, color.g, color.b, color.a };
        }

        private static float[] ToArray(Vector4 vector)
        {
            return new[] { vector.x, vector.y, vector.z, vector.w };
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "material";
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }
            return name.Replace('/', '_');
        }
    }
}
