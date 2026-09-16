// LilToNonToon Switcher
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NonToonSwitcher
{
    /// <summary>How the converted materials end up on the model.</summary>
    public enum ReplaceMode
    {
        /// <summary>Keep the lilToon materials and add a menu switch (default).</summary>
        Switch = 0,
        /// <summary>Replace the materials on the selected objects in place (no switch, no copy).</summary>
        ReplaceInPlace = 1,
        /// <summary>Duplicate the selected objects as &lt;名字&gt;_nontoon, convert the copy and disable the original.</summary>
        DuplicateThenReplace = 2,
    }

    public sealed class ConvertRequest
    {
        public GameObject[] Targets;
        public bool IncludeInactive = true;
        public bool ApplyToSelectionImmediately = true;
        public bool CreateSwitcher = true;
        public bool CreateMenuToggle = true;
        public bool NonToonOnByDefault = true;
        public bool BakeSharedMask = true;
        public bool BakeBaseTexture = true;
        public bool BakeGradients = true;
        /// <summary>Which MA component performs the switch.</summary>
        public SwitcherMode SwitcherMode = SwitcherMode.MaterialSetter;
        /// <summary>
        /// Replaces the materials on the selected renderers with the converted ones.
        /// Default: only in Material Swap mode. With the Material Setter the renderers keep their original
        /// lilToon materials so the menu toggle can switch them to NonToon - swapping them here would make
        /// the toggle point at the material that is already assigned.
        /// </summary>
        public bool ReplaceMaterialsOnRenderers;
        /// <summary>切换开关 / 原地直接替换 / 复制一份 <名字>_nontoon 再替换。</summary>
        public ReplaceMode ReplaceMode = ReplaceMode.Switch;
        /// <summary>Also convert materials that are only referenced by animation clips.</summary>
        public bool CollectAnimatorMaterials = true;
        /// <summary>
        /// 同一个 avatar 下已经有 _NonToonSwitch 时，把新材质追加进去而不是新建一个。
        /// 关掉则每次转换都新建（会产生多个开关，仅在你确实想要分开控制时才关）。
        /// </summary>
        public bool ReuseExistingSwitcher = true;
        public string OutputFolder = NonToonSwitcherSettings.DefaultOutputFolder;
        public string MenuParameter = "NonToon";
        public string MenuLabel = "NonToon";
    }

    /// <summary>The conversion itself: lilToon materials in, saved NonToon materials out.</summary>
    public static class NonToonConverter
    {
        // ------------------------------------------------------------------ discovery

        public static void CollectRenderers(GameObject root, bool includeInactive, List<Renderer> renderers)
        {
            if (root == null) return;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(includeInactive))
            {
                if (!renderers.Contains(renderer)) renderers.Add(renderer);
            }
        }

        public static void CollectAnimatorMaterials(GameObject root, bool includeInactive, List<Material> materials)
        {
            if (root == null) return;
#if UNITY_EDITOR
            var animators = root.GetComponentsInChildren<Animator>(includeInactive);
            foreach (var animator in animators)
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null) continue;
                    AddCurveMaterials(clip, materials);
                }
            }

            // Animation clips referenced by components that play them (VRChat "Playable Layers" etc. are
            // covered by the animator above; this picks up standalone Animation components).
            foreach (var animation in root.GetComponentsInChildren<Animation>(includeInactive))
            {
                foreach (AnimationState state in animation)
                {
                    if (state == null || state.clip == null) continue;
                    AddCurveMaterials(state.clip, materials);
                }
            }
#endif
        }

        private static void AddCurveMaterials(AnimationClip clip, List<Material> materials)
        {
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes == null) continue;
                foreach (var keyframe in keyframes)
                {
                    var material = keyframe.value as Material;
                    if (material != null && !materials.Contains(material)) materials.Add(material);
                }
            }
        }

        // ------------------------------------------------------------------ names

        /// <summary>Tracks which source material already owns an output path, so equal names never collide.</summary>
        private sealed class FolderLocks
        {
            public readonly Dictionary<string, string> Owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Keeps two material assets with the same name from overwriting each other.</summary>
        public static object CreateNameLocks()
        {
            return new FolderLocks();
        }

        /// <summary>Suffix appended to converted material names, e.g. "衣服" -> "衣服_nontoon.mat".</summary>
        public const string OutputSuffix = "_nontoon";

        /// <summary>切换对象的固定名字，用于在同一个 avatar 下复用。</summary>
        public const string SwitcherObjectName = "_NonToonSwitch";

        /// <summary>「复制一份再换」时副本对象的名字后缀：<原对象名>_nontoon。</summary>
        public const string DuplicateSuffix = "_nontoon";

        /// <summary>
        /// File name of the converted material: "&lt;original name&gt;_nontoon.mat".
        /// An existing suffix is not duplicated.
        /// </summary>
        public static string OutputName(string sourceName)
        {
            var name = ShaderUtility.SanitizeFileName(sourceName);
            if (name.EndsWith(OutputSuffix, StringComparison.OrdinalIgnoreCase))
                return name + ".mat";
            return name + OutputSuffix + ".mat";
        }

        /// <summary>Converts a single material into <paramref name="outputFolder"/> and returns the new asset.</summary>
        public static Material ConvertMaterial(Material source, string outputFolder, string preferredName,
            object locks, bool bakeSharedMask, bool bakeBaseTexture, ConversionLog log,
            string assetPath = null)
        {
            var folderLocks = locks as FolderLocks;
            var shader = ShaderUtility.FindNonToonShader();
            if (shader == null)
            {
                log.Error("找不到 NonToon shader，请先安装 jp.lilxyzw.nontoon（以及 jp.lilxyzw.shadercore）。");
                return null;
            }

            var folder = outputFolder.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(folder)) folder = NonToonSwitcherSettings.DefaultOutputFolder;
            NonToonMaskBuilder.EnsureFolder(folder);

            var name = string.IsNullOrEmpty(preferredName) ? source.name : preferredName;
            name = ShaderUtility.SanitizeFileName(name);
            var path = assetPath;
            if (string.IsNullOrEmpty(path))
                path = MakeUniqueAssetPath(folder + "/" + OutputName(name), source, locks);

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            Material target;
            if (existing != null && ShaderUtility.IsNonToon(existing.shader))
            {
                target = existing;
            }
            else
            {
                target = new Material(shader);
                target.name = Path.GetFileNameWithoutExtension(path);
                // Create the asset *before* the values are written. AssetDatabase.CreateAsset reverts an
                // in-memory object when something already occupies the target path, which would silently
                // throw away every value set before this point.
                AssetDatabase.CreateAsset(target, path);
            }

            target.shader = shader;
            ApplyMappings(source, target, log);
            ApplyRenderingMode(source, target, log);
            ApplyVertexColorFlags(source, target, log);
            ApplyOutlineWidthMaskWorkaround(source, target, log);

            // Integer properties do not survive a plain SetInt on Shader Core shaders, so they are written
            // through the serialized property list once the material exists as an asset.
            EditorUtility.SetDirty(target);
            ShaderUtility.PersistIntegers(target);

            // Flush the material to disk right away. The baking pass below imports PNG / gradient assets,
            // and an import reloads materials from disk - anything still only in memory would be reverted.
            AssetDatabase.SaveAssets();

            log.Destination = target;
            log.Folder = folder;
            log.BakeBaseTexture = bakeBaseTexture;
            log.BakeSharedMask = bakeSharedMask;
            return target;
        }

        /// <summary>
        /// Second conversion pass. It runs after <see cref="AssetDatabase.StopAssetEditing"/> because baked
        /// assets (PNGs, gradient arrays) have to be imported before they can be referenced. Imports can
        /// reload the material from disk, so the asset is re-fetched afterwards.
        /// </summary>
        private static void PostProcess(ConversionLog log)
        {
            var source = log.Source;
            var target = log.Destination;
            if (source == null || target == null || string.IsNullOrEmpty(log.Folder)) return;

            if (log.BakeBaseTexture)
            {
                var baked = NonToonTextureBaker.BakeBaseTexture(source, target, log.Folder, log);
                if (baked != null && ShaderUtility.HasProperty(target, "_BaseTexture"))
                {
                    target.SetTexture("_BaseTexture", baked);
                    // The colour now lives in the pixels.
                    if (ShaderUtility.HasProperty(target, "_BaseColor")) target.SetColor("_BaseColor", Color.white);
                    log.Unconverted.Clear();
                }
            }

            if (log.BakeSharedMask) NonToonMaskBuilder.Bake(source, target, log.Folder, log);

            // Anything that had nowhere to go and could not be baked away becomes a warning now.
            foreach (var pair in log.Unconverted) log.Unsupported(pair.Value);

            EditorUtility.SetDirty(target);
            ShaderUtility.PersistIntegers(target);
            AssetDatabase.SaveAssets();

            // Re-fetch: importing the baked textures can replace the in-memory object behind our back.
            var assetPath = AssetDatabase.GetAssetPath(target);
            log.Destination = string.IsNullOrEmpty(assetPath)
                ? target
                : AssetDatabase.LoadAssetAtPath<Material>(assetPath) ?? target;
        }

        /// <summary>
        /// Bakes the lilToon shadow / rim shade colours into a Shader Core gradient array and points the
        /// NonToon Shade and RimShade modules at it. Must run outside of an asset-editing block.
        /// </summary>
        private static void ApplyGradients(ConversionLog log, string folder, bool bakeGradients)
        {
            var source = log.Source;
            var destination = log.Destination;
            if (source == null || destination == null) return;

            var shadeProperty = FindGradientIndexProperty(destination.shader, "shade_", "ShadeGradientIndex");
            var rimShadeProperty = FindGradientIndexProperty(destination.shader, "rimshade_", "RimShadeGradientIndex");

            if (!bakeGradients) return;

            if (shadeProperty == null && rimShadeProperty == null)
            {
                if (ShaderUtility.HasProperty(source, "_UseShadow") && source.GetFloat("_UseShadow") != 0f)
                    log.Unsupported("lilToon 的阴影颜色（未安装 NonToon 的 Shade 模块）");
                return;
            }

            var gradients = NonToonTextureBaker.BakeGradients(source, destination, folder, out var bakedIndices, log);
            if (gradients == null)
            {
                // Without a ramp the Shade module would sample an empty array, which renders black, so the
                // modules are left disabled (index -1) instead.
                if (shadeProperty != null) ShaderUtility.SetIntPersistent(destination, shadeProperty, -1);
                if (rimShadeProperty != null) ShaderUtility.SetIntPersistent(destination, rimShadeProperty, -1);
                EditorUtility.SetDirty(destination);
                AssetDatabase.SaveAssets();
                return;
            }

            if (gradients is Texture gradientTexture && ShaderUtility.HasProperty(destination, "_SharedGradients"))
                destination.SetTexture("_SharedGradients", gradientTexture);

            // Only point a module at a slice that really exists: the Shade slice is baked first, the RimShade
            // slice right after it, and anything else must stay at -1 or the shader samples out of range.
            var shadeIndex = bakedIndices.Count > 0 ? bakedIndices[0] : -1;
            var rimShadeIndex = bakedIndices.Count > 1 ? bakedIndices[1] : -1;
            if (shadeProperty != null) ShaderUtility.SetIntPersistent(destination, shadeProperty, shadeIndex);
            if (rimShadeProperty != null) ShaderUtility.SetIntPersistent(destination, rimShadeProperty, rimShadeIndex);
            EditorUtility.SetDirty(destination);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Converts one material and bakes everything that belongs to it (base texture, shared mask, ramps).
        /// Use this from scripts; the menu path uses <see cref="Convert"/>.
        /// </summary>
        public static Material ConvertOne(Material source, string outputFolder, string preferredName, ConversionLog log)
        {
            return ConvertOne(source, outputFolder, preferredName, log, true, true, null);
        }

        public static Material ConvertOne(Material source, string outputFolder, string preferredName, ConversionLog log,
            bool bakeBaseTexture, bool bakeSharedMask, object locks)
        {
            var material = ConvertMaterial(source, outputFolder, preferredName, locks,
                bakeSharedMask, bakeBaseTexture, log);
            PostProcess(log);
            ApplyGradients(log, string.IsNullOrEmpty(log.Folder) ? outputFolder : log.Folder, NonToonSwitcherSettings.instance.BakeGradients);
            return material;
        }

        private static string MakeUniqueAssetPath(string desiredPath, Material source, object locks)
        {
            var folderLocks = locks as FolderLocks;
            if (folderLocks == null) return desiredPath;
            var owner = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(owner)) owner = "instance:" + source.GetInstanceID();
            folderLocks.Owner.TryGetValue(desiredPath, out var existingOwner);

            var candidate = desiredPath;
            var index = 2;
            while (existingOwner != null && !string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.GetDirectoryName(desiredPath).Replace('\\', '/') + "/" +
                            Path.GetFileNameWithoutExtension(desiredPath) + "_" + index + ".mat";
                index++;
                folderLocks.Owner.TryGetValue(candidate, out existingOwner);
            }

            folderLocks.Owner[candidate] = owner;
            return candidate;
        }

        /// <summary>Properties produced by the explicit passes; the automatic passes must not overwrite them.</summary>
        private static readonly HashSet<string> ReservedTargets = new HashSet<string>(StringComparer.Ordinal)
        {
            "_RenderingMode", "_SrcBlend", "_DstBlend", "_SrcBlendAlpha", "_DstBlendAlpha", "_AlphaToMask",
            "_OutlineFromVertexColor", "_SharedMask", "_SharedGradients", "_NTDitherTex",
        };

        private static void ApplyMappings(Material source, Material target, ConversionLog log)
        {
            var handled = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in NonToonPropertyMap.AllEntries)
            {
                if (!ShaderUtility.HasProperty(source, entry.From)) continue;
                try
                {
                    entry.Copy(source, target, log);
                }
                catch (Exception exception)
                {
                    log.Warn("属性 " + entry.From + " -> " + entry.To + " 转换出错：" + exception.Message);
                }
                handled.Add(entry.From);
            }

            // Pass 2: identical property names.
            var fuzzy = NonToonPropertyMap.BuildFuzzyTargets(target.shader);
            var count = source.shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = source.shader.GetPropertyName(i);
                if (string.IsNullOrEmpty(name)) continue;
                if (handled.Contains(name)) continue;
                if (name.StartsWith("_Dummy", StringComparison.Ordinal)) continue;

                if (ShaderUtility.HasProperty(target, name))
                {
                    // Never overwrite what the explicit table or the render-state pass produced.
                    if (ReservedTargets.Contains(name) || IsTouchedByMap(name)) continue;
                    if (ShaderUtility.CopyProperty(source, target, name)) log.Mapped(name, name);
                    handled.Add(name);
                }
            }

            // Pass 3: renamed properties (same type, normalised name).
            for (var i = 0; i < count; i++)
            {
                var name = source.shader.GetPropertyName(i);
                if (string.IsNullOrEmpty(name)) continue;
                if (handled.Contains(name)) continue;
                if (name.StartsWith("_Dummy", StringComparison.Ordinal)) continue;
                if (ShaderUtility.HasProperty(target, name)) continue;

                var normalized = NonToonPropertyMap.NormalizeForFuzzy(name);
                if (!fuzzy.TryGetValue(normalized, out var destinationName)) continue;
                if (ReservedTargets.Contains(destinationName) || IsTouchedByMap(destinationName)) continue;
                if (ShaderUtility.PropertyType(source.shader, name) != ShaderUtility.PropertyType(target.shader, destinationName)) continue;
                if (ShaderUtility.CopyProperty(source, target, destinationName))
                {
                    log.Mapped(name, destinationName);
                    handled.Add(name);
                }
            }
        }

        private static bool IsTouchedByMap(string propertyName)
        {
            foreach (var entry in NonToonPropertyMap.AllEntries)
            {
                if (entry.To == propertyName) return true;
            }
            return false;
        }

        /// <summary>
        /// Finds a Shader Core "gradient select" property of a module. The module part is matched with the
        /// leading underscore so that the Shade module ("_..._shade_") cannot match the RimShade module
        /// ("_..._rimshade_").
        /// </summary>
        private static string FindGradientIndexProperty(Shader shader, string modulePart, string propertySuffix)
        {
            if (shader == null) return null;
            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                if (!name.EndsWith("_" + modulePart + propertySuffix, StringComparison.Ordinal)) continue;
                return name;
            }
            return null;
        }

        /// <summary>
        /// lilToon 把渲染模式写在两层里：
        ///   · 主 shader（lilToon）用材质属性 `_TransparentMode`（0 不透明 / 1 镂空 / 2 透明 / 3 折射 / 4 毛 / 5 毛镂空 / 6 宝石）；
        ///   · 但 lilToon 的 Inspector 一旦选了模式，材质就会被换成对应的隐藏变体
        ///     （`Hidden/lilToonCutout`、`Hidden/lilToonTransparentOutline`、`Hidden/lilToonTwoPassTransparent` …），
        ///     那些材质的 `_TransparentMode` 往往还是 0，模式只能从 shader 名字里读出来。
        /// 所以先看名字，再看属性 —— 之前只看属性，导致"原来是镂空/透明的材质转完变成不透明"。
        /// </summary>
        private static int DetectLilToonMode(Material source)
        {
            var shaderName = (source.shader != null ? source.shader.name : string.Empty).ToLowerInvariant();

            if (shaderName.Contains("twopasstransparent")) return 2;
            if (shaderName.Contains("furcutout")) return 5;
            if (shaderName.Contains("cutout")) return 1;
            if (shaderName.Contains("transparent") || shaderName.Contains("trans")) return 2;
            if (shaderName.Contains("gem")) return 6;
            if (shaderName.Contains("fur")) return 4;

            // 主 shader / 只有描边变体时，模式还在属性里
            if (ShaderUtility.HasProperty(source, "_TransparentMode"))
                return Mathf.RoundToInt(source.GetFloat("_TransparentMode"));

            return 0;
        }

        /// <summary>
        /// lilToon 的混合方式 / ZWrite / Cull / AlphaToMask 都是"材质驱动"的
        /// （lilToon 的 pass 里写的是 Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]、
        /// ZWrite [_ZWrite]、Cull [_Cull]、AlphaToMask [_AlphaToMask]，渲染队列也能被材质覆盖），
        /// 所以这些值必须原样搬到 NonToon 上。否则作者特意调过的材质 —— 例如"透明混合但仍然写深度、
        /// 还待在几何队列里"的脸和头发 —— 会被改成"不写深度 + 排到透明队列"，
        /// 表现就是该实心的地方透了、前后遮挡关系也乱了。
        /// </summary>
        private static readonly string[] CopiedRenderStates =
        {
            "_Cull", "_SrcBlend", "_DstBlend", "_SrcBlendAlpha", "_DstBlendAlpha", "_ZWrite", "_AlphaToMask",
        };

        /// <summary>lilToon stores the rendering mode in the shader variant; NonToon stores it in a property.</summary>
        private static void ApplyRenderingMode(Material source, Material target, ConversionLog log)
        {
            var mode = DetectLilToonMode(source);

            var resolved = 0;
            switch (mode)
            {
                case 1: case 5: resolved = 1; break;         // cutout / fur cutout
                case 2: case 3: case 6: resolved = 2; break; // transparent / refraction / gem
                default: resolved = 0; break;                // opaque, fur
            }

            if (mode == 3)
                log.Warn("lilToon 的折射（Refraction）在 NonToon 里没有对应实现，已按透明处理。");
            else if (mode == 4 || mode == 5 || mode == 6)
                log.Unsupported("lilToon 的 " + ModeName(mode) + " 渲染模式（已按" +
                                (resolved == 0 ? "不透明" : "镂空") + "近似处理）");

            if (!ShaderUtility.HasProperty(target, "_RenderingMode"))
            {
                log.Warn("当前 NonToon 版本没有 _RenderingMode 属性（例如 NonToonFur），渲染模式未做修改。");
                return;
            }

            // _RenderingMode 只决定 NonToon 怎么处理 alpha（不透明强制 1 / 镂空剪切 / 透明保留），
            // 具体怎么混合、写不写深度由下面的材质属性决定。
            ShaderUtility.SetIntValue(target, "_RenderingMode", resolved);

            // 先用 NonToon 自己那套模式默认值打底（和它的渲染模式下拉框完全一致），
            // 源材质没有对应属性时就用这套值。
            ApplyModeDefaults(target, resolved);

            // 再用源材质的渲染状态覆盖：lilToon 里这些值就是用户/作者调过的真实值。
            var copied = CopyRenderStates(source, target);
            target.renderQueue = ResolveRenderQueue(source, resolved);

            log.Mapped("rendering mode " + ModeName(mode) + "（按 shader 名判断：" + source.shader.name + "）",
                "NonToon " + RenderingModeName(resolved));
            log.Mapped("渲染状态（沿用原材质）", copied + "，队列 " +
                (target.renderQueue >= 0 ? target.renderQueue.ToString() : "默认"));

            if (ShaderUtility.HasProperty(source, "_AlphaMaskMode") && source.GetFloat("_AlphaMaskMode") != 0f && resolved == 0)
            {
                log.Warn("lilToon 用了透明遮罩，但材质是 Opaque；NonToon 只在 Cutout / Transparent 模式下应用透明遮罩。");
            }
        }

        /// <summary>The values NonToon's own rendering-mode dropdown writes (Editor/RenderingModeElement.cs).</summary>
        private static void ApplyModeDefaults(Material target, int resolved)
        {
            switch (resolved)
            {
                case 1:
                    ShaderUtility.SetIntValue(target, "_SrcBlend", (int)BlendMode.One);
                    ShaderUtility.SetIntValue(target, "_DstBlend", (int)BlendMode.Zero);
                    var dither = ShaderUtility.HasProperty(target, "_NTDitherTex") ? target.GetTexture("_NTDitherTex") : null;
                    ShaderUtility.SetIntValue(target, "_AlphaToMask", dither != null ? 0 : 1);
                    ShaderUtility.SetIntValue(target, "_ZWrite", 1);
                    break;
                case 2:
                    ShaderUtility.SetIntValue(target, "_SrcBlend", (int)BlendMode.SrcAlpha);
                    ShaderUtility.SetIntValue(target, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    ShaderUtility.SetIntValue(target, "_AlphaToMask", 0);
                    ShaderUtility.SetIntValue(target, "_ZWrite", 0);
                    break;
                default:
                    ShaderUtility.SetIntValue(target, "_SrcBlend", (int)BlendMode.One);
                    ShaderUtility.SetIntValue(target, "_DstBlend", (int)BlendMode.Zero);
                    ShaderUtility.SetIntValue(target, "_AlphaToMask", 0);
                    ShaderUtility.SetIntValue(target, "_ZWrite", 1);
                    break;
            }
        }

        /// <summary>Copies lilToon's material-driven render states and returns them for the report.</summary>
        private static string CopyRenderStates(Material source, Material target)
        {
            var parts = new List<string>();
            foreach (var name in CopiedRenderStates)
            {
                if (!ShaderUtility.HasProperty(source, name)) continue;
                if (!ShaderUtility.HasProperty(target, name)) continue;
                if (!ShaderUtility.CopyProperty(source, target, name)) continue;
                parts.Add(name.Substring(1) + "=" + ReadSourceInt(source, name));
            }
            return parts.Count > 0 ? string.Join("、", parts.ToArray()) : "无可沿用项";
        }

        /// <summary>
        /// 读原材质的整数值（只用于写日志）。材质变体（m_Parent 指向父材质）的属性是从父材质继承来的，
        /// 序列化列表里没有条目，这时要让 Unity 通过 Material.GetInt 把继承值解析出来。
        /// </summary>
        private static int ReadSourceInt(Material source, string name)
        {
            var value = ShaderUtility.ReadSerializedInt(source, name, int.MinValue);
            if (value != int.MinValue) return value;
            try { return source.GetInt(name); }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// 渲染队列：原材质自己设过就照搬；否则用原 shader 声明的队列（lilToon 的透明变体是 2460、
        /// 镂空 2450、不透明 2000、宝石 2900）；两者都没有才退回 NonToon 的模式默认值。
        /// </summary>
        private static int ResolveRenderQueue(Material source, int resolved)
        {
            if (source.renderQueue >= 0) return source.renderQueue;
            if (source.shader != null && source.shader.renderQueue >= 0) return source.shader.renderQueue;

            switch (resolved)
            {
                case 1: return 2450;
                case 2: return GraphicsSettings.currentRenderPipeline != null ? 3000 : 2460;
                default: return -1;
            }
        }

        private static string ModeName(int lilToonTransparentMode)
        {
            switch (lilToonTransparentMode)
            {
                case 1: return "Cutout";
                case 2: return "Transparent";
                case 3: return "Refraction";
                case 4: return "Fur";
                case 5: return "FurCutout";
                case 6: return "Gem";
                default: return "Opaque";
            }
        }

        private static string RenderingModeName(int mode)
        {
            switch (mode)
            {
                case 1: return "Cutout";
                case 2: return "Transparent";
                default: return "Opaque";
            }
        }

        /// <summary>
        /// lilToon 的描边宽度贴图（`_OutlineWidthMask`）在 NonToon 里没有对应功能，而两者差别很大：
        /// 作者常用它把嘴唇、眼睛这些地方的描边宽度压成 0（描边壳不该出现在五官上），
        /// 但 NonToon 的描边是**均匀**的反向外扩壳，于是嘴腔内壁的外扩壳会照样画出来，
        /// 而且它采样的是该处 UV 的贴图颜色 —— 看上去就像脸在嘴部被"撕破"。
        ///
        /// 处理办法：把描边整体沿视线方向后移一个自身宽度（`_OutlineZOffset`，NonToon 的
        /// outline 顶点代码是 `pos += N * _OutlineWidth * 0.01 - V * _OutlineZOffset`，
        /// 正值就是往后推）。这样描边壳在跟脸面自身重叠的地方会被深度测试剔除，
        /// 剪影处的描边依旧保留。
        /// </summary>
        private static void ApplyOutlineWidthMaskWorkaround(Material source, Material target, ConversionLog log)
        {
            if (target == null) return;
            if (!ShaderUtility.HasProperty(target, "_OutlineZOffset")) return;
            if (!ShaderUtility.HasProperty(source, "_OutlineWidthMask")) return;
            if (source.GetTexture("_OutlineWidthMask") == null) return;
            if (!ShaderUtility.HasProperty(target, "_OutlineWidth")) return;

            var width = target.GetFloat("_OutlineWidth");
            if (width <= 0f) return;

            // 后移倍数是项目设置（默认 1 = 与描边自身宽度同量级），0 表示不做处理。
            var factor = NonToonSwitcherSettings.instance.OutlineZOffsetFactor;
            var offset = width * 0.01f * factor;
            if (offset <= 0f)
            {
                log.Mapped("描边宽度贴图（NonToon 没有这个功能）", "后移倍数 = 0，未做处理（描边可能盖住嘴唇 / 眼睛）");
                return;
            }

            var current = ShaderUtility.HasProperty(target, "_OutlineZOffset") ? target.GetFloat("_OutlineZOffset") : 0f;
            if (current >= offset) return;

            ShaderUtility.SetFloatValue(target, "_OutlineZOffset", offset);
            log.Mapped("描边宽度贴图（NonToon 没有这个功能）",
                "描边整体后移 " + offset.ToString("0.#####") + "（_OutlineZOffset，倍数 " +
                factor.ToString("0.##") + " × 描边宽度），避免描边盖住嘴唇 / 眼睛");
        }

        private static void ApplyVertexColorFlags(Material source, Material target, ConversionLog log)
        {
            // lilToon 2.x: 1 = 0/1 mask from vertex colour R, 2/3 = width from vertex colour R.
            var flag = 0;
            if (ShaderUtility.HasProperty(source, "_OutlineVertexR2Width"))
                flag = Mathf.RoundToInt(source.GetFloat("_OutlineVertexR2Width"));

            if (flag != 0x02 && ShaderUtility.HasProperty(source, "_VertexColor2Normal"))
            {
                // lilToon 1.x kept flags in a bitfield.
                var bits = Mathf.RoundToInt(source.GetFloat("_VertexColor2Normal"));
                if ((bits & 0x02) != 0) flag = 2;
            }

            if (flag != 0) ShaderUtility.SetIntValue(target, "_OutlineFromVertexColor", 1);
        }

        // ------------------------------------------------------------------ the whole flow

        public static ConversionResult Convert(ConvertRequest request)
        {
            var result = new ConversionResult { OutputFolder = request.OutputFolder };
            var settings = NonToonSwitcherSettings.instance;

            if (request.Targets == null || request.Targets.Length == 0)
            {
                result.Error("没有选中任何对象。请先选中要转换的 avatar / 衣服对象。");
                return result;
            }

            var shader = ShaderUtility.FindNonToonShader();
            if (shader == null)
            {
                result.Error("找不到 NonToon shader，请先安装 jp.lilxyzw.nontoon 与 jp.lilxyzw.shadercore 再转换。");
                return result;
            }

            if (!ShaderUtility.IsLilToonAvailable)
                result.Warn("找不到 lilToon shader，仍在用 lilToon 的材质无法被识别；" +
                            "如果不是有意为之，请安装 jp.lilxyzw.liltoon。");

            // 1. find everything that has to be converted
            var renderers = new List<Renderer>();
            foreach (var target in request.Targets) CollectRenderers(target, request.IncludeInactive, renderers);

            var materials = new List<Material>();
            foreach (var renderer in renderers)
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material != null && !materials.Contains(material)) materials.Add(material);
                }
            }

            var animatorMaterials = new List<Material>();
            foreach (var target in request.Targets) CollectAnimatorMaterials(target, request.IncludeInactive, animatorMaterials);

            var lilToonMaterials = new List<Material>();
            foreach (var material in materials)
            {
                if (ShaderUtility.IsLilToon(material.shader)) lilToonMaterials.Add(material);
                else result.NotConverted.Add(material.name + " (" + (material.shader != null ? material.shader.name : "no shader") + ")");
            }

            var animatorLilToon = new List<Material>();
            foreach (var material in animatorMaterials)
            {
                if (material != null && ShaderUtility.IsLilToon(material.shader) && !lilToonMaterials.Contains(material))
                    animatorLilToon.Add(material);
            }

            if (lilToonMaterials.Count == 0 && animatorLilToon.Count == 0)
            {
                result.Error("选中对象里没有找到 lilToon 材质。");
                return result;
            }

            // 2. make sure the output folder exists（连同所有缺失的父级一起创建）
            var folder = ShaderUtility.ToAssetPath(request.OutputFolder).TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(folder))
            {
                if (!ShaderUtility.TryCreateFolderRecursive(folder))
                {
                    result.Error("无法创建输出文件夹 " + folder + "。请确认它位于 Assets 目录下。");
                    return result;
                }
            }

            // Phase 1: create / update the NonToon materials. Asset paths are decided up front so that two
            // source materials with the same name never overwrite each other.
            var locks = CreateNameLocks();
            var converted = new Dictionary<Material, Material>();
            var logs = new List<ConversionLog>();
            var allSources = new List<Material>(lilToonMaterials);
            foreach (var material in animatorLilToon) allSources.Add(material);

            var paths = new Dictionary<Material, string>();
            foreach (var material in allSources)
            {
                paths[material] = MakeUniqueAssetPath(folder + "/" + OutputName(material.name), material, locks);
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var material in allSources)
                {
                    var log = new ConversionLog { Source = material };
                    ConvertMaterial(material, folder, material.name, locks,
                        request.BakeSharedMask, request.BakeBaseTexture, log, paths[material]);
                    logs.Add(log);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // Phase 2: bake textures and gradient arrays. These are real imported assets, so they must be
            // produced outside of the asset-editing block above.
            foreach (var log in logs) PostProcess(log);

            // Imports during phase 2 can replace the in-memory material objects, so collect the final ones.
            foreach (var log in logs)
            {
                if (log.Source != null && log.Destination != null) converted[log.Source] = log.Destination;
            }

            foreach (var log in logs) ApplyGradients(log, folder, request.BakeGradients);

            // Gradient baking reloads the materials once more, so refresh the collected assets.
            foreach (var log in logs)
            {
                if (log.Source != null && log.Destination != null) converted[log.Source] = log.Destination;
            }

            foreach (var log in logs) result.Logs.Add(log);

            if (converted.Count == 0)
            {
                result.Error("没有任何材质转换成功。");
                return result;
            }

            // 3. swap the materials on the selection and build the toggle object.
            // The renderer assignments are captured first: the switch entries need the original lilToon
            // material and its slot, and that information is gone once the renderers have been swapped.
            var assignments = CaptureAssignments(renderers, converted);

            // 「复制一份再换」：把选中的对象复制成 <名字>_nontoon，材质换在副本上，
            // 原来那份原样保留（只是取消勾选），随时可以勾回来回退。
            if (request.ReplaceMode == ReplaceMode.DuplicateThenReplace)
            {
                var duplicated = DuplicateAndReplace(request.Targets, assignments, result);
                if (duplicated)
                {
                    result.ReplacedOnRenderers = true;
                    foreach (var target in request.Targets)
                    {
                        if (target != null) EditorUtility.SetDirty(target);
                    }
                    EditorSceneManager_Helper.MarkDirty();
                    AssetDatabase.SaveAssets();

                    if (settings.LogToConsole)
                    {
                        var duplicateText = result.BuildText();
                        if (result.Errors.Count > 0) Debug.LogError(duplicateText);
                        else if (result.Warnings.Count > 0) Debug.LogWarning(duplicateText);
                        else Debug.Log(duplicateText);
                    }

                    return result;
                }

                // 复制失败就退回"直接替换原对象"，不要什么都不做。
                result.Warn("复制对象失败，已改为直接替换原对象上的材质。");
            }

            // In Material Setter mode the renderers keep their original materials unless the user asks for
            // the swap, because the toggle is what turns NonToon on.
            var replaceOnRenderers = request.ReplaceMaterialsOnRenderers ||
                                     request.ReplaceMode != ReplaceMode.Switch ||
                                     (request.ApplyToSelectionImmediately &&
                                      request.SwitcherMode == SwitcherMode.MaterialSwap) ||
                                     !request.CreateSwitcher;

            if (replaceOnRenderers)
            {
                ApplyMaterials(assignments);
                result.ReplacedOnRenderers = true;
            }

            if (request.CreateSwitcher)
            {
                var pairs = BuildSwitchPairs(assignments, converted, request.CollectAnimatorMaterials);

                var parent = FindSwitcherParent(request.Targets);
                var swapRoot = FindSwapRoot(request.Targets);

                // 同一个 avatar 下已经转换过一次时，把新材质追加到已有的开关里，
                // 而不是再建一个（否则转几次就会出现几个开关）。
                var reused = false;
                var existing = request.ReuseExistingSwitcher
                    ? FindExistingSwitcher(request.Targets, parent)
                    : null;
                if (existing != null && request.SwitcherMode == SwitcherMode.MaterialSetter)
                {
                    var added = NonToonSwitcherBuilder.AppendToMaterialSetter(existing, pairs,
                        request.CreateMenuToggle, request.MenuParameter, result);
                    if (added >= 0)
                    {
                        result.SwitchObject = existing;
                        result.SwitchReused = true;
                        result.SwitchEntryCount = added;
                        if (existing.transform.parent != null) EditorUtility.SetDirty(existing.transform.parent);
                        EditorUtility.SetDirty(existing);
                        reused = true;
                    }
                    else
                    {
                        result.Warnings.RemoveAll(w => w.Contains("已改为新建"));
                    }
                }

                if (!reused)
                {
                    var switcher = NonToonSwitcherBuilder.Build(parent, swapRoot, pairs,
                        request.CreateMenuToggle, request.MenuParameter, request.MenuLabel,
                        request.NonToonOnByDefault, request.SwitcherMode, result);
                    if (switcher != null)
                    {
                        result.SwitchEntryCount = pairs.Count;
                        if (parent != null) EditorUtility.SetDirty(parent);
                    }
                }
            }

            foreach (var target in request.Targets)
            {
                if (target != null) EditorUtility.SetDirty(target);
            }

            EditorSceneManager_Helper.MarkDirty();
            AssetDatabase.SaveAssets();

            if (settings.LogToConsole)
            {
                var text = result.BuildText();
                if (result.Errors.Count > 0) Debug.LogError(text);
                else if (result.Warnings.Count > 0) Debug.LogWarning(text);
                else Debug.Log(text);
            }

            return result;
        }

        /// <summary>One converted material slot: which renderer, which slot, and what it should become.</summary>
        private sealed class MaterialAssignment
        {
            public Renderer Renderer;
            public int Index;
            public Material Original;
            public Material Converted;
        }

        /// <summary>
        /// Records every renderer slot that has a converted counterpart, before anything is swapped.
        /// </summary>
        private static List<MaterialAssignment> CaptureAssignments(List<Renderer> renderers,
            Dictionary<Material, Material> converted)
        {
            var assignments = new List<MaterialAssignment>();
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null) continue;
                    if (!converted.TryGetValue(material, out var replacement)) continue;
                    assignments.Add(new MaterialAssignment
                    {
                        Renderer = renderer,
                        Index = i,
                        Original = material,
                        Converted = replacement,
                    });
                }
            }
            return assignments;
        }

        /// <summary>
        /// 「复制一份再换」：把每个选中对象复制成 <名字>_nontoon，材质换在副本上，
        /// 原来那份保留原样但取消勾选（Active = false），想回退就把它勾回来。
        /// 副本里会先删掉我们之前生成的 <see cref="SwitcherObjectName"/>（否则副本里既有 NonToon 材质、
        /// 又有会切回 lilToon 的开关，互相打架）。
        /// </summary>
        private static bool DuplicateAndReplace(GameObject[] targets, List<MaterialAssignment> assignments,
            ConversionResult result)
        {
            if (targets == null || targets.Length == 0) return false;

            var ok = false;
            foreach (var target in targets)
            {
                if (target == null || target.transform == null) continue;

                var duplicate = FindExistingDuplicate(target) ?? DuplicateObject(target, result);
                if (duplicate == null) continue;

                RemoveGeneratedSwitchers(duplicate);
                if (!duplicate.activeSelf) duplicate.SetActive(true);

                ApplyMaterials(RemapToDuplicate(assignments, target, duplicate, result));

                Undo.RecordObject(target, "停用原对象（NonToon 副本已生成）");
                target.SetActive(false);
                EditorUtility.SetDirty(target);

                result.DuplicatedObjects.Add(duplicate.name + "（原对象 " + target.name + " 已取消勾选）");
                ok = true;
            }

            return ok;
        }

        /// <summary>复制对象本体，名字加 <see cref="DuplicateSuffix"/>，并排在原对象后面。</summary>
        private static GameObject DuplicateObject(GameObject target, ConversionResult result)
        {
            var duplicate = UnityEngine.Object.Instantiate(target);
            duplicate.name = target.name + DuplicateSuffix;
            duplicate.transform.SetParent(target.transform.parent, false);
            duplicate.transform.SetSiblingIndex(target.transform.GetSiblingIndex() + 1);
            Undo.RegisterCreatedObjectUndo(duplicate, "复制为 " + DuplicateSuffix);
            result.CreatedObjects.Add(duplicate);
            return duplicate;
        }

        /// <summary>同一个父对象下已经有一个同名副本时直接复用它（重复转换不会越堆越多）。</summary>
        private static GameObject FindExistingDuplicate(GameObject target)
        {
            var parent = target.transform.parent;
            var wanted = target.name + DuplicateSuffix;
            if (parent == null)
            {
                foreach (var root in target.scene.GetRootGameObjects())
                {
                    if (root != target && root.name == wanted) return root;
                }
                return null;
            }

            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child != target.transform && child.name == wanted) return child.gameObject;
            }
            return null;
        }

        /// <summary>副本里删掉我们生成的切换开关（连同它下面的 MA 组件一起）。</summary>
        private static void RemoveGeneratedSwitchers(GameObject duplicate)
        {
            var containers = new List<GameObject>();
            foreach (var transform in duplicate.GetComponentsInChildren<Transform>(true))
            {
                if (transform != null && transform.name == SwitcherObjectName) containers.Add(transform.gameObject);
            }
            foreach (var container in containers)
            {
                if (container == null) continue;
                Undo.DestroyObjectImmediate(container);
            }
        }

        /// <summary>
        /// 把「原对象上的渲染器槽位」翻译成「副本里对应的渲染器槽位」。
        /// 副本是 Instantiate 出来的，层级结构一致，所以按相对路径找即可。
        /// </summary>
        private static List<MaterialAssignment> RemapToDuplicate(List<MaterialAssignment> assignments,
            GameObject originalRoot, GameObject duplicateRoot, ConversionResult result)
        {
            var remapped = new List<MaterialAssignment>();
            foreach (var assignment in assignments)
            {
                if (assignment.Renderer == null) continue;
                if (!IsUnder(assignment.Renderer.transform, originalRoot.transform)) continue;

                var relative = RelativePath(originalRoot.transform, assignment.Renderer.transform);
                var copy = string.IsNullOrEmpty(relative)
                    ? duplicateRoot.transform
                    : duplicateRoot.transform.Find(relative);
                if (copy == null)
                {
                    result.Warn("副本里找不到对应的渲染器：" + relative + "（这个槽位没有替换）");
                    continue;
                }

                var copyRenderer = copy.GetComponent(assignment.Renderer.GetType()) as Renderer;
                if (copyRenderer == null)
                {
                    result.Warn("副本里的 " + relative + " 没有同类渲染器组件（这个槽位没有替换）");
                    continue;
                }

                remapped.Add(new MaterialAssignment
                {
                    Renderer = copyRenderer,
                    Index = assignment.Index,
                    Original = assignment.Original,
                    Converted = assignment.Converted,
                });
            }
            return remapped;
        }

        private static bool IsUnder(Transform transform, Transform root)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                if (current == root) return true;
            }
            return false;
        }

        /// <summary>相对根对象的层级路径（用于在副本里找同一个渲染器）。</summary>
        private static string RelativePath(Transform root, Transform target)
        {
            if (root == null || target == null || root == target) return string.Empty;
            var parts = new List<string>();
            for (var current = target; current != null && current != root; current = current.parent)
            {
                parts.Add(current.name);
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        /// <summary>Applies the recorded assignments to the renderers.</summary>
        private static void ApplyMaterials(List<MaterialAssignment> assignments)
        {
            foreach (var group in assignments.GroupBy(a => a.Renderer))
            {
                var renderer = group.Key;
                if (renderer == null) continue;

                Undo.RecordObject(renderer, "将材质转换为 NonToon");
                var materials = renderer.sharedMaterials;
                var changed = false;
                foreach (var assignment in group)
                {
                    if (assignment.Index < 0 || assignment.Index >= materials.Length) continue;
                    materials[assignment.Index] = assignment.Converted;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
        }

        /// <summary>
        /// Builds the switch entries. The MA Material Setter needs the renderer and slot for every entry;
        /// MA Material Swap only needs the material pairs.
        /// </summary>
        private static List<SwitchPair> BuildSwitchPairs(List<MaterialAssignment> assignments,
            Dictionary<Material, Material> converted, bool includeAnimatorMaterials)
        {
            var pairs = new List<SwitchPair>();
            foreach (var assignment in assignments)
            {
                pairs.Add(new SwitchPair
                {
                    Original = assignment.Original,
                    Converted = assignment.Converted,
                    Renderer = assignment.Renderer,
                    MaterialIndex = assignment.Index,
                });
            }

            // Materials that only appear inside animation clips cannot be addressed per renderer slot.
            if (!includeAnimatorMaterials) return pairs;

            foreach (var pair in converted)
            {
                var covered = false;
                foreach (var existing in pairs)
                {
                    if (existing.Original == pair.Key || existing.Converted == pair.Value)
                    {
                        covered = true;
                        break;
                    }
                }
                if (covered) continue;
                pairs.Add(new SwitchPair { Original = pair.Key, Converted = pair.Value, Renderer = null, MaterialIndex = 0 });
            }

            return pairs;
        }

        /// <summary>Where the switch object is placed: as far up as possible while staying inside the avatar.</summary>
        private static GameObject FindSwitcherParent(GameObject[] targets)
        {
            GameObject common = null;
            foreach (var target in targets)
            {
                if (target == null) continue;
                common = common == null ? target : FindCommonAncestor(common, target);
            }
            if (common == null) return null;

            // 放在 avatar 根节点下：MA 的菜单安装器需要位于 avatar 内部，而 Material Setter 是按对象
            // 逐条记录的，放在根节点不会扩大作用范围。
            var avatarRoot = FindAvatarRoot(common);
            if (avatarRoot != null) return avatarRoot;

            // 没有 avatar 时不要把开关塞进选中对象内部：挂到公共祖先的父级（或场景根），
            // 这样即使之后再转同一个模型的其他部分，也能找到并复用同一个开关。
            var parent = common.transform.parent;
            return parent != null ? parent.gameObject : null;
        }

        /// <summary>
        /// 查找同一个 avatar 下已经存在的切换对象，避免每转换一次就新建一个开关。
        /// </summary>
        private static GameObject FindExistingSwitcher(GameObject[] targets, GameObject parent)
        {
            var avatarRoot = parent != null ? FindAvatarRoot(parent) : null;

            // 优先找同一 avatar 下的
            if (avatarRoot != null)
            {
                foreach (var child in avatarRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (child != null && child.name == SwitcherObjectName) return child.gameObject;
                }
            }

            // 没有 avatar 时，在同一个父节点（或同一场景的根节点）下找
            if (parent != null)
            {
                for (var i = 0; i < parent.transform.childCount; i++)
                {
                    var child = parent.transform.GetChild(i);
                    if (child != null && child.name == SwitcherObjectName) return child.gameObject;
                }
                return null;
            }

            var targets2 = targets ?? new GameObject[0];
            var scene = targets2.Length > 0 && targets2[0] != null
                ? targets2[0].scene
                : UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root != null && root.name == SwitcherObjectName) return root;
            }
            return null;
        }

        /// <summary>The renderers below this object are the ones the material swap touches.</summary>
        private static GameObject FindSwapRoot(GameObject[] targets)
        {
            GameObject common = null;
            foreach (var target in targets)
            {
                if (target == null) continue;
                common = common == null ? target : FindCommonAncestor(common, target);
            }
            return common;
        }

        public static GameObject FindCommonAncestor(GameObject a, GameObject b)
        {
            if (a == null) return b;
            if (b == null) return a;
            var ancestors = new HashSet<Transform>();
            for (var t = a.transform; t != null; t = t.parent) ancestors.Add(t);
            for (var t = b.transform; t != null; t = t.parent)
            {
                if (ancestors.Contains(t)) return t.gameObject;
            }
            return a;
        }

        /// <summary>Walks up until a VRChat avatar descriptor is found (the object MA treats as the avatar root).</summary>
        public static GameObject FindAvatarRoot(GameObject start)
        {
            if (start == null) return null;
            var current = start.transform;
            while (current != null)
            {
                foreach (var component in current.GetComponents<Component>())
                {
                    if (component == null) continue;
                    var type = component.GetType();
                    if (type.FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") return current.gameObject;
                    if (type.FullName == "nadena.dev.ndmf.runtime.components.NDMFBuildInfo") return current.gameObject;
                }
                current = current.parent;
            }
            return null;
        }
    }

    public static class EditorSceneManager_Helper
    {
        public static void MarkDirty()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
