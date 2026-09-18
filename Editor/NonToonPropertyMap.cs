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
            // lilToon 的 `_BumpScale` 只是一张"备用"数值：法线贴图没挂时它完全不参与渲染，
            // 但作者往往调过（遇到过 8.17 这种值）。照搬到 NonToon 上会把默认白贴图也当成法线放大，
            // 所以没挂贴图时直接写 0（等于关掉）。
            MapFunc("_BumpScale", "_NormalScale", (src, dst, log) =>
            {
                if (!ShaderUtility.HasProperty(dst, "_NormalScale")) return;
                var hasMap = ShaderUtility.HasProperty(src, "_BumpMap") && src.GetTexture("_BumpMap") != null;
                if (!hasMap)
                {
                    dst.SetFloat("_NormalScale", 0f);
                    log.Mapped("_BumpScale（源材质没挂法线贴图）", "_NormalScale = 0（关掉）");
                    return;
                }
                var scale = src.GetFloat("_BumpScale");
                dst.SetFloat("_NormalScale", scale);
                log.Mapped("_BumpScale " + scale.ToString("0.###"), "_NormalScale");
            }, "法线强度");
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
                // lilToon 的 `_UseRim` 是这层效果的开关：作者关掉时（= 0）颜色值还留着，
                // 照搬会在 NonToon 上凭空多出一圈边缘光（头发上特别明显）。
                if (ShaderUtility.HasProperty(src, "_UseRim") && src.GetFloat("_UseRim") == 0f)
                {
                    dst.SetColor(to, Color.black);
                    log.Mapped("_UseRim = 0（作者没启用边缘光）", to + " = 黑色（关掉）");
                    return;
                }
                var color = src.GetColor("_RimColor");
                if (color == Color.clear) return;
                // lilToon 的 _RimMainStrength 作用在边缘光强度上，等价于缩放颜色
                var strength = ShaderUtility.HasProperty(src, "_RimMainStrength") ? src.GetFloat("_RimMainStrength") : 1f;
                var rgb = new Color(color.r * strength, color.g * strength, color.b * strength, color.a);
                dst.SetColor(to, rgb);
                log.Mapped("_RimColor" + (Mathf.Abs(strength - 1f) > 0.001f ? " × _RimMainStrength " + strength.ToString("0.###") : ""), to);
            }, "边缘光颜色");

            MapFunc("_RimBorder", ShaderUtility.NtRimLightPrefix + "RimLightRange", (src, dst, log) =>
            {
                var to = ShaderUtility.NtRimLightPrefix + "RimLightRange";
                if (!ShaderUtility.HasProperty(dst, to)) return;
                if (ShaderUtility.HasProperty(src, "_UseRim") && src.GetFloat("_UseRim") == 0f)
                {
                    dst.SetVector(to, new Vector4(1f, 1f, 0f, 0f));
                    return;
                }
                var border = src.GetFloat("_RimBorder");
                var blur = Mathf.Max(0.001f, src.GetFloat("_RimBlur"));
                if (border <= 0f) return;

                // lilToon 的边缘光：`f = 1 - dot(N,V)` → `f = pow(f, _RimFresnelPower)` → 再用 border / blur
                // 卡阈值。也就是说阈值和宽度都长在**幂次空间**里，而 NonToon 是在原始空间做 smoothstep，
                // 所以要把它们换算回原始空间：阈值位置 = border^(1/p)，宽度 = blur / (p·center^(p-1))
                //（就是 f^p 的导数，用来把幂次空间的宽度映射回原始空间）。
                var power = ShaderUtility.HasProperty(src, "_RimFresnelPower")
                    ? Mathf.Clamp(src.GetFloat("_RimFresnelPower"), 0.01f, 32f)
                    : 1f;
                float low, high;
                if (Mathf.Abs(power - 1f) < 0.001f)
                {
                    low = Mathf.Clamp01(border - blur * 0.5f);
                    high = Mathf.Clamp01(border + blur * 0.5f);
                }
                else
                {
                    var center = Mathf.Pow(Mathf.Clamp(border, 0.0001f, 1f), 1f / power);
                    var slope = power * Mathf.Pow(Mathf.Max(center, 0.0001f), power - 1f);
                    var width = Mathf.Clamp(blur / Mathf.Max(slope, 0.0001f), 0.01f, 1f);
                    low = Mathf.Clamp01(center - width * 0.5f);
                    high = Mathf.Clamp01(center + width * 0.5f);
                }
                dst.SetVector(to, new Vector4(low, high, 0f, 0f));
                log.Mapped("_RimBorder/_RimBlur/_RimFresnelPower " + power.ToString("0.##"),
                    to + " = (" + low.ToString("0.###") + ", " + high.ToString("0.###") + ")（换算回原始空间）");
            }, "边缘光边界/模糊/菲涅尔幂 -> 边缘光范围");

            // ----- matcap -----
            // lilToon 的 lilBlendColor：0 = Normal（**直接用 matcap 颜色替换**）、1 = Add、2 = Screen、3 = Multiply。
            // NonToon 只有 Multiply / Add 两个槽，所以 0/1/2 都放到 Add（叠加最接近"替换"），只有真正的
            // Multiply(3) 才用 Multiply 槽 —— 之前 0 被当成 Multiply，白布料 × 金色 matcap 会算成发灰，
            // 金饰就整片没了。
            MapFunc("_MatCapTex", ShaderUtility.NtMatCapsPrefix + "MatCapMultiply", (src, dst, log) =>
            {
                var multiply = ShaderUtility.NtMatCapsPrefix + "MatCapMultiply";
                var add = ShaderUtility.NtMatCapsPrefix + "MatCapAdd";
                var texture = src.GetTexture("_MatCapTex");
                if (texture == null) return;
                var blendMode = ShaderUtility.HasProperty(src, "_MatCapBlendMode")
                    ? Mathf.RoundToInt(src.GetFloat("_MatCapBlendMode"))
                    : 3;
                var to = blendMode == 3 ? multiply : add;
                if (!ShaderUtility.HasProperty(dst, to))
                {
                    log.Unsupported("MatCap：需要安装 NonToon 的 MatCaps 模块。");
                    return;
                }
                ShaderUtility.SetTextureValue(dst, to, texture);
                // 另一个槽要清空：材质是复用的，上一次转换留在里面的贴图会一起生效（效果翻倍）。
                var other = to == add ? multiply : add;
                if (ShaderUtility.HasProperty(dst, other)) ShaderUtility.SetTextureValue(dst, other, null);

                // NonToon 的 MatCaps 模块有个总开关 `_Enable`，**默认是 0（关闭）** ——
                // 只挂贴图不开开关的话模块完全不生效（金饰整片消失就是这么来的）。
                var enable = ShaderUtility.NtMatCapsPrefix + "Enable";
                if (ShaderUtility.HasProperty(dst, enable))
                {
                    if (ShaderUtility.ReadSerializedInt(dst, enable, 0) == 0)
                    {
                        ShaderUtility.SetIntPersistent(dst, enable, 1);
                        log.Mapped("_UseMatCap", enable + " = 1（打开 MatCap 模块）");
                    }
                    // Shader Core 的 SCConstValue 开关实际是给材质加一个关键字 `<属性名大写>_<值>`
                    // （在 Inspector 里点一下就是这个动作）。只写整数、不加关键字的话模块依然不生效，
                    // 表现为"开关明明是勾着的、但要手动再点一次才亮"。
                    var key = enable.ToUpperInvariant();
                    var value = ShaderUtility.ReadSerializedInt(dst, enable, 1);
                    dst.EnableKeyword(key + "_" + value);
                    dst.DisableKeyword(key + "_" + (value == 0 ? 1 : 0));
                }
                log.Mapped("_MatCapTex（lilToon 混合模式 " + blendMode + "）", to +
                    (to == add ? "（NonToon 没有 Normal/Screen，用叠加近似）" : ""));
            }, "MatCap 贴图：需要安装 NonToon 的 MatCaps 模块。");

            MapFunc("_MatCapColor", ShaderUtility.NtMatCapsPrefix + "MatCapMultiplyColor", (src, dst, log) =>
            {
                var multiplyColor = ShaderUtility.NtMatCapsPrefix + "MatCapMultiplyColor";
                var addColor = ShaderUtility.NtMatCapsPrefix + "MatCapAddColor";
                var color = ShaderUtility.HasProperty(src, "_MatCapColor") ? src.GetColor("_MatCapColor") : Color.white;
                // 总是写：材质是复用的，上一次转换留下的旧颜色会一直生效（如果是黑的，叠加等于没加）。
                var blendMode = ShaderUtility.HasProperty(src, "_MatCapBlendMode")
                    ? Mathf.RoundToInt(src.GetFloat("_MatCapBlendMode"))
                    : 3;
                var to = blendMode != 3 && ShaderUtility.HasProperty(dst, addColor) ? addColor : multiplyColor;
                if (!ShaderUtility.HasProperty(dst, to)) return;
                dst.SetColor(to, color);
                log.Mapped("_MatCapColor " + color.ToString("0.###"), to);
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

                // _NormalMapWithRoughness 读序列化值（Material.GetInt 对整数属性会报错）
                if (ShaderUtility.ReadSerializedInt(dst, "_NormalMapWithRoughness", 0) != 0) return;

                // lilToon 没开反射（_UseReflection = 0）时根本不画高光 → 对应 NonToon 的"最粗糙"。
                // 以前这里直接 return，结果留下 NonToon 的默认 0.5，等于凭空多出一个高光。
                var reflectOn = !ShaderUtility.HasProperty(src, "_UseReflection") || src.GetFloat("_UseReflection") != 0f;
                if (!reflectOn)
                {
                    dst.SetFloat("_Roughness", 1f);
                    log.Mapped("_UseReflection = 0（源材质没有高光）", "_Roughness = 1（无高光）");
                    return;
                }

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
