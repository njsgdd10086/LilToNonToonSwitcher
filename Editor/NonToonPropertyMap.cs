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
            // lilToon 的法线贴图在 NonToon 里要接**两份**：
            //   · 主 `_NormalMap` —— 只影响 sd.N（toon 硬色阶下的明暗分界 / 高光），细密纹理基本看不出来；
            //   · Details 模块的 `_Detail0NormalMap` —— 影响 sd.N_detail，而 Shade 模块**真的**用
            //     `NdotL_Detail` 参与明暗计算 → 织物那种质感就是靠这条显示出来的。
            // 只接主 Normal Map 时，布料会变成"塑料感、纹理糊掉"。
            MapFunc("_BumpMap", "_jp_lilxyzw_nontoon_details_Detail0NormalMap", (src, dst, log) =>
            {
                const string prefix = "_jp_lilxyzw_nontoon_details_";
                var normal = prefix + "Detail0NormalMap";
                if (!ShaderUtility.HasProperty(dst, normal)) return;
                var texture = ShaderUtility.HasProperty(src, "_BumpMap") ? src.GetTexture("_BumpMap") : null;
                if (texture == null) return;

                ShaderUtility.SetTextureValue(dst, normal, texture);
                if (ShaderUtility.HasProperty(dst, prefix + "Detail0NormalScale"))
                    ShaderUtility.SetFloatValue(dst, prefix + "Detail0NormalScale",
                        ShaderUtility.HasProperty(src, "_BumpScale") ? src.GetFloat("_BumpScale") : 1f);
                // 细节层的 UV 走 _Detail0Texture_ST，跟随源法线贴图自己的 tiling/offset
                if (ShaderUtility.HasProperty(dst, prefix + "Detail0Texture"))
                {
                    dst.SetTextureScale(prefix + "Detail0Texture", src.GetTextureScale("_BumpMap"));
                    dst.SetTextureOffset(prefix + "Detail0Texture", src.GetTextureOffset("_BumpMap"));
                }

                // 打开 Details 模块：写整数 + Shader Core 认的关键字（和 MatCap 一模一样的坑）
                var enable = prefix + "Enable";
                if (ShaderUtility.HasProperty(dst, enable))
                {
                    if (ShaderUtility.ReadSerializedInt(dst, enable, 0) == 0)
                    {
                        ShaderUtility.SetIntPersistent(dst, enable, 1);
                        log.Mapped("_BumpMap → Details 模块", enable + " = 1（让法线参与明暗）");
                    }
                    // Details 一旦打开，四层细节都会 `albedo *= lerp(1, detailTex * boost, mask)`，
                    // 而默认 `_DetailMask` 是白的（= 全部生效）—— boost 必须是 1，否则基础色会被整体提亮/压暗。
                    for (var i = 0; i < 4; i++)
                    {
                        var boost = prefix + "Detail" + i + "Boost";
                        if (ShaderUtility.HasProperty(dst, boost)) ShaderUtility.SetFloatValue(dst, boost, 1f);
                    }
                    var key = enable.ToUpperInvariant();
                    dst.EnableKeyword(key + "_1");
                    dst.DisableKeyword(key + "_0");
                }
                log.Mapped("_BumpMap（同时接到 Details 的 Detail0NormalMap）", normal);
            }, "法线贴图 → Details 模块：需要安装 NonToon 的 Details 模块。");
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

            // 织物/细节法线模块（插件自带，会登记进 NonToon 的 Shader Core 模块列表）：
            // 主 _NormalMap 的扰动在受光面没有出口（sd.lightColor 被 saturate 了），这个模块在
            // __SC_PHASE_postpixel__ 里只把"法线扰动造成的明暗差"乘回去 —— 织物纹理就是这么显示出来的。
            MapFunc("_BumpMap", "fabric", (src, dst, log) =>
            {
                var texture = ShaderUtility.HasProperty(src, "_BumpMap") ? src.GetTexture("_BumpMap") : null;
                if (texture == null) return;
                if (!FabricModuleInstaller.EnsureInstalled(log)) return;

                var shader = dst.shader;
                var mapProperty = FabricModuleInstaller.FindPropertyName(shader, "FabricNormalMap");
                if (mapProperty == null)
                {
                    log.Warn("织物法线模块刚登记，NonToon 的 shader 还在重新生成 —— 请稍后再转换一次。");
                    return;
                }
                dst.SetTexture(mapProperty, texture);

                var normalStrength = FabricModuleInstaller.FindPropertyName(shader, "FabricNormalStrength");
                if (normalStrength != null)
                    ShaderUtility.SetFloatValue(dst, normalStrength,
                        ShaderUtility.HasProperty(src, "_BumpScale") ? src.GetFloat("_BumpScale") : 1f);

                var strength = FabricModuleInstaller.FindPropertyName(shader, "FabricStrength");
                // 默认 0.25：强度扫描对照源 lilToon 后选的（0 = 平得像塑料，0.5 偏强，1.0 出现刺眼噪点）
                // 不写强度值：FabricStrength 的默认值由模块自己的 properties.hlsl 决定，
                // 转换器硬写 0.25 属于"魔法值补偿"，已移除（用户明确要求转换必须严谨、不许猜数值）。

                // 自动启用（用户要求：保持自动启用，bug 就修）。
                // 注意：本模块属于自建模块，实测 Shader Core **不下发其属性**，所以相位里**不能依赖任何
                // 材质参数**（否则读到默认值/未初始化值会画出不受控的色块）。相位已改为只用几何法线 sd.N
                // 与共享遮罩（都是可达的核心数据）做织物明暗。
                var enable = FabricModuleInstaller.FindPropertyName(shader, "Enable");
                if (enable != null)
                {
                    ShaderUtility.SetIntPersistent(dst, enable, 1);
                    var key = enable.ToUpperInvariant();
                    dst.EnableKeyword(key + "_1");
                    dst.DisableKeyword(key + "_0");
                    log.Mapped("_BumpMap → 织物法线模块", enable + " = 1（相位不依赖模块属性）");
                }
            }, "织物/细节法线模块");

            // Shade 替换模块（模块包里的 com.nontoon.modules.shadereplace）：
            // lilToon 是 col = lerp(indirectCol, directCol, lns)，阴影色是**绝对颜色**，可以比本体还亮
            // （实测这套模型：衣服阴影 1.41~1.71 倍本体、头发 1.06~1.37 倍）；而 NonToon 的 Shade 是
            // **乘算**（渐变贴图 RGBA32 + sRGB，上限 1）→ 只能压暗 → 这类材质整体偏暗。
            // 这个模块在 postpixel 直接做 lilToon 的替换，阴影色走材质属性（不受 0~1 限制）。
            MapFunc("_ShadowColor", "shadeReplace", (src, dst, log) =>
            {
                if (!ShaderUtility.HasProperty(src, "_UseShadow") || src.GetFloat("_UseShadow") == 0f) return;
                if (!ShaderUtility.HasProperty(src, "_ShadowColor")) return;

                const string token = "shadereplace";
                if (!FabricModuleInstaller.EnsureModule("com.nontoon.modules.shadereplace", "Shade 替换模块", log)) return;

                var shader = dst.shader;
                var enable = FabricModuleInstaller.FindModuleProperty(shader, token, "Enable");
                if (enable == null)
                {
                    log.Warn("Shade 替换模块刚登记，NonToon 的 shader 还在重新生成 —— 请稍后再转换一次。");
                    return;
                }

                var setColor = new Action<string, string>((lilToonName, suffix) =>
                {
                    if (!ShaderUtility.HasProperty(src, lilToonName)) return;
                    var property = FabricModuleInstaller.FindModuleProperty(shader, token, suffix);
                    if (property == null) return;
                    dst.SetColor(property, src.GetColor(lilToonName));
                });
                var setFloat = new Action<string, string>((lilToonName, suffix) =>
                {
                    if (!ShaderUtility.HasProperty(src, lilToonName)) return;
                    var property = FabricModuleInstaller.FindModuleProperty(shader, token, suffix);
                    if (property == null) return;
                    ShaderUtility.SetFloatValue(dst, property, src.GetFloat(lilToonName));
                });

                setColor("_ShadowColor", "ShadowColor");
                setColor("_Shadow2ndColor", "Shadow2ndColor");
                setColor("_Shadow3rdColor", "Shadow3rdColor");
                setFloat("_ShadowBorder", "ShadowBorder");
                setFloat("_ShadowBlur", "ShadowBlur");
                setFloat("_Shadow2ndBorder", "Shadow2ndBorder");
                setFloat("_Shadow2ndBlur", "Shadow2ndBlur");
                setFloat("_Shadow3rdBorder", "Shadow3rdBorder");
                setFloat("_Shadow3rdBlur", "Shadow3rdBlur");
                setFloat("_ShadowStrength", "ShadowStrength");
                setFloat("_ShadowMainStrength", "ShadowMainStrength");

                ShaderUtility.SetIntPersistent(dst, enable, 1);
                var key = enable.ToUpperInvariant();
                dst.EnableKeyword(key + "_1");
                dst.DisableKeyword(key + "_0");

                // 注意：这里**不要**把 Shade 渐变索引设成 -1。
                // ShadeReplace 模块现在只负责"补回丢失的主光"，不做明暗；NonToon 自带的 Shade 渐变
                // 才是负责阴影分层的（转换器会把 lilToon 的阴影色烘成渐变并写入正确的索引）。
                // 之前在这关掉它，材质就只剩光照、中间调被冲白（实测衣服发白发平）。
                log.Mapped("_ShadowColor/_Shadow2ndColor/… → Shade 替换模块", enable + " = 1");
                // 主光强度：保持模块默认值 1 —— 也就是 lilToon 自己的 clamp(_LightColor0 × atten, Min, Max)。
                // 曾经标定成 1.6，但那是在**被我改动过光照的场景**里扫出来的坏基准：shinano 那边主光本来
                // 就有 0.73，乘 1.6 = 1.17 被 saturate 钳成 1，脸又变回"贴图 × 1.0"的白脸。
                // 所以这里只把历史遗留的 1.6 迁回默认值，不再放大。
                for (var i = 0; i < dst.shader.GetPropertyCount(); i++)
                {
                    var name = dst.shader.GetPropertyName(i);
                    if (name.IndexOf("shadereplace", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!name.EndsWith("MainLightStrength", StringComparison.Ordinal)) continue;
                    if (Mathf.Abs(dst.GetFloat(name) - 1f) > 0.001f)
                    {
                        ShaderUtility.SetFloatValue(dst, name, 1f);
                        log.Mapped("主光强度 → 1（= lilToon 自己的光强）", "撤销之前标定过头的 1.6");
                    }
                    break;
                }
            }, "Shade 替换模块（需要 com.nontoon.modules）");

            // ----- 自发光（emission）-----
            // NonToon 没有 emission 属性，而且它自带 Lighten 模块的 As Emission 在实测的两个工程里
            // **完全不执行**（把 LightBoost 设成 5，一个像素都没变），所以自发光由我们自己的模块实现：
            //   ShadeReplace 的 phase 里 sd.postadd += _EmissionColor.rgb * _EmissionStrength * mask
            //   （postadd 是 NonToon 在 col *= lightColor 之后才加的 —— 正好等于 lilToon 的加算自发光）
            // lilToon 原式：col += _EmissionColor.rgb * _EmissionBlend * _EmissionColor.a * 蒙版。
            MapFunc("_EmissionColor", "_com_nontoon_modules_shadereplace_EmissionColor", (src, dst, log) =>
            {
                if (!ShaderUtility.HasProperty(src, "_UseEmission") || src.GetFloat("_UseEmission") == 0f) return;

                const string prefix = "_com_nontoon_modules_shadereplace_";
                var colorProperty = prefix + "EmissionColor";
                var strengthProperty = prefix + "EmissionStrength";
                if (!ShaderUtility.HasProperty(dst, colorProperty) || !ShaderUtility.HasProperty(dst, strengthProperty))
                {
                    log.Unsupported("自发光（需要 NonToon 模块包 com.nontoon.modules 的新版 ShadeReplace 模块）");
                    return;
                }

                var color = ShaderUtility.HasProperty(src, "_EmissionColor") ? src.GetColor("_EmissionColor") : Color.white;
                var blend = ShaderUtility.HasProperty(src, "_EmissionBlend") ? src.GetFloat("_EmissionBlend") : 1f;
                // lilToon 2.x：_EmissionMainStrength 决定"自发光有多少作用到主颜色"。
                // 实测（Atri_face 1：_UseEmission=1、_EmissionColor=0.45、_EmissionBlend=1、模式=加算，
                // 但 _EmissionMainStrength=0）：把 lil 自己的 _UseEmission 关掉，渲染出来只差 0.002 ——
                // 也就是这个材质在 lilToon 下**几乎不发光**。之前漏了这个系数，无脑加 0.45，
                // 整张脸被冲白（实测暖度 R-B 从 0.067 掉到 0.006），也就是用户说的"发白蒙脸"。
                var mainStrength = ShaderUtility.HasProperty(src, "_EmissionMainStrength")
                    ? Mathf.Clamp01(src.GetFloat("_EmissionMainStrength"))
                    : 1f;
                // lilToon: emissionBlend = _EmissionBlend * _EmissionColor.a（alpha 就是强度）
                var strength = blend * color.a * mainStrength;
                // 不发光时必须把模块的强度清零：重新转换一个原本带自发光的材质时，
                // 如果这里直接 return，材质上会残留上一次转换写进去的值（实测残留 0.453 → 整脸发白）。
                if (strength <= 0.001f)
                {
                    ShaderUtility.SetFloatValue(dst, strengthProperty, 0f);
                    log.Mapped("_EmissionColor × 主色强度 " + mainStrength.ToString("0.##"),
                        "自发光 = 0（lilToon 的 _EmissionMainStrength 为 0，自发光不作用到主颜色）");
                    return;
                }

                // 颜色里的 HDR 亮度乘进去（模块的 _EmissionColor 是普通颜色，避免 HDR 在材质上被夹掉）
                var peak = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
                var normalized = peak > 0.0001f
                    ? new Color(color.r / peak, color.g / peak, color.b / peak, 1f)
                    : Color.white;
                dst.SetColor(colorProperty, normalized);
                ShaderUtility.SetFloatValue(dst, strengthProperty, Mathf.Clamp(peak * strength, 0f, 8f));

                var maskOk = (ShaderUtility.HasProperty(src, "_EmissionMap") && src.GetTexture("_EmissionMap") != null) ||
                             (ShaderUtility.HasProperty(src, "_EmissionBlendMask") && src.GetTexture("_EmissionBlendMask") != null);
                log.Mapped("_EmissionColor（HDR " + color.r.ToString("0.##") + "/" + color.g.ToString("0.##") + "/" + color.b.ToString("0.##")
                           + " × 强度 " + color.a.ToString("0.##") + " × 主色强度 " + mainStrength.ToString("0.##") + "）",
                    "自发光 = " + (peak * strength).ToString("0.###") + "（加算，由 ShadeReplace 模块实现）" +
                    (maskOk ? "，形状/蒙版烘进 _SharedMask 见 EmissionMaskChannel" : "（源没有发光贴图 → 整块材质生效）"));
            }, "自发光（ShadeReplace 模块）");

            // 自发光蒙版走共享遮罩，由 NonToonMaskBuilder 的 MaskSource（ModuleKeyword = "Emission"）负责
            // 烘进 _SharedMask 并设置 _EmissionMaskChannel —— 不要在这里映射贴图：
            // 在模块的 properties.hlsl 里声明 SC_Texture2D 会让整个模块被 Shader Core 丢弃（实测：模块
            // 的 phase 完全不生效、亮度从 0.54 掉回 0.28），用共享遮罩就没有这个坑。

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

            // ----- matcap（第二层）-----
            // NonToon 的两个槽其实是**两层** MatCap（各自带颜色 / Detail / Mask Channel），
            // 不是"同一个的第一层两种混合模式"。第一层按混合模式占了一个槽之后，第二层就用**剩下那个**。
            // 以前完全没转第二层（连贴图都没映射），所以走第二层的装饰（比如帽子的羽毛）转完就一直是灰的。
            MapFunc("_MatCap2ndTex", ShaderUtility.NtMatCapsPrefix + "MatCapMultiply", (src, dst, log) =>
            {
                if (ShaderUtility.HasProperty(src, "_UseMatCap2nd") && src.GetFloat("_UseMatCap2nd") == 0f) return;
                var texture = ShaderUtility.HasProperty(src, "_MatCap2ndTex") ? src.GetTexture("_MatCap2ndTex") : null;
                if (texture == null) return;

                var to = SecondMatCapSlot(src);
                if (!ShaderUtility.HasProperty(dst, to))
                {
                    log.Unsupported("MatCap 第二层：需要安装 NonToon 的 MatCaps 模块。");
                    return;
                }
                ShaderUtility.SetTextureValue(dst, to, texture);
                log.Mapped("_MatCap2ndTex", to + "（第二层；第一层用的是另一个槽）");
                EnableMatCapsModule(dst, log);
            }, "MatCap 第二层贴图：需要安装 NonToon 的 MatCaps 模块。");

            MapFunc("_MatCap2ndColor", ShaderUtility.NtMatCapsPrefix + "MatCapMultiplyColor", (src, dst, log) =>
            {
                if (ShaderUtility.HasProperty(src, "_UseMatCap2nd") && src.GetFloat("_UseMatCap2nd") == 0f) return;
                var texture = ShaderUtility.HasProperty(src, "_MatCap2ndTex") ? src.GetTexture("_MatCap2ndTex") : null;
                if (texture == null) return;
                var to = SecondMatCapSlot(src).Replace("MatCapAdd", "MatCapAddColor").Replace("MatCapMultiply", "MatCapMultiplyColor");
                if (!ShaderUtility.HasProperty(dst, to)) return;
                var color = ShaderUtility.HasProperty(src, "_MatCap2ndColor") ? src.GetColor("_MatCap2ndColor") : Color.white;
                // lilToon 的 _MatCap2ndBlend 是这一层的强度，NonToon 没有单独的强度属性 → 乘进颜色里
                var blend = ShaderUtility.HasProperty(src, "_MatCap2ndBlend") ? Mathf.Clamp01(src.GetFloat("_MatCap2ndBlend")) : 1f;
                var scaled = new Color(color.r * blend, color.g * blend, color.b * blend, color.a);
                dst.SetColor(to, scaled);
                log.Mapped("_MatCap2ndColor × _MatCap2ndBlend " + blend.ToString("0.###"), to);
            }, "MatCap 第二层颜色：需要安装 NonToon 的 MatCaps 模块。");

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

        /// <summary>第一层 MatCap 占了一个槽之后，第二层用剩下的那个。</summary>
        private static string SecondMatCapSlot(Material source)
        {
            var multiply = ShaderUtility.NtMatCapsPrefix + "MatCapMultiply";
            var add = ShaderUtility.NtMatCapsPrefix + "MatCapAdd";
            var blendMode = ShaderUtility.HasProperty(source, "_MatCapBlendMode")
                ? Mathf.RoundToInt(source.GetFloat("_MatCapBlendMode"))
                : 3;
            var first = blendMode == 3 ? multiply : add;
            return first == add ? multiply : add;
        }

        /// <summary>打开 MatCaps 模块：写整数 + Shader Core 真正认的那个关键字。</summary>
        private static void EnableMatCapsModule(Material target, ConversionLog log)
        {
            var enable = ShaderUtility.NtMatCapsPrefix + "Enable";
            if (!ShaderUtility.HasProperty(target, enable)) return;
            if (ShaderUtility.ReadSerializedInt(target, enable, 0) == 0)
            {
                ShaderUtility.SetIntPersistent(target, enable, 1);
                log.Mapped("MatCap", enable + " = 1（打开 MatCaps 模块）");
            }
            var key = enable.ToUpperInvariant();
            var value = ShaderUtility.ReadSerializedInt(target, enable, 1);
            target.EnableKeyword(key + "_" + value);
            target.DisableKeyword(key + "_" + (value == 0 ? 1 : 0));
        }

        private static void MapTexture(string from, string to, string note, string unsupportedNote = null)        {
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

// touch r35

