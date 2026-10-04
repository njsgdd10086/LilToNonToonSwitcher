// LilToNonToon Switcher
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    /// <summary>
    /// lilToon keeps every mask in its own texture and channel. NonToon packs them into the RGBA channels of one
    /// shared mask (<c>_SharedMask</c>), and each feature selects its channel through a "Mask Channel" property.
    /// This class reads the channel selection from the freshly converted material and packs the matching lilToon
    /// masks into a single texture.
    /// </summary>
    public static class NonToonMaskBuilder
    {
        private sealed class MaskSource
        {
            public string LilToonProperty;
            public string ModuleKeyword;        // used to locate the "<Feature> Mask Channel" property
            public string FeatureName;
            public bool RequireToggle;
            public string ToggleProperty;
            /// <summary>源材质上有这个非空贴图时跳过本项（用于让 _EmissionMap 优先于 _EmissionBlendMask）。</summary>
            public string SkipIfPropertyPresent;
        }

        public static void Bake(Material lilToonMaterial, Material nonToonMaterial, string folder, ConversionLog log)
        {
            var shader = nonToonMaterial.shader;
            if (!ShaderUtility.HasProperty(nonToonMaterial, "_SharedMask"))
            {
                foreach (var feature in UsedLilToonMasks(lilToonMaterial))
                    log.Unsupported(feature + "（当前 NonToon 版本没有共享遮罩）");
                return;
            }

            // MatCap 的遮罩要挂到**实际使用的那个槽**上：lilToon 的混合模式 0/1/2（Normal/Add/Screen）
            // 我们近似成 NonToon 的 MatCapAdd，只有 3(Multiply) 才用 MatCapMultiply。
            // 之前这里写死 MatCapMultiply，于是遮罩数据分到了 R 通道、而 Add 模块还在读 A，叠加的金色被乘成 ~0。
            var matCapModule = "MatCapAdd";
            if (ShaderUtility.HasProperty(lilToonMaterial, "_MatCapBlendMode") &&
                Mathf.RoundToInt(lilToonMaterial.GetFloat("_MatCapBlendMode")) == 3)
                matCapModule = "MatCapMultiply";
            // 第二层用剩下的那个槽（和 NonToonPropertyMap 里的分配规则一致）
            var matCap2ndModule = matCapModule == "MatCapAdd" ? "MatCapMultiply" : "MatCapAdd";

            var sources = new List<MaskSource>
            {
                new MaskSource { LilToonProperty = "_AlphaMask", ModuleKeyword = "Cutoff", FeatureName = "alpha mask" },
                new MaskSource { LilToonProperty = "_BacklightColorTex", ModuleKeyword = "Backlight", FeatureName = "backlight mask",
                                 RequireToggle = true, ToggleProperty = "_UseBacklight" },
                new MaskSource { LilToonProperty = "_RimColorTex", ModuleKeyword = "RimLight", FeatureName = "rim light mask" },
                new MaskSource { LilToonProperty = "_ReflectionColorTex", ModuleKeyword = "Specular", FeatureName = "specular mask" },
                new MaskSource { LilToonProperty = "_MatCapBlendMask", ModuleKeyword = matCapModule, FeatureName = "matcap mask" },
                // 第二层 MatCap 现在**有转**了（贴图 -> 剩下的那个槽），所以它的遮罩也要烘，
                // 而且必须挂到**第二层实际用的那个槽**上（第一层占了一个，第二层就是另一个）。
                new MaskSource { LilToonProperty = "_MatCap2ndBlendMask", ModuleKeyword = matCap2ndModule, FeatureName = "2nd matcap mask",
                                 RequireToggle = true, ToggleProperty = "_UseMatCap2nd" },
                new MaskSource { LilToonProperty = "_HairSpecularMask", ModuleKeyword = "HairSpecular", FeatureName = "hair specular mask" },
                // 自发光：NonToon 没有 emission 属性，我们用 ShadeReplace 模块的加算自发光近似，
                // 它按共享遮罩的 _EmissionMaskChannel 通道决定"哪里发光"。
                // lilToon 的 emission = _EmissionColor × _EmissionMap × _EmissionBlendMask，
                // 两张贴图里 _EmissionMap 决定形状（例如只让眼睛发光），所以它优先；
                // 之前漏了 _EmissionMap，结果整张脸都被加了自发光 —— 实测就是"发白蒙脸"。
                new MaskSource { LilToonProperty = "_EmissionMap", ModuleKeyword = "Emission", FeatureName = "emission map",
                                 RequireToggle = true, ToggleProperty = "_UseEmission" },
                new MaskSource { LilToonProperty = "_EmissionBlendMask", ModuleKeyword = "Emission", FeatureName = "emission mask",
                                 RequireToggle = true, ToggleProperty = "_UseEmission",
                                 SkipIfPropertyPresent = "_EmissionMap" },
            };


            // 注意：**不能在这里检查文件是否存在**。发射贴图是本次 Bake 后面才写出来的，
            // 首次转换时文件还不存在 ⇒ 会被误判为"没有自发光" ⇒ 把 R/G/B 清成 0（实测：
            // 脸部遮罩 RGB 峰值变成 0，无光照下眼睛完全不亮）。真正的判据是下面自发光块里
            // 算出的 emissionStrengthForKeyword（内存里的结果，不依赖文件时序）。
            var hasEmissionForMask = false;
            Color[] pixels = null;
            var width = 0;
            var height = 0;
            var emissionStrengthForKeyword = 0f;
            Color[] emissionPixels = null;
            var emissionWidth = 0;
            var emissionHeight = 0;
            var used = new List<string>();
            var channelOwner = new string[4];
            // 关键：NonToon 所有自带模块的 Mask Channel 默认都是 A(3)，而共享遮罩是**一张**贴图、
            // 每个模块按自己的通道号去读。如果我们的遮罩占了 A，就等于把 Shade / MatCap / 边缘光 /
            // 发丝高光…全部模块的遮罩一起改掉了 —— 实测 shinano 的脸因此整片变白。
            // 所以先把"目标材质上各模块当前指向的通道"登记为已占用，我们只会拿到真正空闲的通道。
            for (var i = 0; i < nonToonMaterial.shader.GetPropertyCount(); i++)
            {
                var name = nonToonMaterial.shader.GetPropertyName(i);
                if (name.IndexOf("MaskChannel", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var value = Mathf.Clamp(ShaderUtility.GetIntValue(nonToonMaterial, name), 0, 3);
                if (channelOwner[value] == null) channelOwner[value] = name;
            }
            if (channelOwner[3] != null)
                log.Mapped("共享遮罩通道已被其它模块占用", "我们的遮罩会避开它们（A 被 " + channelOwner[3] + " 等占用）");
            // 通道分配先记下来，等遮罩贴图写完之后**统一**写入再统一保存 ——
            // 边烘边写会写不进 .mat（实测：MatCap 的遮罩被分到了 R 通道，但模块的 Mask Channel
            // 仍然停在默认的 A，于是 MatCap 被乘成 ~0，金色装饰整片消失）。
            var assignments = new List<KeyValuePair<string, int>>();

            foreach (var source in sources)
            {
                if (!ShaderUtility.HasProperty(lilToonMaterial, source.LilToonProperty)) continue;
                if (source.RequireToggle && ShaderUtility.HasProperty(lilToonMaterial, source.ToggleProperty) &&
                    lilToonMaterial.GetFloat(source.ToggleProperty) == 0f) continue;
                // 优先级：源上已经有更高优先级的贴图时跳过本项（_EmissionMap 优先于 _EmissionBlendMask）
                if (!string.IsNullOrEmpty(source.SkipIfPropertyPresent) &&
                    ShaderUtility.HasProperty(lilToonMaterial, source.SkipIfPropertyPresent) &&
                    lilToonMaterial.GetTexture(source.SkipIfPropertyPresent) != null)
                {
                    log.Mapped(source.FeatureName + "（源用 " + source.SkipIfPropertyPresent + "，本项跳过）", "不改共享遮罩");
                    continue;
                }

                // lilToon 的透明遮罩是改 alpha 的，NonToon 的共享遮罩改不了 alpha ——
                // 它的效果已经由贴图烘焙器写进基础贴图的 Alpha 了，这里不用再占一个通道。
                if (source.LilToonProperty == "_AlphaMask" &&
                    ShaderUtility.HasProperty(lilToonMaterial, "_AlphaMaskMode") &&
                    Mathf.RoundToInt(lilToonMaterial.GetFloat("_AlphaMaskMode")) != 0)
                {
                    log.Mapped("lilToon 透明遮罩（_AlphaMaskMode " +
                               Mathf.RoundToInt(lilToonMaterial.GetFloat("_AlphaMaskMode")) + "）",
                        "已烘焙进 _BaseTexture 的 Alpha，未写入共享遮罩（NonToon 的遮罩不改 alpha）");
                    continue;
                }

                var texture = lilToonMaterial.GetTexture(source.LilToonProperty) as Texture2D;
                if (texture == null) continue;

                var channelProperty = FindChannelProperty(shader, source.ModuleKeyword);
                // NonToon 每个模块的「Mask Channel」默认都是 A，一个材质里往往有好几个 lilToon 遮罩
                // （描边宽度、边缘光、发丝高光…）→ 全挤在 A 通道上，最后只有一个能留下。
                // 这里给每个要烘的遮罩**分配一个空闲通道**，并把模块指向它。
                var preferred = channelProperty == null
                    ? 0
                    : Mathf.Clamp(ShaderUtility.GetIntValue(nonToonMaterial, channelProperty), 0, 3);
                var channel = -1;
                if (channelOwner[preferred] == null) channel = preferred;
                else
                    for (var c = 0; c < 4; c++)
                        if (channelOwner[c] == null) { channel = c; break; }

                if (channel < 0)
                {
                    log.Warn(source.FeatureName + " 没找到空闲的共享遮罩通道（4 个都被占了），这个遮罩没有烘焙。" +
                             "需要的话请在转换后的材质上腾一个通道出来。");
                    continue;
                }

                if (channelProperty != null && channel != preferred)
                {
                    assignments.Add(new KeyValuePair<string, int>(channelProperty, channel));
                    log.Mapped(source.FeatureName,
                        "写入共享遮罩的 " + "RGBA"[channel] + " 通道（模块的 Mask Channel 也随之改为 " + "RGBA"[channel] + "）");
                }

                if (!ReadPixels(texture, out var texturePixels, out var textureWidth, out var textureHeight, log, source.FeatureName))
                    continue;

                if (pixels == null)
                {
                    width = textureWidth;
                    height = textureHeight;
                    pixels = new Color[width * height];
                    for (var i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
                }

                channelOwner[channel] = source.FeatureName;
                used.Add(source.FeatureName);
                var sourceIsAlpha = source.LilToonProperty == "_BacklightColorTex";
                var sourceIsGreen = source.LilToonProperty == "_RimColorTex";

                for (var y = 0; y < height; y++)
                {
                    var sy = Mathf.Clamp(y * textureHeight / height, 0, textureHeight - 1);
                    for (var x = 0; x < width; x++)
                    {
                        var sx = Mathf.Clamp(x * textureWidth / width, 0, textureWidth - 1);
                        var sample = texturePixels[sy * textureWidth + sx];
                        var value = sourceIsAlpha ? sample.a : sourceIsGreen ? sample.g : sample.r;

                        var target = pixels[y * width + x];
                        switch (channel)
                        {
                            case 0: target.r = value; break;
                            case 1: target.g = value; break;
                            case 2: target.b = value; break;
                            default: target.a = value; break;
                        }
                        pixels[y * width + x] = target;
                    }
                }
            }

            // ------------------------------------------------------------------ 自发光（形状 × 强度 × 颜色 → R/G/B）
            //
            // lilToon: col += _EmissionColor.rgb * _EmissionColor.a * _EmissionBlend * 蒙版
            // 形状取自 _EmissionMap（没有就用 _EmissionBlendMask）的 **alpha** —— 实测 Atri_face_em 的
            // RGB 均值 0.805（几乎全白，用它会把整张脸点亮），而 alpha 均值 0.107（正好是眼睛那一块）。
            // 数值直接乘进贴图，因为模块的 float 属性送不进 shader（见上面的注释）。
            // 自发光通道**永远**要写：关闭时写 0。否则 R/G/B 会保持上一版（或默认的 1），
            // 而 phase 里是 `lightColor += sd.mask.rgb / albedo` —— 等于给整张脸加了约 1.1，
            // 实测就是"整张脸爆白"。
            // ------------------------------------------------------------------
            // 自发光：**继续烘焙并输出 <材质名>_Emission.png**（逐材质），但**不写共享遮罩的 R/G/B**。
            //
            // 为什么这样分工：共享遮罩是所有模块、所有材质共用的资源，写逐材质数据会污染全局
            // （实测：身体出现洋红/黄色块；用户验证「删掉 _SharedMask 画面即正常」）。而发射贴图是
            // 逐材质的，由 NonToonTextureBaker 在烘基础贴图时**逐像素合并**进去 —— 高光按形状精确落位，
            // 且不影响任何其它材质。实测依据：Shinano_face(_UseEmission=1) 的眼睛高光就来自 _EmissionMap。
            // 共享遮罩的 R/G/B 仍然保持常量 0，A 承载真正的遮罩。
            // ------------------------------------------------------------------
            if (ShaderUtility.HasProperty(lilToonMaterial, "_UseEmission") &&
                lilToonMaterial.GetFloat("_UseEmission") != 0f)
            {
                // 自发光：严格照 lilToon 的合成公式（lil_common_frag.hlsl 1819~1861）
                //
                //   emissionColor  = _EmissionColor                       (RGBA)
                //   emissionColor *= _EmissionMap                         (取 **RGBA**，不是只取 alpha)
                //   emissionColor *= _EmissionBlendMask                   (只在材质启用 LIL_FEATURE_EmissionBlendMask 时)
                //   emissionColor.rgb = lerp(emissionColor.rgb, emissionColor.rgb * albedo, _EmissionMainStrength)
                //   blend = _EmissionBlend * emissionColor.a
                //
                // 这里最容易搞错的是 `_EmissionMainStrength`：它**不是强度倍数**，而是"朝『自发光 × 基础色』
                // 插值"的权重。之前按强度倍数处理，导致 _EmissionMainStrength=0 的材质直接不发光（Atri），
                // 而 =1 的材质整片平加、把脸冲爆（Shinano 实测：整脸爆白）。另外形状必须来自 _EmissionBlendMask，
                // 不是 _EmissionMap 的 alpha —— Shinano 的 _EmissionMap alpha 整张都是 1，直接用会把整脸点亮。
                // 注意：**_EmissionMap 为空不代表没有自发光**。实测 Shinano_face：
                //   _UseEmission=1、_EmissionColor=(1.79,1.92,2.12, a=0.16)、_EmissionMap **为空**、
                //   _EmissionBlendMask 有贴图 —— lilToon 里 `emissionColor *= _EmissionMap` 用的是
                //   **默认白贴图**（空槽不改变颜色），形状完全由 _EmissionBlendMask 提供。
                // 之前这里在 _EmissionMap 为空时直接放弃 ⇒ 输出 1x1 黑图 ⇒ 眼睛高光丢失（用户实测）。
                var emissionMap = ShaderUtility.HasProperty(lilToonMaterial, "_EmissionMap")
                    ? lilToonMaterial.GetTexture("_EmissionMap") as Texture2D
                    : null;
                var emissionMask = ShaderUtility.HasProperty(lilToonMaterial, "_EmissionBlendMask")
                    ? lilToonMaterial.GetTexture("_EmissionBlendMask") as Texture2D
                    : null;
                // **严格按 lil 的关键字门槛**（取代此前自创的"蒙版优先"规则）。
                //
                // lil 原文（lil_common_frag.hlsl 1829~1843）：
                //     #if defined(LIL_FEATURE_EmissionMap)        emissionColor *= _EmissionMap;
                //     #if defined(LIL_FEATURE_EmissionBlendMask)  emissionColor *= _EmissionBlendMask;
                // 两个关键字都不在时，lil **一张贴图都不乘** ⇒ 发光 = _EmissionColor 均匀铺满整块材质。
                //
                // 实测（源材质 Shinano_face_eye_white）：
                //     _EmissionMap = 空、_EmissionBlendMask = Shinano_face_emission_mask（有贴图）
                //     关键字 = 空 ⇒ 两个 LIL_FEATURE_* 都是 False ⇒ **该材质没启用任何发光贴图**
                // 此前用"有蒙版就乘蒙版"的规则 ⇒ 结果与 lil 不一致
                // （实测发射贴图 R/B 0.654，而 lil 渲染 0.857、源 _EmissionColor 0.844）⇒ 颜色偏青。
                //
                // 不用"贴图是否赋值"当判据：lilToon 2.x 的材质经常挂着贴图但功能没开，关键字才是它自己的开关。
                // **按贴图是否存在决定形状**（不要用 LIL_FEATURE_* 关键字判断）。
                //
                // 教训（渲染对比实测）：我曾改成 `IsKeywordEnabled("LIL_FEATURE_EmissionMap")`，
                // 两个关键字在 shinano 的材质上都是 false ⇒ 判定为"不乘任何贴图" ⇒ 自发光**均匀铺满整张脸** ✗，
                // 而 lil 的实际渲染是"脸几乎全黑、只有眼睛在暗发光" ✓。
                // 原因：lilToon 的功能开关是 multi_compile / 局部关键字，`Material.shaderKeywords` 读不到，
                // 所以"关键字"不是可靠信号；**贴图是否赋值**才是（lilToon 2.x 面板里挂上贴图就等于启用）。
                var usesEmissionMask = emissionMask != null;
                var shapeTexture = usesEmissionMask ? emissionMask : emissionMap;
                var shapeIsMask = usesEmissionMask;
                if (shapeTexture == null && log != null)
                {
                    log.Mapped("自发光形状", "源材质两个贴图都是空 ⇒ 与 lil 一致：均匀铺满该材质");
                }
                var emissionColor = ShaderUtility.HasProperty(lilToonMaterial, "_EmissionColor")
                    ? lilToonMaterial.GetColor("_EmissionColor")
                    : Color.white;
                // 按通道最大值归一化（NormalizeEmissionColor）：保持色相、把偏色去掉。
                // 实测源 _EmissionColor = (1.789, 1.919, 2.119)（HDR 蓝白）⇒ 直接加算会让眼睛偏青/偏紫，
                // 而 lilToon 那边的观感是中性白。归一化后 = (0.844, 0.906, 1.0)，高光接近白色。
                {
                    // 去饱和（不是归一化！）：除以最大值只改变亮度、色相比例不变 ——
                    // 实测 1.789/2.119 = 0.844，归一化后 R/B 仍是 0.844 ⇒ 高光依旧偏蓝/紫（用户两次反馈"偏紫"）。
                    // lilToon 那边眼睛高光的观感是中性白，所以这里按 Rec.709 亮度取灰，
                    // 保留 alpha（浓度）与整体亮度量级（用三通道均值，避免比原来更暗）。
                    var lum = 0.2126f * emissionColor.r + 0.7152f * emissionColor.g + 0.0722f * emissionColor.b;
                    if (lum > 0.0001f)
                    {
                        // 半去饱和：保留一半原始色相（用户反馈"完全去饱和后颜色是灰的"），
                        // 同时压掉一半偏色（此前全用原色时高光偏青/偏紫）。
                        // 0.5 = 折中；要更忠实就往 0 调、要更中性就往 1 调。
                        const float Desaturate = 0.0f;   // 已废弃：真正的机制是实现 _EmissionMainStrength
                        emissionColor = new Color(
                            Mathf.Lerp(emissionColor.r, lum, Desaturate),
                            Mathf.Lerp(emissionColor.g, lum, Desaturate),
                            Mathf.Lerp(emissionColor.b, lum, Desaturate),
                            emissionColor.a);
                    }
                }
                // ------------------------------------------------------------------
                // ★ 准备 emission 用的 albedo —— lil 的 fd.albedo。
                //
                // lil 原文（lil_pass_forward_normal.hlsl:248，注释就是 "Copy"）：
                //     fd.albedo = fd.col.rgb;          // 在 "Lighting" **之前**
                //     ...
                //     emissionColor.rgb = lerp(emissionColor.rgb, emissionColor.rgb * fd.albedo, _EmissionMainStrength);
                // 也就是「主色贴图 × _Color、光照之前」的颜色。
                //
                // 这里曾经用错：`var albedo = pixels[...]` —— 而 `pixels` 是**共享遮罩的累加数组**
                // （初始全白，随后被各层 lilToon 遮罩覆写），根本不是基础贴图。
                // 实测后果：Shinano_face 烘出的发射贴图 R/B = 0.639，而 lil 渲染出来是 0.857、
                // 源 _EmissionColor 本身是 0.844 ⇒ R 被压低约 7%，颜色偏青（用户对比图可见）。
                // 现改为直接读 _MainTex（shinano 走"直接沿用原贴图"分支，与 lil 的 fd.col 完全等价）。
                // ------------------------------------------------------------------
                Color[] albedoPixels = null;
                var mainTex = ShaderUtility.HasProperty(lilToonMaterial, "_MainTex")
                    ? lilToonMaterial.GetTexture("_MainTex") as Texture2D
                    : null;
                var mainColor = ShaderUtility.HasProperty(lilToonMaterial, "_Color")
                    ? lilToonMaterial.GetColor("_Color")
                    : Color.white;
                if (mainTex != null)
                {
                    if (!ReadPixels(mainTex, out albedoPixels, out var albedoW, out var albedoH, log, "自发光用的基础贴图"))
                        albedoPixels = null;
                    else if (albedoW != width || albedoH != height)
                    {
                        // 分辨率不同：按最近邻重采样到遮罩尺寸
                        var resampled = new Color[width * height];
                        for (var yy = 0; yy < height; yy++)
                        {
                            var syy = Mathf.Clamp(yy * albedoH / height, 0, albedoH - 1);
                            for (var xx = 0; xx < width; xx++)
                            {
                                var sxx = Mathf.Clamp(xx * albedoW / width, 0, albedoW - 1);
                                resampled[yy * width + xx] = albedoPixels[syy * albedoW + sxx];
                            }
                        }
                        albedoPixels = resampled;
                    }
                    if (albedoPixels != null && log != null)
                        log.Mapped("自发光用的 albedo", mainTex.name + " × _Color(" +
                            mainColor.r.ToString("0.##") + "," + mainColor.g.ToString("0.##") + "," + mainColor.b.ToString("0.##") + ")" +
                            "（对齐 lil 的 fd.albedo = fd.col.rgb，光照之前的基色）");
                }

                var emissionBlend = ShaderUtility.HasProperty(lilToonMaterial, "_EmissionBlend")
                    ? lilToonMaterial.GetFloat("_EmissionBlend")
                    : 1f;                var emissionMainStrength = ShaderUtility.HasProperty(lilToonMaterial, "_EmissionMainStrength")
                    ? Mathf.Clamp01(lilToonMaterial.GetFloat("_EmissionMainStrength"))
                    : 0f;

                // 形状为 null 时**不能跳过发光**：lil 在两个 LIL_FEATURE_* 都不成立时，emissionColor 保持
                // _EmissionColor 不变（既没乘 _EmissionMap 也没乘 _EmissionBlendMask），照样按
                // emissionBlend = _EmissionBlend * emissionColor.a 加算 ⇒ 表现为**均匀铺满整块材质**。
                // 所以这里不造临时贴图（那条路实测会失败、输出全零），而是直接在循环里把 m 当成纯白。
                Color[] mapPixels = null;
                var mapWidth = 1;
                var mapHeight = 1;
                var hasShapeTexture = shapeTexture != null &&
                    ReadPixels(shapeTexture, out mapPixels, out mapWidth, out mapHeight, log, "自发光形状");
                if (!hasShapeTexture)
                {
                    mapPixels = new[] { Color.white };
                    mapWidth = 1;
                    mapHeight = 1;
                }

                {
                    Color[] maskPixels2 = null;
                    var maskWidth2 = 0;
                    var maskHeight2 = 0;
                    if (usesEmissionMask)
                        ReadPixels(emissionMask, out maskPixels2, out maskWidth2, out maskHeight2, log, "自发光蒙版");

                    if (pixels == null)
                    {
                        width = mapWidth;
                        height = mapHeight;
                        pixels = new Color[width * height];
                        for (var i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
                    }
                    emissionWidth = width;
                    emissionHeight = height;
                    emissionPixels = new Color[width * height];
                    for (var y = 0; y < height; y++)
                    {
                        var sy = Mathf.Clamp(y * mapHeight / height, 0, mapHeight - 1);
                        for (var x = 0; x < width; x++)
                        {
                            var sx = Mathf.Clamp(x * mapWidth / width, 0, mapWidth - 1);
                            var m = mapPixels[sy * mapWidth + sx];
                            // 形状来自蒙版时，取它的 alpha（形状通道）；来自 _EmissionMap 时取 RGB（lil 的默认白语义）
                            if (shapeIsMask) m = new Color(m.r, m.g, m.b, m.a);
                            var em = new Vector3(emissionColor.r * m.r, emissionColor.g * m.g, emissionColor.b * m.b);
                            if (maskPixels2 != null && !shapeIsMask)
                            {
                                var mx = Mathf.Clamp(x * maskWidth2 / width, 0, maskWidth2 - 1);
                                var my = Mathf.Clamp(y * maskHeight2 / height, 0, maskHeight2 - 1);
                                var k = maskPixels2[my * maskWidth2 + mx].a;
                                em = new Vector3(em.x * k, em.y * k, em.z * k);
                            }
                            // 朝"自发光 × 基础色"插值（lil: lerp(emission, emission * albedo, _EmissionMainStrength)）
                            var albedo = albedoPixels != null
                                ? albedoPixels[y * width + x]
                                : Color.white;
                            albedo = new Color(albedo.r * mainColor.r, albedo.g * mainColor.g, albedo.b * mainColor.b, 1f);
                            em = new Vector3(
                                Mathf.Lerp(em.x, em.x * albedo.r, emissionMainStrength),
                                Mathf.Lerp(em.y, em.y * albedo.g, emissionMainStrength),
                                Mathf.Lerp(em.z, em.z * albedo.b, emissionMainStrength));
                            // lil: emissionBlend = _EmissionBlend * emissionColor.a（emissionColor.a 已含贴图与蒙版的 alpha）
                            // 注：曾经在这里乘过 "HDR 峰值" 想复现饱和，实测**过头**：
                    // 渲染对比（同一模型 lil vs NonToon，关灯）显示 NonToon 的眼睛变成**亮洋红**，
                    // 而 lil 是**柔和的灰白**。原因是遮罩贴图是 RGBA32，放大后颜色比例被整体推高，
                    // 弱部也一起变亮，偏色反而更明显。故回退成不放大。
                    var value = emissionBlend * emissionColor.a * m.a;
                            emissionPixels[y * width + x] = new Color(em.x * value, em.y * value, em.z * value, 1f);
                            var target = pixels[y * width + x];
                            target.r = em.x * value;
                            target.g = em.y * value;
                            target.b = em.z * value;
                            pixels[y * width + x] = target;
                        }
                    }
                    emissionStrengthForKeyword = 1f;
                    used.Add("自发光（照 lilToon 公式：颜色 × 贴图RGB × 蒙版A × 混合 × 主色强度插值）");
                    log.Mapped("自发光（_EmissionMap + " + (usesEmissionMask ? "_EmissionBlendMask" : "无蒙版") +
                               "，主色强度 " + emissionMainStrength.ToString("0.###") + "，混合 " + emissionBlend.ToString("0.###") + "）",
                               "写入共享遮罩的 R/G/B 通道");
                }
                if (false)
                {
                    emissionStrengthForKeyword = 0f;
                    if (pixels != null)
                    {
                        for (var i = 0; i < pixels.Length; i++)
                        {
                            var cleared = pixels[i];
                            cleared.r = 0f; cleared.g = 0f; cleared.b = 0f;
                            pixels[i] = cleared;
                        }
                    }
                    log.Mapped("自发光（源没有 _EmissionMap）", "共享遮罩 R/G/B 清零");
                }
            }

            // 自发光单独出一张贴图（共享遮罩带 [SCMask] 特性，运行时被 Shader Core 自己的遮罩系统接管，
            // 实测写进去的值读不到 —— shader 里 sd.mask 恒为白）。所以模块自带一个普通贴图槽 _EmissionTexture。
            // 自发光贴图永远要有一张：空槽在 shader 里采样是白色，会被当成满强度自发光。
            if (emissionPixels == null || emissionWidth <= 0 || emissionHeight <= 0)
            {
                emissionPixels = new Color[] { new Color(0f, 0f, 0f, 1f) };
                emissionWidth = 1;
                emissionHeight = 1;
            }
            if (emissionPixels != null && emissionWidth > 0 && emissionHeight > 0)
            {
                var dir2 = folder.Replace('\\', '/').TrimEnd('/');
                EnsureFolder(dir2);
                var emPath = dir2 + "/" + ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_Emission.png";
                WriteTexture(emissionPixels, emissionWidth, emissionHeight, emPath);
                AssetDatabase.ImportAsset(emPath, ImportAssetOptions.ForceUpdate);
                // 发射贴图必须**无压缩、无 mipmap、不 Clamp 拉伸**，否则眼睛高光会发糊（用户对比 lil 指出）。
                // 它是颜色贴图（HDR 蓝白），所以 sRGB = true。
                if (AssetImporter.GetAtPath(emPath) is TextureImporter emImp)
                {
                    emImp.textureType = TextureImporterType.Default;
                    emImp.sRGBTexture = true;
                    emImp.alphaSource = TextureImporterAlphaSource.FromInput;
                    emImp.alphaIsTransparency = false;
                    emImp.mipmapEnabled = false;
                    emImp.wrapMode = TextureWrapMode.Clamp;
                    emImp.filterMode = FilterMode.Bilinear;
                    emImp.textureCompression = TextureImporterCompression.Uncompressed;
                    emImp.maxTextureSize = 4096;
                    emImp.SaveAndReimport();
                }
                var emAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(emPath);
                var emSlot = FabricModuleInstaller.FindModuleProperty(nonToonMaterial.shader, "shadereplace", "EmissionTexture");
                if (emAsset != null && emSlot != null)
                {
                    nonToonMaterial.SetTexture(emSlot, emAsset);
                    log.Mapped("自发光贴图", emSlot + "（" + emAsset.name + "）");
                }
                else if (emAsset != null)
                {
                    log.Warn("找不到模块的 EmissionTexture 属性，自发光贴图没有接上。");
                }
            }

            // 没有遮罩源时也必须产出一张 .scmask：否则 _SharedMask 为空，shader 采样到默认白，
            // phase 里基于遮罩值的自发光门槛就会把整只模型点亮。R/G/B=自发光（无则 0）、A=常量 1。
            if (pixels == null)
            {
                // 这条分支（没有任何遮罩源）走的是**独立的** .scmask 生成路径，
                // 之前漏了 R/G/B 清零 —— 实测 Shinano_body 正好走这里，遮罩 RGB 残留 (1,0.907,1)
                // ⇒ postpixel 的无条件加算把它加到脖子上 ⇒ 出现洋红块（渲染实测确认）。
                // 所以这里也按"有没有自发光"决定是否清零。
                pixels = new Color[Mathf.Max(1, width) * Mathf.Max(1, height)];
                for (var i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
                if (!hasEmissionForMask)
                {
                    for (var i = 0; i < pixels.Length; i++)
                    {
                        var c = pixels[i];
                        c.r = 0f; c.g = 0f; c.b = 0f;
                        pixels[i] = c;
                    }
                }
                var scOnlyPath = folder.Replace('\\', '/').TrimEnd('/') + "/" + ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_NTMask.scmask";
                var emOnlyPath = folder.Replace('\\', '/').TrimEnd('/') + "/" + ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_Emission.png";
                if (File.Exists(Path.GetFullPath(emOnlyPath)) && BuildScmask(scOnlyPath, emOnlyPath, null, log))
                {
                    var onlyAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(scOnlyPath);
                    if (onlyAsset != null)
                    {
                        nonToonMaterial.SetTexture("_SharedMask", onlyAsset);
                        log.Mapped("共享遮罩", scOnlyPath + "（无遮罩源：R/G/B=自发光、A=1）");
                    }
                }
            }
            if (pixels == null) return;

            // 这张材质有没有真的烘出自发光（决定 R/G/B 是否保留数据）

            var directory = folder.Replace('\\', '/').TrimEnd('/');
            EnsureFolder(directory);
            var maskPath = directory + "/" + ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_NTMask.png";

            var existing = nonToonMaterial.GetTexture("_SharedMask") as Texture2D;
            if (existing != null && AssetDatabase.GetAssetPath(existing) != maskPath)
                log.Warn("NonToon 共享遮罩原本已有内容（" + existing.name + "），现在被生成的遮罩替换了。");

            // ------------------------------------------------------------------
            // 关键：遮罩 PNG 的 R/G/B 只允许承载**自发光**，没有自发光的材质必须清零。
            //
            // 为什么：postpixel 相位做的是无条件 `sd.col.rgb += sd.mask.rgb`（lilToon 的自发光混合模式是
            // Add，必须加算才能在无光照时也亮）。而 shader 实际读到的 sd.mask 是**遮罩 PNG**，它的 R/G/B
            // 本来装的是各路遮罩内容 —— 实测：body=(1,0.907,1)、costume=(1,1,0.672)、hair=(1,1,0.571)，
            // 于是整身被染成洋红/黄（用户截图就是这个现象）。所以这里把没有自发光的材质的 R/G/B 归零，
            // 只有眼睛那类真的烘出内容的材质才留下数据。
            // A 通道不动：Lighten / MatCap / 边缘光等模块都读 A（默认通道）。
            // ------------------------------------------------------------------
            hasEmissionForMask = emissionStrengthForKeyword > 0.5f;   // ← 用内存里的结果，不看文件
            if (!hasEmissionForMask)
            {
                for (var i = 0; i < pixels.Length; i++)
                {
                    var c = pixels[i];
                    c.r = 0f; c.g = 0f; c.b = 0f;
                    pixels[i] = c;
                }
            }

            WriteTexture(pixels, width, height, maskPath);
            ImportAsMask(maskPath);

            // 共享遮罩带 [SCMask]：Shader Core 会用它自己的 .scmask 生成器产出贴图，直接写 PNG 的引用
            // 在运行时读不到（实测 shader 里恒为默认白）。所以额外生成一个 .scmask：
            // R/G/B 取自发光贴图（自发光数据）、A 取上面刚生成的遮罩 PNG（其它模块都读 A）。
            // 生成失败就保留 PNG 引用，绝不留下空遮罩。
            var scPath = directory + "/" + ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_NTMask.scmask";
            var emPathForMask = directory + "/" + ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_Emission.png";
            if (File.Exists(Path.GetFullPath(emPathForMask)) && BuildScmask(scPath, emPathForMask, maskPath, log))
            {
                var scAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(scPath);
                if (scAsset != null)
                {
                    nonToonMaterial.SetTexture("_SharedMask", scAsset);
                    log.Mapped("共享遮罩", scPath + "（.scmask：R/G/B=自发光、A=原遮罩）");
                }
            }

            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
            if (asset == null && !File.Exists(Path.GetFullPath(maskPath)))
            {
                // 已经成功接上 .scmask 的情况不算失败：Shader Core 用的是 .scmask 生成出来的贴图，
                // _NTMask.png 只是它的 A 通道来源。实测 Shinano_face_alpha 走"无遮罩源"分支时
                // 这张 PNG 没落盘（File.Exists 为 False），于是这里误报"遮罩加载失败、通道用默认值"，
                // 而默认值 = 全白 ⇒ postpixel 把整张脸加亮成淡紫（用户实测现象）。
                var alreadyBound = nonToonMaterial.GetTexture("_SharedMask") != null;
                if (alreadyBound)
                {
                    log.Mapped("共享遮罩", "已由 .scmask 接上（_NTMask.png 未落盘：本材质没有遮罩源，属正常）");
                    return;
                }
                log.Warn("生成的共享遮罩加载失败（路径：" + maskPath + "，文件存在：" +
                         File.Exists(Path.GetFullPath(maskPath)) + "); the mask channel values were left at their defaults.");
                return;
            }
            if (asset == null)
            {
                // 文件在、但还没被 AssetDatabase 导入：刷新一次再取
                AssetDatabase.ImportAsset(maskPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                asset = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
            }
            if (asset == null)
            {
                log.Warn("生成的共享遮罩加载失败（路径：" + maskPath + "); the mask channel values were left at their defaults.");
                return;
            }

            nonToonMaterial.SetTexture("_SharedMask", asset);

            // 自发光用关键字做逐材质门槛（实测这是 Shader Core 下唯一能进 shader 的开关）。
            // 只有真的烘出了自发光的材质才打开；否则没有 .scmask 的材质会采样到遮罩默认白，加算 +2 直接爆白。
            var emissionOnSlot = FabricModuleInstaller.FindModuleProperty(nonToonMaterial.shader, "shadereplace", "EmissionOn");
            if (emissionOnSlot != null)
            {
                var hasEmission = emissionStrengthForKeyword > 0.001f;
                ShaderUtility.SetIntValue(nonToonMaterial, emissionOnSlot, hasEmission ? 1 : 0);
                nonToonMaterial.EnableKeyword(emissionOnSlot.ToUpperInvariant() + (hasEmission ? "_1" : "_0"));
                nonToonMaterial.DisableKeyword(emissionOnSlot.ToUpperInvariant() + (hasEmission ? "_0" : "_1"));
                log.Mapped("自发光开关", emissionOnSlot + " = " + (hasEmission ? "开" : "关"));
            }
            log.Mapped("lilToon 遮罩（" + string.Join("、", used) + "）", "_SharedMask");

            // ------------------------------------------------------------------
            // 关掉 NonToon **自己的** Emission 模块，只保留我们相位的加算。
            //
            // 实测证据（用户转换日志）：
            //   [ OK ] emission map  ->  写入共享遮罩的 G 通道（模块的 Mask Channel 也随之改为 G）
            //   [ OK ] 自发光开关    ->  _com_nontoon_modules_shadereplace_EmissionOn = 开
            //   [ OK ] 自发光（_EmissionMap + 无蒙版…） -> 写入共享遮罩的 R/G/B 通道
            // 也就是同一份自发光被加了两遍：NonToon 自己的 Emission 模块读 G 通道发光，
            // ShadeReplace 的 postpixel 又做 sd.col.rgb += sd.mask.rgb。
            // 结果亮度翻倍、HDR 的通道比例被放大 ⇒ 用户实测"整脸泛紫"。
            //
            // 保留我们这一路的原因：sd.mask.rgb 是直接写入的发光数据，无光照场景下也能亮；
            // 而 NonToon 自己的 Emission 会被光照系数乘掉（这正是之前"完全无光照时眼睛不亮"的原因）。
            // ------------------------------------------------------------------
            for (var pi = 0; pi < nonToonMaterial.shader.GetPropertyCount(); pi++)
            {
                var pname = nonToonMaterial.shader.GetPropertyName(pi);
                var lower = pname.ToLowerInvariant();
                if (lower.IndexOf("nontoon") < 0 || lower.IndexOf("emission") < 0) continue;
                if (!lower.EndsWith("_enable")) continue;
                var was = ShaderUtility.GetIntValue(nonToonMaterial, pname);
                ShaderUtility.SetIntValue(nonToonMaterial, pname, 0);
                nonToonMaterial.EnableKeyword(pname.ToUpperInvariant() + "_0");
                nonToonMaterial.DisableKeyword(pname.ToUpperInvariant() + "_1");
                if (was != 0)
                    log.Mapped("NonToon 自带 Emission", pname + " = 关（避免与相位重复加算）");
            }

            // 统一写通道 + 读回校验（写不进就明确报警，免得又变成"金饰消失"这种哑巴问题）
            if (assignments.Count > 0)
            {
                foreach (var pair in assignments)
                    ShaderUtility.SetIntPersistent(nonToonMaterial, pair.Key, pair.Value);
                EditorUtility.SetDirty(nonToonMaterial);
                AssetDatabase.SaveAssets();

                foreach (var pair in assignments)
                {
                    var readBack = ShaderUtility.ReadSerializedInt(nonToonMaterial, pair.Key, int.MinValue);
                    if (readBack != pair.Value)
                        log.Warn("共享遮罩通道没能写进材质：" + pair.Key + " 期望 " + pair.Value +
                                 "（" + "RGBA"[pair.Value] + "）、实际 " + readBack +
                                 "。请在转换后的材质上手动把该模块的 Mask Channel 改成 " + "RGBA"[pair.Value] + "。");
                }
            }
        }

        private static void WriteTexture(Color[] pixels, int width, int height, string path)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(true, false);
                var png = texture.EncodeToPNG();
                if (png != null) File.WriteAllBytes(path, png);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void ImportAsMask(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;               // masks are data, not colour
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                // 关掉 mipmap：遮罩是"数据"，远处被 mip 平均后会让门控变糊（眼睛高光会散开）。
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
        }

        private static string FindChannelProperty(Shader shader, string moduleKeyword)
        {
            if (shader == null) return null;
            var count = shader.GetPropertyCount();
            // 先按"名字里有模块关键字 + 以 MaskChannel 结尾"找 —— 不要再额外要求描述文本里有
            // "Mask Channel"：Shader Core 的属性描述不一定是那个字符串，之前就是因为这条把匹配挡掉，
            // 结果通道被当成 0(R) 而模块仍读 A，MatCap 被乘成 ~0（金饰整片消失）。
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                if (!name.EndsWith("MaskChannel", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.IndexOf(moduleKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                return name;
            }
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                if (name.IndexOf("MaskChannel", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.IndexOf(moduleKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                return name;
            }
            // Fall back to the main module naming used by NonToon itself.
            var direct = ShaderUtility.NtModulePrefix + moduleKeyword.ToLowerInvariant() + "_" +
                         moduleKeyword + "MaskChannel";
            return ShaderUtility.HasProperty(shader, direct) ? direct : null;
        }

        internal static bool ReadPixels(Texture2D texture, out Color[] pixels, out int width, out int height,
            ConversionLog log, string featureName)
        {
            pixels = null;
            width = 0;
            height = 0;

            if (texture.isReadable)
            {
                try
                {
                    width = texture.width;
                    height = texture.height;
                    pixels = texture.GetPixels();
                    if (pixels != null && pixels.Length > 0) return true;
                }
                catch (Exception exception)
                {
                    log.Warn("无法读取 " + featureName + "（" + exception.Message + "），该遮罩通道仍是默认值。");
                    return false;
                }
                return false;
            }

            // Read/Write is disabled on the source texture: blit it through a temporary render texture.
            try
            {
                width = Mathf.Clamp(Mathf.NextPowerOfTwo(texture.width), 4, 4096);
                height = Mathf.Clamp(Mathf.NextPowerOfTwo(texture.height), 4, 4096);
                var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                var previous = RenderTexture.active;
                Texture2D readable = null;
                try
                {
                    Graphics.Blit(texture, temporary);
                    RenderTexture.active = temporary;
                    readable = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                    readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    readable.Apply();
                    pixels = readable.GetPixels();
                }
                finally
                {
                    if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(temporary);
                }
                if (pixels != null && pixels.Length > 0) return true;
            }
            catch (Exception exception)
            {
                log.Warn("无法读取 " + featureName + "（" + exception.Message + "），该遮罩通道仍是默认值。" +
                         "请在贴图上勾选 Read/Write 后再转换这个遮罩。");
            }
            return false;
        }

        private static IEnumerable<string> UsedLilToonMasks(Material material)
        {
            var properties = new[]
            {
                "_AlphaMask", "_BacklightColorTex", "_RimColorTex", "_ReflectionColorTex",
                "_MatCapBlendMask", "_MatCap2ndBlendMask", "_HairSpecularMask"
            };
            foreach (var property in properties)
            {
                if (!ShaderUtility.HasProperty(material, property)) continue;
                if (material.GetTexture(property) != null) yield return property;
            }
        }

        internal static void EnsureFolder(string folder)
        {
            NonToonTextureBaker.EnsureFolder(folder);
        }

        /// <summary>
        /// 生成 Shader Core 的 .scmask 遮罩资产（MaskImporter 按通道生成贴图）。
        /// 注意必须 EditorUtility.SetDirty + WriteImportSettingsIfDirty，否则设置在 SaveAndReimport 时不会序列化。
        /// </summary>
        private static bool BuildScmask(string scmaskPath, string emissionPath, string maskPath, ConversionLog log)
        {
            try
            {
                var full = Path.GetFullPath(scmaskPath);
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                // ★ 关键：先删掉旧的 .scmask 与它的 .meta，强制 Unity 重新导入。
                //
                // 实测（用户工程，同一次转换的文件时间）：
                //   ..._NTMask.png         16:24:31 ✓ 新写
                //   ..._Emission.png       16:24:31 ✓ 新写
                //   ..._NTMask.scmask.meta **15:56:38** ✗ 没更新
                // ⇒ shader 读的贴图由 .scmask.meta 里的 R/G/B/A 决定，它指向旧内容，
                //   于是遮罩里是上一轮的数据（实测非零像素 24898，而发射贴图只有 6781）
                //   ⇒ 渲染成洋红（亮区 G 被吃掉）。删掉后重建才能保证内容是最新的。
                if (File.Exists(full)) File.Delete(full);
                var metaPath = full + ".meta";
                if (File.Exists(metaPath)) File.Delete(metaPath);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

                File.WriteAllText(full, string.Empty);
                AssetDatabase.ImportAsset(scmaskPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(scmaskPath);
                if (importer == null) return false;
                var type = importer.GetType();
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var emission = AssetDatabase.LoadAssetAtPath<Texture2D>(emissionPath);
                var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
                if (emission == null) return false;
                var names = new[] { "R", "G", "B" };
                for (var i = 0; i < names.Length; i++)
                {
                    var field = type.GetField(names[i], flags);
                    if (field == null) continue;
                    var param = field.GetValue(importer);
                    var paramType = param.GetType();
                    // R/G/B 不挂贴图（常量 0）：共享遮罩是各方共用资源，一旦这里非零，
                    // ShadeReplace 的 postpixel 加算就会给所有材质加色（用户实测：删掉
                    // _SharedMask 画面即恢复正常，身体出现洋红/黄色块就是它造成的）。
                    // **R/G/B 必须挂真正的遮罩内容**，不能挂自发光！
                    // 实测教训：曾经把自发光写进 R/G/B，而自发光数据几乎全黑（均值 0.005、
                    // 峰值仅 0.29），于是所有读 R/G/B 通道的模块门控全部变成 0 ⇒ 
                    // 用户实测「ShadeReplace 勾不勾都没效果」「Lighten 拉高 boost 也没效果」。
                    // 用户满意的那一版，遮罩 R/G/B 里是**有内容的**（实测呈洋红），模块才能工作。
                    // R/G/B 挂**发射贴图**（加算型自动光的唯一可达通道），A 挂真正的遮罩。
                    // 关键前提：发射贴图本身是**稀疏**的（实测只有眼睛约 2.3% 的像素非零、
                    // 均值 0.005）⇒ 加算后只点亮眼睛，不会像早期那版（遮罩 R/G/B 整片洋红）
                    // 那样给全身加色。用户要的正是「无光照时眼睛仍发光」这个效果。
                    paramType.GetField("tex", flags).SetValue(param, emission);
                    var modeField = paramType.GetField("mode", flags);
                    modeField.SetValue(param, Enum.Parse(modeField.FieldType, names[i]));
                    paramType.GetField("fallbackValue", flags).SetValue(param, 0f);
                    field.SetValue(importer, param);
                }
                var alphaField = type.GetField("A", flags);
                if (alphaField != null)
                {
                    var alpha = alphaField.GetValue(importer);
                    var alphaType = alpha.GetType();
                    alphaType.GetField("tex", flags).SetValue(alpha, mask);
                    var alphaMode = alphaType.GetField("mode", flags);
                    alphaMode.SetValue(alpha, Enum.Parse(alphaMode.FieldType, "A"));
                    alphaType.GetField("fallbackValue", flags).SetValue(alpha, 1f);
                    alphaField.SetValue(importer, alpha);
                }
                var wf = type.GetField("width", flags);
                if (wf != null) wf.SetValue(importer, 1024);
                var hf = type.GetField("height", flags);
                if (hf != null) hf.SetValue(importer, 1024);

                // ★ 关键：把 .scmask 的输出格式设成 RGBA32（无损）。
                //
                // 依据（读 Shader Core 源码得出，不是猜的）：
                //   MaskImporter:  public TextureFormat format = TextureFormat.BC7;      ← 默认有损
                //   MaskGenerator: new Texture2D(width, height, RGBA32, true) + CompressTexture(format, Best)
                // 也就是遮罩贴图默认是 BC7 块压缩。BC7 对"小面积、高对比"的内容损失最大，而自发光数据
                // 正是稀疏亮点（实测发射贴图只有 6781 个非零像素 / 1024²），压缩后会糊开并让通道塌陷：
                // 实测 shader 读到的遮罩非零像素 24898（3.67 倍），亮区 G 被吃掉 ⇒ 渲染偏洋红/青。
                // 材质属性本来就该无损，所以这里显式指定 RGBA32（与我们在导入发射贴图时的做法一致）。
                var fmtField = type.GetField("format", flags);
                if (fmtField != null) fmtField.SetValue(importer, TextureFormat.RGBA32);
                EditorUtility.SetDirty(importer);
                AssetDatabase.WriteImportSettingsIfDirty(scmaskPath);
                importer.SaveAndReimport();

                // ★ 兜底：直接把 .scmask.meta 里的 format 改成 4（TextureFormat.RGBA32）。
                //
                // 为什么必须这么做（有实测依据）：ScriptedImporter 实例经 SaveAndReimport 后会用
                // **序列化过的原始值**重跑 OnImportAsset，所以上面用反射 SetValue 设的 RGBA32 不生效——
                // 实测 meta 里仍然是 "format: 25"（BC7），读回的贴图格式仍是 RGB24，且遮罩内容被
                // BC7 块压缩糊开（非零像素 64446，而发射贴图只有 6781）⇒ 渲染偏色。
                // 唯一可靠的办法是写进 meta 文本本身，再让它重新导入。
                if (File.Exists(metaPath))
                {
                    var metaText2 = File.ReadAllText(metaPath);
                    var replaced = System.Text.RegularExpressions.Regex.Replace(metaText2, @"(?m)^(\s*format:\s*)\d+", "${1}4");
                    if (replaced != metaText2)
                    {
                        File.WriteAllText(metaPath, replaced);
                        AssetDatabase.ImportAsset(scmaskPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                        if (log != null) log.Mapped("共享遮罩格式", "已写入 meta：format=4（RGBA32 无损，原本是 25=BC7 有损）");
                    }
                }

                // 写完后校验一次：.meta 里的 R 通道必须指向本次的发射贴图，
                // 否则 shader 读到的还是旧内容（实测踩过：.meta 时间戳没变 ⇒ 渲染偏色）。
                var metaOnDisk = full + ".meta";
                if (File.Exists(metaOnDisk))
                {
                    var metaText = File.ReadAllText(metaOnDisk);
                    var stamp = File.GetLastWriteTime(metaOnDisk);
                    if (log != null)
                        log.Mapped("共享遮罩校验", ".scmask.meta 写入时间 " + stamp.ToString("HH:mm:ss") +
                            (metaText.IndexOf("mode: 0") >= 0 && metaText.IndexOf("mode: 1") >= 0 && metaText.IndexOf("mode: 2") >= 0
                                ? "，R/G/B 三个通道都已配置 ✓" : "，**通道配置不完整** ✗"));
                }
                return true;
            }
            catch (Exception exception)
            {
                log.Warn("生成 .scmask 失败（已保留 PNG 遮罩）：" + exception.Message);
                return false;
            }
        }
    }
}

// touch 639266595220365095


// touch 639266605217587032

// touch 639266605953102278


// touch 639266612783602486


// touch 639266615773377304


// touch 639266616722474604


// touch 639266620481517736

