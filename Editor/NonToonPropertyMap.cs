// LilToNonToon Switcher
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NonToonSwitcher
{
    /// <summary>
    /// lilToon -> NonToon property mapping.
    ///
    /// Three layers are used, in order:
    ///   1. the explicit table below (semantic conversions, the only place values are re-interpreted),
    ///   2. automatic copy of properties that exist in both shaders with the same type,
    ///   3. fuzzy copy of renamed properties of the same type.
    ///
    /// lilToon features that NonToon 0.1.x does not implement (emission, glitter, AudioLink, parallax,
    /// tessellation, dissolve, 2nd/3rd main colour, ...) end up in the conversion log as warnings
    /// instead of being dropped silently.
    /// </summary>
    public static class NonToonPropertyMap
    {
        public delegate void CopyFunc(Material source, Material destination, ConversionLog log);

        public sealed class Entry
        {
            public string From;
            public string To;
            public CopyFunc Copy;
            public string Note;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Dictionary<string, Entry> BySource = new Dictionary<string, Entry>(StringComparer.Ordinal);

        private static void MapFunc(string from, string to, CopyFunc copy, string note = null)
        {
            var entry = new Entry { From = from, To = to, Copy = copy, Note = note };
            Entries.Add(entry);
            BySource[from] = entry;
        }

        public static bool TryGetEntry(string lilToonProperty, out Entry entry)
        {
            return BySource.TryGetValue(lilToonProperty, out entry);
        }

        public static IEnumerable<Entry> AllEntries { get { return Entries; } }

        // ------------------------------------------------------------------ normalisation for the fuzzy pass

        public static string NormalizeForFuzzy(string property)
        {
            var name = property ?? string.Empty;
            if (name.StartsWith(ShaderUtility.NtModulePrefix, StringComparison.Ordinal))
                name = "_" + name.Substring(ShaderUtility.NtModulePrefix.Length);
            if (name.StartsWith("_", StringComparison.Ordinal)) name = name.Substring(1);

            name = name.ToLowerInvariant().Replace("_", "");

            var suffixes = new[] { "texture", "tex", "map", "mask" };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var suffix in suffixes)
                {
                    if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        name = name.Substring(0, name.Length - suffix.Length);
                        changed = true;
                        break;
                    }
                }
            }
            return name;
        }

        /// <summary>Normalised-name lookup of the destination shader properties (fuzzy fallback pass).</summary>
        public static Dictionary<string, string> BuildFuzzyTargets(Shader nonToonShader)
        {
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            if (nonToonShader == null) return targets;
            var count = nonToonShader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = nonToonShader.GetPropertyName(i);
                if (string.IsNullOrEmpty(name)) continue;
                var normalized = NormalizeForFuzzy(name);
                if (normalized.Length < 3) continue;
                if (!targets.ContainsKey(normalized)) targets[normalized] = name;
            }
            return targets;
        }

        // ------------------------------------------------------------------ the table

        static NonToonPropertyMap()
        {
            BuildEntries();
        }

        private static void BuildEntries()
        {
            // ----- base -----
            MapFunc("_MainTex", "_BaseTexture", (src, dst, log) =>
            {
                if (!ShaderUtility.HasProperty(src, "_MainTex")) return;
                if (!ShaderUtility.HasProperty(dst, "_BaseTexture")) { log.Unsupported("主贴图：NonToon 没有 _BaseTexture 属性。"); return; }
                dst.SetTexture("_BaseTexture", src.GetTexture("_MainTex"));
                var scale = src.GetTextureScale("_MainTex");
                var offset = src.GetTextureOffset("_MainTex");
                if (scale != Vector2.one) dst.SetTextureScale("_BaseTexture", scale);
                if (offset != Vector2.zero) dst.SetTextureOffset("_BaseTexture", offset);
                log.Mapped("_MainTex", "_BaseTexture");
            }, "主贴图");
            MapFunc("_Color", "_BaseColor", (src, dst, log) =>
            {
                if (!ShaderUtility.HasProperty(src, "_Color")) return;
                var colour = src.GetColor("_Color");
                if (ShaderUtility.HasProperty(dst, "_BaseColor"))
                {
                    dst.SetColor("_BaseColor", colour);
                    log.Mapped("_Color", "_BaseColor");
                    return;
                }
                // NonToon 0.1.x has no base colour property. The converter bakes it into the base texture;
                // whether that worked is only known afterwards, so it is recorded instead of warned about here.
                if (colour != Color.white)
                    log.RecordUnconverted("_Color", "主颜色：" + colour +
                        "，NonToon 没有基础颜色属性且未能烘焙进贴图，请转换后手动调整。");
            }, "主颜色 -> NonToon 基础颜色");
            MapFloat("_Cutoff", "_Cutoff", "透明裁剪阈值");
            MapTexture("_DitherTex", "_NTDitherTex", "抖动贴图");
            MapToggle("_UseDither", "_NTDitherTex", "抖动");
            MapFloat("_AlphaToMask", "_AlphaToMask", "AlphaToMask 透明覆盖");

            // ----- normal map -----
            MapTexture("_BumpMap", "_NormalMap", "法线贴图");
            MapFloat("_BumpScale", "_NormalScale", "法线强度");
            MapToggle("_UseBumpMap", "_NormalMap", "法线贴图");

            // ----- outline -----
            // NonToon's standard outline has no outline texture / width mask of its own.
            MapColor("_OutlineColor", "_OutlineColor", "描边颜色");
            MapFunc("_OutlineTex", "_OutlineColor", (src, dst, log) =>
            {
                if (src.GetTexture("_OutlineTex") == null) return;
                log.Unsupported("描边贴图：NonToon 没有对应设置。");
            }, "描边贴图");
            MapFloat("_OutlineWidth", "_OutlineWidth", "描边宽度");
            MapFloat("_OutlineZBias", "_OutlineZOffset", "描边 Z 偏移量 -> 描边 Z Offset");
            MapToggle("_OutlineVertexR2Width", "_OutlineFromVertexColor", "用顶点色控制描边宽度");

            // ----- rim shade -----
            MapColor("_RimShadeColor", ShaderUtility.NtRimShadePrefix + "RimShadeColor", "边缘阴影颜色");

            // ----- render state that lives in float properties on the NonToon side -----
            MapFloat("_StencilRef", "_StencilRef", "模板参考值");
            MapFloat("_OutlineStencilRef", "_OutlineStencilRef", "描边模板参考值");
            MapFloat("_StencilComp", "_StencilComp", "模板比较方式");
            MapFloat("_StencilPass", "_StencilPass", "模板 Pass 操作");
            MapFloat("_OutlineStencilComp", "_OutlineStencilComp", "描边模板比较方式");
            MapFloat("_OutlineStencilPass", "_OutlineStencilPass", "描边模板 Pass 操作");
            MapFloat("_ZTest", "_ZTest", "ZTest 深度测试");

            // ----- backlight -----
            MapToggle("_UseBacklight", ShaderUtility.NtBacklightPrefix + "Enable", "逆光");
            MapColor("_BacklightColor", ShaderUtility.NtBacklightPrefix + "BacklightColor", "逆光颜色");
            MapFloat("_BacklightBorder", ShaderUtility.NtBacklightPrefix + "BacklightRange", "逆光边界 -> 逆光范围");

            // ----- rim light -----
            MapFunc("_RimColor", ShaderUtility.NtRimLightPrefix + "RimLightColor", (src, dst, log) =>
            {
                var to = ShaderUtility.NtRimLightPrefix + "RimLightColor";
                if (!ShaderUtility.HasProperty(dst, to)) return;
                var color = src.GetColor("_RimColor");
                if (color == Color.clear) return;
                dst.SetColor(to, color);
                log.Mapped("_RimColor", to);
            }, "边缘光颜色");

            MapFunc("_RimBorder", ShaderUtility.NtRimLightPrefix + "RimLightRange", (src, dst, log) =>
            {
                var to = ShaderUtility.NtRimLightPrefix + "RimLightRange";
                if (!ShaderUtility.HasProperty(dst, to)) return;
                var border = src.GetFloat("_RimBorder");
                var blur = Mathf.Max(0.001f, src.GetFloat("_RimBlur"));
                if (border <= 0f) return;
                var low = Mathf.Clamp01(border - blur * 0.5f);
                var high = Mathf.Clamp01(border + blur * 0.5f);
                dst.SetVector(to, new Vector4(low, high, 0f, 0f));
                log.Mapped("_RimBorder", to + " (approximated from border + blur)");
            }, "边缘光边界/模糊 -> 边缘光范围");

            // ----- matcap -----
            MapFunc("_MatCapTex", ShaderUtility.NtMatCapsPrefix + "MatCapMultiply", (src, dst, log) =>
            {
                var multiply = ShaderUtility.NtMatCapsPrefix + "MatCapMultiply";
                var add = ShaderUtility.NtMatCapsPrefix + "MatCapAdd";
                var texture = src.GetTexture("_MatCapTex");
                if (texture == null) return;
                var blendMode = Mathf.RoundToInt(src.GetFloat("_MatCapBlendMode"));
                // lilToon: 0 normal, 1 add, 2 screen, 3 multiply, 4 overlay / 2nd matcap is always an overlay.
                var to = blendMode == 1 || blendMode == 2 ? add : multiply;
                if (!ShaderUtility.HasProperty(dst, to)) { log.Unsupported("MatCap：需要安装 NonToon 的 MatCaps 模块。"); return; }
                ShaderUtility.SetTextureValue(dst, to, texture);
                if (ShaderUtility.HasProperty(src, "_MatCapBlend"))
                    ShaderUtility.SetFloatValue(dst, to == add ? ShaderUtility.NtMatCapsPrefix + "MatCapAddDetail" : ShaderUtility.NtMatCapsPrefix + "MatCapMultiplyDetail",
                        Mathf.Clamp01(1f - src.GetFloat("_MatCapBlend")));
                log.Mapped("_MatCapTex", to);
            }, "MatCap 贴图：需要安装 NonToon 的 MatCaps 模块。");

            MapFunc("_MatCapColor", ShaderUtility.NtMatCapsPrefix + "MatCapMultiplyColor", (src, dst, log) =>
            {
                var multiplyColor = ShaderUtility.NtMatCapsPrefix + "MatCapMultiplyColor";
                var addColor = ShaderUtility.NtMatCapsPrefix + "MatCapAddColor";
                var color = src.GetColor("_MatCapColor");
                if (color == Color.white) return;
                var blendMode = Mathf.RoundToInt(src.GetFloat("_MatCapBlendMode"));
                var to = (blendMode == 1 || blendMode == 2) && ShaderUtility.HasProperty(dst, addColor) ? addColor : multiplyColor;
                if (!ShaderUtility.HasProperty(dst, to)) return;
                dst.SetColor(to, color);
                log.Mapped("_MatCapColor", to);
            }, "MatCap 颜色：需要安装 NonToon 的 MatCaps 模块。");

            // ----- specular / reflection -----
            // Aligned with how the reference lilToon -> NonToon conversion behaves:
            //   _UseReflection off -> leave NonToon's defaults alone (roughness 0.5, specular black),
            //                         because lilToon is not drawing a reflection either.
            //   _UseReflection on  -> roughness comes from lilToon smoothness, and the specular colour is
            //                         _ReflectionColor copied as-is (scaling it by _Reflectance makes the
            //                         highlight far too dark: the default reflectance is only 0.04).
            var specularColorProperty = ShaderUtility.NtSpecularPrefix + "SpecularColor";
            MapFunc("_ReflectionColor", specularColorProperty, (src, dst, log) =>
            {
                var to = ShaderUtility.NtSpecularPrefix + "SpecularColor";
                if (!ShaderUtility.HasProperty(dst, to)) return;

                var reflectionOn = !ShaderUtility.HasProperty(src, "_UseReflection") ||
                                   src.GetFloat("_UseReflection") != 0f;

                if (!reflectionOn)
                {
                    dst.SetColor(to, Color.black);
                    log.Mapped("_UseReflection 0", "SpecularColor 保持黑（不产生高光）");
                    return;
                }

                var tint = ShaderUtility.HasProperty(src, "_ReflectionColor") ? src.GetColor("_ReflectionColor") : Color.white;
                dst.SetColor(to, new Color(tint.r, tint.g, tint.b, 1f));
                log.Mapped("_ReflectionColor", to);
            }, "高光颜色：需要安装 NonToon 的 Specular 模块。");

            MapFunc("_Smoothness", "_Roughness", (src, dst, log) =>
            {
                if (!ShaderUtility.HasProperty(dst, "_Roughness")) return;

                // lilToon does not draw a reflection when _UseReflection is off, so NonToon keeps its default.
                if (ShaderUtility.HasProperty(src, "_UseReflection") && src.GetFloat("_UseReflection") == 0f) return;

                // _NormalMapWithRoughness is read from the serialized data only: Material.GetInt logs an error
                // for that integer property.
                if (ShaderUtility.ReadSerializedInt(dst, "_NormalMapWithRoughness", 0) != 0) return;

                var smoothness = src.GetFloat("_Smoothness");
                var roughness = Mathf.Clamp(1f - smoothness, 0.05f, 1f);
                dst.SetFloat("_Roughness", roughness);
                log.Mapped("_Smoothness " + smoothness.ToString("0.###"), "_Roughness " + roughness.ToString("0.###"));
            }, "粗糙度 -> NonToon Roughness");

            // ----- backlight texture -----
            MapFunc("_BacklightColorTex", ShaderUtility.NtBacklightPrefix + "BacklightColor", (src, dst, log) =>
            {
                if (src.GetTexture("_BacklightColorTex") == null) return;
                var to = ShaderUtility.NtBacklightPrefix + "BacklightColor";
                if (!ShaderUtility.HasProperty(dst, to)) return;
                dst.SetColor(to, src.GetColor("_BacklightColor"));
                log.Mapped("_BacklightColor", to);
            }, "逆光颜色：需要安装 NonToon 的 Backlight 模块。");
        }

        // ------------------------------------------------------------------ table helpers

        private static void MapTexture(string from, string to, string note, string unsupportedNote = null)
        {
            MapFunc(from, to, (src, dst, log) => { CopyTexture(src, dst, from, to, log, unsupportedNote); }, note);
        }

        private static void MapFloat(string from, string to, string note, string unsupportedNote = null)
        {
            MapFunc(from, to, (src, dst, log) =>
            {
                if (!ShaderUtility.HasProperty(src, from)) return;
                if (!ShaderUtility.HasProperty(dst, to))
                {
                    if (src.GetFloat(from) != 0f && unsupportedNote != null) log.Unsupported(unsupportedNote);
                    return;
                }
                var value = src.GetFloat(from);
                if (Mathf.Approximately(value, 0f) && from != "_Cutoff") return;
                ShaderUtility.SetFloatValue(dst, to, value);
                log.Mapped(from, to);
            }, note);
        }

        private static void MapToggle(string from, string to, string note)
        {
            MapFunc(from, to, (src, dst, log) =>
            {
                if (!ShaderUtility.HasProperty(src, from)) return;
                if (src.GetFloat(from) == 0f) return;
                if (!ShaderUtility.HasProperty(dst, to))
                {
                    log.Unsupported(note + "：目标属性 " + to + " 不存在。");
                    return;
                }
                log.Mapped(from, "enabled " + note);
            }, note);
        }

        private static void MapColor(string from, string to, string note, string unsupportedNote = null)
        {
            MapFunc(from, to, (src, dst, log) =>
            {
                if (!ShaderUtility.HasProperty(src, from)) return;
                var color = src.GetColor(from);
                if (color == Color.clear && from != "_Color") return;
                if (!ShaderUtility.HasProperty(dst, to))
                {
                    if (unsupportedNote != null && color != Color.white) log.Unsupported(unsupportedNote);
                    return;
                }
                dst.SetColor(to, color);
                log.Mapped(from, to);
            }, note);
        }

        public static void CopyTexture(Material src, Material dst, string from, string to, ConversionLog log, string unsupportedNote)
        {
            if (!ShaderUtility.HasProperty(src, from)) return;
            var texture = src.GetTexture(from);
            if (texture == null) return;
            if (!ShaderUtility.HasProperty(dst, to))
            {
                if (unsupportedNote != null) log.Unsupported(unsupportedNote);
                return;
            }
            dst.SetTexture(to, texture);
            var scale = src.GetTextureScale(from);
            var offset = src.GetTextureOffset(from);
            if (scale != Vector2.one) dst.SetTextureScale(to, scale);
            if (offset != Vector2.zero) dst.SetTextureOffset(to, offset);
            log.Mapped(from, to);
        }
    }
}
