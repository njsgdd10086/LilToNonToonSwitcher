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
        /// <summary>Also convert materials that are only referenced by animation clips.</summary>
        public bool CollectAnimatorMaterials = true;
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

        /// <summary>lilToon stores the rendering mode in the shader variant; NonToon stores it in a property.</summary>
        private static void ApplyRenderingMode(Material source, Material target, ConversionLog log)
        {
            var mode = 0; // 0 opaque, 1 cutout, 2 transparent
            var shaderName = source.shader.name ?? string.Empty;

            if (ShaderUtility.HasProperty(source, "_TransparentMode"))
                mode = Mathf.RoundToInt(source.GetFloat("_TransparentMode"));
            else if (shaderName.Contains("cutout")) mode = 1;
            else if (shaderName.Contains("trans")) mode = 2;

            var resolved = 0;
            switch (mode)
            {
                case 1: resolved = 1; break;
                case 2: case 3: resolved = 2; break;   // transparent / refraction
                default: resolved = 0; break;          // opaque, fur, gem
            }

            if (mode == 4 || mode == 5 || mode == 6)
                log.Unsupported("lilToon 的 " + ModeName(mode) + " 渲染模式（已按 Opaque 近似处理）");

            if (!ShaderUtility.HasProperty(target, "_RenderingMode"))
            {
                log.Warn("当前 NonToon 版本没有 _RenderingMode 属性，渲染模式未做修改。");
                return;
            }

            ShaderUtility.SetIntValue(target, "_RenderingMode", resolved);

            // Mirror what NonToon's own inspector does when the mode changes. The render queue values are the
            // canonical ones from NonToon's rendering mode popup.
            switch (resolved)
            {
                case 0:
                    ShaderUtility.SetIntValue(target, "_SrcBlend", (int)BlendMode.One);
                    ShaderUtility.SetIntValue(target, "_DstBlend", (int)BlendMode.Zero);
                    ShaderUtility.SetIntValue(target, "_AlphaToMask", 0);
                    target.renderQueue = -1;
                    break;
                case 1:
                    ShaderUtility.SetIntValue(target, "_SrcBlend", (int)BlendMode.One);
                    ShaderUtility.SetIntValue(target, "_DstBlend", (int)BlendMode.Zero);
                    var dither = ShaderUtility.HasProperty(target, "_NTDitherTex") ? target.GetTexture("_NTDitherTex") : null;
                    ShaderUtility.SetIntValue(target, "_AlphaToMask", dither != null ? 0 : 1);
                    target.renderQueue = 2450;
                    break;
                default:
                    ShaderUtility.SetIntValue(target, "_SrcBlend", (int)BlendMode.SrcAlpha);
                    ShaderUtility.SetIntValue(target, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    ShaderUtility.SetIntValue(target, "_AlphaToMask", 0);
                    target.renderQueue = 2460;
                    break;
            }

            log.Mapped("rendering mode " + ModeName(mode), "NonToon " + RenderingModeName(resolved));

            if (ShaderUtility.HasProperty(source, "_AlphaMaskMode") && source.GetFloat("_AlphaMaskMode") != 0f && resolved == 0)
            {
                log.Warn("lilToon 用了透明遮罩，但材质是 Opaque；NonToon 只在 Cutout / Transparent 模式下应用透明遮罩。");
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

            // 2. make sure the output folder exists
            var folder = ShaderUtility.ToAssetPath(request.OutputFolder).TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(folder))
            {
                var parent = Path.GetDirectoryName(folder);
                if (string.IsNullOrEmpty(parent)) parent = "Assets";
                parent = parent.Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(parent))
                {
                    result.Error("输出文件夹 " + folder + " 不存在，且找不到它的上级目录。");
                    return result;
                }
                AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
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

            // In Material Setter mode the renderers keep their original materials unless the user asks for
            // the swap, because the toggle is what turns NonToon on.
            var replaceOnRenderers = request.ReplaceMaterialsOnRenderers ||
                                     (request.ApplyToSelectionImmediately &&
                                      request.SwitcherMode == SwitcherMode.MaterialSwap) ||
                                     !request.CreateSwitcher;

            if (replaceOnRenderers) ApplyMaterials(assignments);

            if (request.CreateSwitcher)
            {
                var pairs = BuildSwitchPairs(assignments, converted, request.CollectAnimatorMaterials);

                var parent = FindSwitcherParent(request.Targets);
                var swapRoot = FindSwapRoot(request.Targets);
                var switcher = NonToonSwitcherBuilder.Build(parent, swapRoot, pairs,
                    request.CreateMenuToggle, request.MenuParameter, request.MenuLabel,
                    request.NonToonOnByDefault, request.SwitcherMode, result);
                if (switcher != null && parent != null) EditorUtility.SetDirty(parent);
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

            // MA needs the menu installer inside the avatar; moving up to the avatar root is fine because
            // MA Material Swap's Root reference limits which renderers are affected.
            var avatarRoot = FindAvatarRoot(common);
            return avatarRoot != null ? avatarRoot : common.transform.parent != null ? common.transform.parent.gameObject : common;
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
