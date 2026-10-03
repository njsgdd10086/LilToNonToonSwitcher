// LilToNonToon Switcher
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    /// <summary>
    /// Texture baking helpers: colour-tinted base textures, shared masks and Shader Core gradient arrays.
    /// </summary>
    public static class NonToonTextureBaker
    {
        public const string GradientsExtension = "scgradients";

        // ------------------------------------------------------------------ folders

        internal static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            if (parts.Length < 2) return;
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        // ------------------------------------------------------------------ pixels

        /// <summary>
        /// Reads a texture as pixels. Textures without Read/Write enabled are copied through a temporary
        /// render texture, so the source texture never has to be modified.
        /// </summary>
        public static bool TryReadPixels(Texture texture, out Color[] pixels, out int width, out int height, ConversionLog log, string what)
        {
            pixels = null;
            width = 0;
            height = 0;
            if (texture == null) return false;

            if (texture is Texture2D texture2D && texture2D.isReadable)
            {
                try
                {
                    width = texture2D.width;
                    height = texture2D.height;
                    pixels = texture2D.GetPixels();
                    if (pixels != null && pixels.Length > 0) return true;
                }
                catch (Exception exception)
                {
                    if (log != null) log.Warn("无法读取 " + what + "（" + exception.Message + "）。");
                    return false;
                }
            }

            try
            {
                // 保持源贴图的**真实尺寸/长宽比**。以前这里用 NextPowerOfTwo 把尺寸向上取整
                //（例如 2048x1943 → 2048x2048），RenderTexture 会把贴图拉伸，UV 全错位 ——
                // 表现就是"衣服上的纹理没了 / 糊了"。只在超过上限时按比例缩小。
                const int maxSize = 4096;
                var longest = Mathf.Max(texture.width, texture.height);
                var scale = longest > maxSize ? maxSize / (float)longest : 1f;
                width = Mathf.Max(4, Mathf.RoundToInt(texture.width * scale));
                height = Mathf.Max(4, Mathf.RoundToInt(texture.height * scale));
                // 用 sRGB 的 RenderTexture + 非 linear 的临时贴图，拿到的才是"贴图里存的那份数值"（sRGB 编码），
                // 和 isReadable 时 GetPixels() 的结果一致。
                // 之前用 ReadWrite.Linear，RT 里存的是线性化后的值，两条路径结果不一样，
                // 后面的烘焙（Gamma 那一步）会因此算得过暗。
                var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var previous = RenderTexture.active;
                Texture2D readable = null;
                try
                {
                    Graphics.Blit(texture, temporary);
                    RenderTexture.active = temporary;
                    readable = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
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
                if (log != null)
                    log.Warn("无法读取 " + what + "（" + exception.Message + "），已保留原贴图引用。");
            }
            return false;
        }

        public static void WritePng(Color[] pixels, int width, int height, string path, bool linear, bool mipmaps)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipmaps, linear);
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

        // ------------------------------------------------------------------ base texture with the main colour baked in

        public static Texture2D BakeBaseTexture(Material lilToonMaterial, Material nonToonMaterial, string folder,
            ConversionLog log)
        {
            // 源材质可能**根本没挂主贴图**（`_MainTex` = fileID 0）—— 那 lilToon 渲染时用的是 shader 里
            // 声明的默认白贴图。这种情况下 `_BaseTexture` 会是空的，但 alpha 相关的东西（透明遮罩的
            // scale/value）依然要烘出来，所以要拿"白底"当像素来源，而不是直接返回。
            var source = nonToonMaterial.GetTexture("_BaseTexture");
            var usesDefaultWhite = source == null;

            var tint = ShaderUtility.HasProperty(lilToonMaterial, "_Color") ? lilToonMaterial.GetColor("_Color") : Color.white;
            var hsvg = ShaderUtility.HasProperty(lilToonMaterial, "_MainTexHSVG")
                ? lilToonMaterial.GetVector("_MainTexHSVG")
                : new Vector4(0f, 1f, 1f, 1f);
            // 注意：不要用 `EFFECT_HUE_VARIATION` 关键字来判断"要不要烘色彩校正"。
            // 实测（lilToon 2.x + `Hidden/lilToonTwoPassTransparent`）关键字是关的，
            // 但 lilToon 渲染时**确实**应用了 `_MainTexHSVG`（粉色布料被烤成白色）——
            // 按关键字跳过会把整件弄成原来的粉紫色。判定方式保持"值 ≠ 默认就烘"。
            var gradationStrength = ShaderUtility.HasProperty(lilToonMaterial, "_MainGradationStrength")
                ? lilToonMaterial.GetFloat("_MainGradationStrength")
                : 0f;
            var gradationTexture = gradationStrength > 0f ? lilToonMaterial.GetTexture("_MainGradationTex") as Texture2D : null;
            var adjustMask = lilToonMaterial.GetTexture("_MainColorAdjustMask") as Texture2D;

            // lilToon 的「透明遮罩」（_AlphaMaskMode）是直接改 alpha 的：
            //   mode 1 用遮罩替换 alpha，2 相乘，3 相加，4 相减，遮罩值先过 scale/value。
            // NonToon 没有这个功能（_SharedMask 只喂给各个模块做范围遮罩），所以只能把
            // 这条链烘进基础贴图的 Alpha —— 半透明轻纱（Veil 之类）就是靠它才有薄纱感。
            var alphaMaskMode = ShaderUtility.HasProperty(lilToonMaterial, "_AlphaMaskMode")
                ? Mathf.RoundToInt(lilToonMaterial.GetFloat("_AlphaMaskMode"))
                : 0;
            var alphaMaskTexture = alphaMaskMode != 0 ? lilToonMaterial.GetTexture("_AlphaMask") as Texture2D : null;
            var alphaMaskScale = ShaderUtility.HasProperty(lilToonMaterial, "_AlphaMaskScale")
                ? lilToonMaterial.GetFloat("_AlphaMaskScale")
                : 1f;
            var alphaMaskValue = ShaderUtility.HasProperty(lilToonMaterial, "_AlphaMaskValue")
                ? lilToonMaterial.GetFloat("_AlphaMaskValue")
                : 0f;
            // 注意：遮罩贴图**没挂**也要算"用了遮罩"。
            // lilToon 采样 _AlphaMask 时，没设置过的属性用的是 shader 里声明的默认贴图（"white" = 1），
            // 所以这时 scale/value 依然生效 —— `_AlphaMaskValue = -0.33` 就是"整体透明度 −33%"，
            // 很多半透明材质（smooth white planet / ring 之类）就是靠这个值，并不需要遮罩贴图。
            var alphaMaskUsed = alphaMaskMode != 0;

            var tintChanged = !Mathf.Approximately(tint.r, 1f) || !Mathf.Approximately(tint.g, 1f) ||
                              !Mathf.Approximately(tint.b, 1f) || !Mathf.Approximately(tint.a, 1f);
            var toneChanged = !Mathf.Approximately(hsvg.x, 0f) || !Mathf.Approximately(hsvg.y, 1f) ||
                              !Mathf.Approximately(hsvg.z, 1f) || !Mathf.Approximately(hsvg.w, 1f);
            var gradationUsed = gradationTexture != null && gradationStrength > 0f;

            // 主色、色调校正（HSV/Gamma）、渐变映射都是默认值、也没用透明遮罩时，直接沿用原贴图。
            if (!tintChanged && !toneChanged && !gradationUsed && !alphaMaskUsed)
            {
                log.Mapped(usesDefaultWhite
                        ? "_MainTex 未指定（lilToon 用 shader 默认白贴图）"
                        : "_MainTex（主色 / 色调校正都是默认值）",
                    usesDefaultWhite ? "_BaseTexture（保持默认白）" : "_BaseTexture（保留原贴图）");
                return null;
            }

            // 色调校正的遮罩和渐变映射：UV 与主贴图一致（lilToon 就是这么采样的）
            Color[] maskPixels = null;
            var maskWidth = 0;
            var maskHeight = 0;
            if (adjustMask != null &&
                !TryReadPixels(adjustMask, out maskPixels, out maskWidth, out maskHeight, null, "the colour adjust mask"))
                maskPixels = null;

            Color[] gradationPixels = null;
            var gradationWidth = 0;
            if (gradationUsed &&
                !TryReadPixels(gradationTexture, out gradationPixels, out gradationWidth, out _, null, "the gradation map"))
                gradationPixels = null;

            Color[] alphaMaskPixels = null;
            var alphaMaskWidth = 0;
            var alphaMaskHeight = 0;
            var alphaMaskScaleUv = Vector2.one;
            var alphaMaskOffsetUv = Vector2.zero;
            if (alphaMaskUsed && alphaMaskTexture != null)
            {
                if (TryReadPixels(alphaMaskTexture, out alphaMaskPixels, out alphaMaskWidth, out alphaMaskHeight, null,
                        "the alpha mask"))
                {
                    // lilToon 采样透明遮罩时用的是主 UV 过一遍 _AlphaMask_ST
                    alphaMaskScaleUv = lilToonMaterial.GetTextureScale("_AlphaMask");
                    alphaMaskOffsetUv = lilToonMaterial.GetTextureOffset("_AlphaMask");
                }
                else
                {
                    alphaMaskPixels = null;
                    log.Warn("lilToon 的透明遮罩（_AlphaMask）贴图读取失败：请给它勾上 Read/Write。" +
                             "这次按默认白遮罩（= 1）计算，遮罩里压暗的部分没有烘进去。");
                }
            }
            else if (alphaMaskUsed)
            {
                log.Mapped("透明遮罩（_AlphaMaskMode " + alphaMaskMode + "）没有挂遮罩贴图",
                    "按 lilToon 的默认白遮罩（= 1）计算 scale/value，" +
                    "即整体透明度偏移 " + alphaMaskValue.ToString("0.###"));
            }

            // ★ 不需要任何校正时，直接沿用原贴图，绝不重新写一张。
            //   实测（公平对比：两边都用 Unity 里的 1024x1024）：重新烘出来的贴图均值 0.818 而原图是 0.731，
            //   暗部 0.299 vs 0.236、亮部直接削平到 1.0 —— 肉眼看就是"衣服干净发白、褶皱消失"。
            //   既然没有色调/渐变/主色要算，拷贝没有任何意义，只会引入损失。
            var tintIsWhite = Mathf.Approximately(tint.r, 1f) && Mathf.Approximately(tint.g, 1f) && Mathf.Approximately(tint.b, 1f);
            if (!toneChanged && gradationPixels == null && tintIsWhite && source is Texture2D)
            {
                nonToonMaterial.SetTexture("_BaseTexture", source);
                log.Mapped("基础贴图（无色调/渐变/主色/透明遮罩需要烘焙）", "直接沿用原贴图 " + source.name + "（不重写，避免提亮与细节损失）");
                return source as Texture2D;
            }

            // 取基础像素：正常情况读原贴图；源材质没挂主贴图时用白底（对应 lilToon 的默认白贴图），
            // 尺寸跟着透明遮罩走，这样遮罩的逐像素差异不会丢。
            Color[] pixels;
            int width;
            int height;
            if (usesDefaultWhite)
            {
                width = alphaMaskPixels != null && alphaMaskWidth > 0 ? alphaMaskWidth : 4;
                height = alphaMaskPixels != null && alphaMaskHeight > 0 ? alphaMaskHeight : 4;
                pixels = new Color[width * height];
                for (var i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
                log.Mapped("_MainTex 未指定（lilToon 用 shader 默认白贴图）", "以白底贴图烘焙主色 / 透明遮罩");
            }
            else if (!TryReadPixels(source, out pixels, out width, out height, log, "the base texture"))
            {
                log.Warn("主色 / 色调校正 / 透明遮罩需要烘焙，但基础贴图读取失败，这些设置没有烘焙进去。" +
                         "请手动设置转换后材质的颜色，或在贴图上勾选 Read/Write。");
                return null;
            }

            // 贴图是不是 sRGB，决定要不要先线性化再算 —— 和 shader 里采样后的空间保持一致，
            // 否则 Gamma 那一步（pow）在 sRGB 数值上算出来的结果会明显不对。
            var sourceIsSrgb = true;
            var sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            if (!string.IsNullOrEmpty(sourcePath) && AssetImporter.GetAtPath(sourcePath) is TextureImporter sourceImporter)
                sourceIsSrgb = sourceImporter.sRGBTexture;
            var linearWork = PlayerSettings.colorSpace == ColorSpace.Linear && sourceIsSrgb;

            var hdrClipped = false;
            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                var rgb = linearWork
                    ? new Vector3(SrgbToLinear(pixel.r), SrgbToLinear(pixel.g), SrgbToLinear(pixel.b))
                    : new Vector3(pixel.r, pixel.g, pixel.b);

                var before = rgb;
                if (toneChanged) rgb = ToneCorrection(rgb, hsvg);
                if (gradationPixels != null) rgb = GradationMap(rgb, gradationPixels, gradationWidth, gradationStrength, linearWork);
                if (maskPixels != null)
                {
                    var x = width > 0 ? Mathf.Clamp((int)((i % width) * (maskWidth / (float)width)), 0, maskWidth - 1) : 0;
                    var y = width > 0 && height > 0
                        ? Mathf.Clamp((int)((i / width) * (maskHeight / (float)height)), 0, maskHeight - 1)
                        : 0;
                    var mask = maskPixels[Mathf.Clamp(y * maskWidth + x, 0, maskPixels.Length - 1)].r;
                    rgb = Vector3.Lerp(before, rgb, mask);
                }

                // 最后才乘主色（含 alpha），顺序和 lilToon 的 `fd.col *= _Color` 一致
                rgb = new Vector3(rgb.x * tint.r, rgb.y * tint.g, rgb.z * tint.b);
                if (rgb.x > 1f || rgb.y > 1f || rgb.z > 1f) hdrClipped = true;
                if (linearWork) rgb = new Vector3(LinearToSrgb(rgb.x), LinearToSrgb(rgb.y), LinearToSrgb(rgb.z));

                // 透明遮罩：lilToon 是在主色之后、cutout 之前改 alpha 的，这里照同样的顺序烘进去。
                var alpha = Mathf.Clamp01(pixel.a * tint.a);
                if (alphaMaskUsed)
                {
                    // 有遮罩贴图就按 UV 采样（过 _AlphaMask_ST），没挂贴图时就是 shader 默认的白贴图 = 1
                    var maskValue = 1f;
                    if (alphaMaskPixels != null && alphaMaskWidth > 0 && alphaMaskHeight > 0)
                    {
                        var u = width > 0 ? (i % width + 0.5f) / width : 0f;
                        var v = width > 0 && height > 0 ? (i / width + 0.5f) / height : 0f;
                        u = Mathf.Repeat(u * alphaMaskScaleUv.x + alphaMaskOffsetUv.x, 1f);
                        v = Mathf.Repeat(v * alphaMaskScaleUv.y + alphaMaskOffsetUv.y, 1f);
                        var mx = Mathf.Clamp((int)(u * alphaMaskWidth), 0, alphaMaskWidth - 1);
                        var my = Mathf.Clamp((int)(v * alphaMaskHeight), 0, alphaMaskHeight - 1);
                        maskValue = alphaMaskPixels[my * alphaMaskWidth + mx].r;
                    }

                    var maskAlpha = Mathf.Clamp01(maskValue * alphaMaskScale + alphaMaskValue);
                    switch (alphaMaskMode)
                    {
                        case 1: alpha = maskAlpha; break;                          // 用遮罩替换 alpha
                        case 2: alpha = alpha * maskAlpha; break;                 // 相乘
                        case 3: alpha = Mathf.Clamp01(alpha + maskAlpha); break;   // 相加
                        case 4: alpha = Mathf.Clamp01(alpha - maskAlpha); break;   // 相减
                    }
                }

                pixels[i] = new Color(Mathf.Clamp01(rgb.x), Mathf.Clamp01(rgb.y), Mathf.Clamp01(rgb.z), alpha);
            }

            var name = ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_Base.png";
            var path = folder.TrimEnd('/') + "/" + name;
            WritePng(pixels, width, height, path, false, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (asset == null)
            {
                log.Warn("带颜色乘算的基础贴图导入失败（路径：" + path + "，文件存在：" +
                         File.Exists(path) + "); the original texture was kept.");
                return null;
            }

            // Keep the source import settings as close as possible.
            if (AssetImporter.GetAtPath(path) is TextureImporter importer &&
                !string.IsNullOrEmpty(sourcePath) &&
                AssetImporter.GetAtPath(sourcePath) is TextureImporter originalImporter)
            {
                importer.sRGBTexture = originalImporter.sRGBTexture;
                importer.alphaSource = alphaMaskPixels != null
                    ? TextureImporterAlphaSource.FromInput      // 透明遮罩烘进了 alpha，必须真的导入 alpha
                    : originalImporter.alphaSource;
                importer.alphaIsTransparency = alphaMaskPixels != null || originalImporter.alphaIsTransparency;
                importer.mipmapEnabled = originalImporter.mipmapEnabled;
                importer.streamingMipmaps = originalImporter.streamingMipmaps;
                importer.maxTextureSize = originalImporter.maxTextureSize;
                importer.textureCompression = originalImporter.textureCompression;
                importer.wrapMode = originalImporter.wrapMode;
                importer.filterMode = originalImporter.filterMode;
                importer.anisoLevel = originalImporter.anisoLevel;
                importer.SaveAndReimport();
            }

            asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            var what = new List<string>();
            if (toneChanged) what.Add("色调校正 HSV/Gamma");
            if (gradationPixels != null) what.Add("渐变映射 " + gradationStrength.ToString("0.###"));
            if (maskPixels != null) what.Add("色调校正遮罩");
            if (tintChanged) what.Add("主色 " + tint);
            if (alphaMaskUsed)
                what.Add("透明遮罩（模式 " + alphaMaskMode + "，scale " + alphaMaskScale.ToString("0.###") +
                         " / value " + alphaMaskValue.ToString("0.###") +
                         (alphaMaskPixels == null ? "，未挂贴图按白=1" : "") + "）→ Alpha");
            log.Mapped("_MainTex + " + string.Join(" + ", what.ToArray()), "_BaseTexture（已烘焙 PNG）");

            if (hdrClipped)
                log.Warn("主色（HDR）超过了 1，烘焙进 8 位贴图时被截断了；" +
                         "如果转换后觉得太暗，可以手动把亮度找回来。");
            return asset;
        }

        // ------------------------------------------------------------------ lilToon 主色处理链（CPU 版）

        /// <summary>lilToon 的 float3 lilToneCorrection(float3 c, float4 hsvg) 的 CPU 版本。</summary>
        private static Vector3 ToneCorrection(Vector3 c, Vector4 hsvg)
        {
            // gamma
            var gamma = Mathf.Approximately(hsvg.w, 0f) ? 1f : hsvg.w;
            c = new Vector3(Mathf.Pow(Mathf.Abs(c.x), gamma), Mathf.Pow(Mathf.Abs(c.y), gamma),
                Mathf.Pow(Mathf.Abs(c.z), gamma));

            // rgb -> hsv（照抄 lilToon 的写法，省得引入不同的分支）
            var p = c.z > c.y ? new Vector4(c.z, c.y, -1f, 2f / 3f) : new Vector4(c.y, c.z, 0f, -1f / 3f);
            var q = p.x > c.x ? new Vector4(p.x, p.y, p.w, c.x) : new Vector4(c.x, p.y, p.z, p.x);
            var d = q.x - Mathf.Min(q.w, q.y);
            const float epsilon = 1e-10f;
            var hue = Mathf.Abs(q.z + (q.w - q.y) / (6f * d + epsilon));
            var saturation = d / (q.x + epsilon);
            var value = q.x;

            // shift
            hue += hsvg.x;
            saturation = Mathf.Clamp01(saturation * hsvg.y);
            value = Mathf.Clamp01(value * hsvg.z);

            // hsv -> rgb
            var f = new Vector3(
                SaturationCurve(hue + 1f),
                SaturationCurve(hue + 2f / 3f),
                SaturationCurve(hue + 1f / 3f));
            var baseValue = value - value * saturation;
            var amplitude = value * saturation;
            return new Vector3(baseValue + amplitude * f.x, baseValue + amplitude * f.y, baseValue + amplitude * f.z);
        }

        private static float SaturationCurve(float hue)
        {
            var wrapped = hue - Mathf.Floor(hue);
            return Mathf.Clamp01(Mathf.Abs(wrapped * 6f - 3f) - 1f);
        }

        /// <summary>
        /// 材质上有没有开某个 shader keyword（lilToon 的很多特性是靠关键字门控的，
        /// 关键字没开时那些属性值根本不参与渲染）。
        /// </summary>
        private static bool IsKeywordOn(Material material, string keyword)
        {
            try
            {
                if (material.IsKeywordEnabled(keyword)) return true;
            }
            catch (Exception)
            {
                // 老版本 Unity 没有这个方法时忽略，退回下面按字符串找
            }

            var shader = material.shader;
            if (shader == null) return false;
            var path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path)) return false;
            foreach (var line in File.ReadAllLines(path))
                if (line.IndexOf(keyword, StringComparison.Ordinal) >= 0 &&
                    line.IndexOf("#pragma shader_feature", StringComparison.Ordinal) >= 0)
                    return true;
            return false;
        }

        /// <summary>lilToon 的 float3 lilGradationMap(float3 col, TEXTURE2D(gradationMap), float strength) 的 CPU 版本。</summary>
        private static Vector3 GradationMap(Vector3 col, Color[] gradation, int gradationWidth, float strength, bool linearWork)
        {
            if (gradation == null || gradationWidth <= 0 || strength <= 0f) return col;

            // lilToon 在 sRGB 空间里采样渐变：线性工程先把颜色转成 sRGB，采样完再转回来
            var sample = linearWork
                ? new Vector3(LinearToSrgb(col.x), LinearToSrgb(col.y), LinearToSrgb(col.z))
                : col;

            var mapped = new Vector3(
                SampleGradation(gradation, gradationWidth, sample.x).r,
                SampleGradation(gradation, gradationWidth, sample.y).g,
                SampleGradation(gradation, gradationWidth, sample.z).b);

            if (linearWork)
                mapped = new Vector3(SrgbToLinear(mapped.x), SrgbToLinear(mapped.y), SrgbToLinear(mapped.z));

            return Vector3.Lerp(col, mapped, Mathf.Clamp01(strength));
        }

        /// <summary>一维渐变：x 轴是输入值，取中间那一行（lilToon 的 LIL_SAMPLE_1D）。</summary>
        private static Color SampleGradation(Color[] gradation, int width, float input)
        {
            if (gradation == null || gradation.Length == 0) return Color.black;
            var height = Mathf.Max(1, gradation.Length / Mathf.Max(1, width));
            var x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(input) * (width - 1)), 0, width - 1);
            var y = Mathf.Clamp(height / 2, 0, height - 1);
            return gradation[Mathf.Clamp(y * width + x, 0, gradation.Length - 1)];
        }

        private static float SrgbToLinear(float value)
        {
            value = Mathf.Clamp01(value);
            return value <= 0.04045f ? value / 12.92f : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);
        }

        private static float LinearToSrgb(float value)
        {
            value = Mathf.Max(0f, value);
            return value <= 0.0031308f ? value * 12.92f : 1.055f * Mathf.Pow(value, 1f / 2.4f) - 0.055f;
        }


        // ------------------------------------------------------------------ Shader Core gradient array (.scgradients)

        /// <summary>
        /// Builds the `.scgradients` asset used by NonToon's Shade / RimShade modules from lilToon's
        /// shadow and rim shade colours. Returns the imported asset and reports which slices were really baked,
        /// so the caller never points a module at a slice that does not exist (that reads as black).
        /// </summary>
        public static UnityEngine.Object BakeGradients(Material lilToonMaterial, Material nonToonMaterial, string folder,
            out int shadeIndex, out int rimShadeIndex, ConversionLog log)
        {
            shadeIndex = -1;
            rimShadeIndex = -1;
            var gradients = new List<Gradient>();
            var used = new List<string>();

            var shadeEnabled = !ShaderUtility.HasProperty(lilToonMaterial, "_UseShadow") ||
                               lilToonMaterial.GetFloat("_UseShadow") != 0f;
            if (shadeEnabled)
            {
                shadeIndex = gradients.Count;
                gradients.Add(BuildShadeGradient(lilToonMaterial, log));
                used.Add("_ShadowColor");
            }

            if (ShaderUtility.HasProperty(lilToonMaterial, "_UseRimShade") &&
                lilToonMaterial.GetFloat("_UseRimShade") != 0f)
            {
                rimShadeIndex = gradients.Count;
                gradients.Add(BuildRimShadeGradient(lilToonMaterial));
                used.Add("_RimShadeColor");
            }

            if (gradients.Count == 0) return null;

            var name = ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_Gradients." + GradientsExtension;
            var path = folder.TrimEnd('/') + "/" + name;
            // 关键：`.scgradients` 的渐变**不在资产文件里** —— GradientsImporter 只读它自己 .meta 里的
            // `gradients` 数组（脚本化导入器的序列化字段）。以前我们把渐变写进资产文件（还伪造了
            // MonoBehaviour YAML），导入器完全忽略，于是每个材质拿到的都是默认渐变「白→白」，
            // Shade 模块乘了个 1 —— 表现为没有阴影、整体偏亮、脸发白。
            // 现在的做法：资产文件留空（和 Unity 自己 Create 出来的一样），渐变写进 .meta 的导入设置。
            File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            WriteGradientsMeta(path, gradients);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
            {
                log.Warn("生成的渐变数组导入失败，NonToon 的阴影颜色仍是默认值。");
                shadeIndex = -1;
                rimShadeIndex = -1;
                return null;
            }

            log.Mapped(string.Join(" + ", used) + "，共 " + gradients.Count + " 层",
                "_SharedGradients（生成的 " + GradientsExtension + "）");
            return asset;
        }

        /// <summary>
        /// lilToon 的阴影在 "x = dot(N,L)*0.5+0.5" 空间里做，每层阴影的过渡窗口是
        /// `[border − blur/2, border + blur/2]`（`lilTooningScale`），阴影色按 alpha 强度叠加：
        ///
        ///     indirect = _ShadowColor.rgb;                                    // 主阴影（不含 alpha）
        ///     indirect = lerp(indirect, _Shadow2ndColor.rgb, a2 * (1 - s2));  // alpha = 强度
        ///     indirect = lerp(indirect, _Shadow3rdColor.rgb, a3 * (1 - s3));  // a3 = 0 时无影响
        ///
        /// NonToon 的 Shade 模块正好也是用同一个 x 采样渐变（`shade ≈ saturate(NdotL)`），
        /// 所以这里**按 lilToon 的公式逐点采样**烘成渐变：位置和软硬都跟 lilToon 一致。
        /// 老版本把关键点放在 `1 - border` 上（等于镜像）而且完全没用 blur —— 结果是分界线跑到
        /// 接近受光的位置、还是硬边 + 渐变贴图的阶梯。
        /// </summary>
        private static Gradient BuildShadeGradient(Material material, ConversionLog log)
        {
            var first = ShaderUtility.HasProperty(material, "_ShadowColor") ? material.GetColor("_ShadowColor") : Color.white;
            var second = ShaderUtility.HasProperty(material, "_Shadow2ndColor")
                ? material.GetColor("_Shadow2ndColor")
                : new Color(0.68f, 0.66f, 0.79f, 1f);
            var third = ShaderUtility.HasProperty(material, "_Shadow3rdColor")
                ? material.GetColor("_Shadow3rdColor")
                : Color.black;

            var border1 = ShaderUtility.HasProperty(material, "_ShadowBorder") ? material.GetFloat("_ShadowBorder") : 0.5f;
            var blur1 = ShaderUtility.HasProperty(material, "_ShadowBlur") ? material.GetFloat("_ShadowBlur") : 0.1f;
            var border2 = ShaderUtility.HasProperty(material, "_Shadow2ndBorder") ? material.GetFloat("_Shadow2ndBorder") : 0.15f;
            var blur2 = ShaderUtility.HasProperty(material, "_Shadow2ndBlur") ? material.GetFloat("_Shadow2ndBlur") : 0.1f;
            var border3 = ShaderUtility.HasProperty(material, "_Shadow3rdBorder") ? material.GetFloat("_Shadow3rdBorder") : 0.25f;
            var blur3 = ShaderUtility.HasProperty(material, "_Shadow3rdBlur") ? material.GetFloat("_Shadow3rdBlur") : 0.1f;
            // lilToon: `lns.x = lerp(1.0, lns.x, _ShadowStrength)` —— 强度是把受光系数往 1 拉，
            // 所以 0.2 表示"阴影只有两成"，我们的渐变也必须按这个比例收着（否则阴影会过重）。
            var strength = ShaderUtility.HasProperty(material, "_ShadowStrength") ? material.GetFloat("_ShadowStrength") : 1f;
            strength = Mathf.Clamp01(strength);
            if (ShaderUtility.HasProperty(material, "_ShadowColorType") && material.GetFloat("_ShadowColorType") != 0f)
                log.Warn("源材质的阴影是「阴影色贴图 / LUT」模式，NonToon 只能按单一阴影色近似，脸上阴影可能不完全一致。");

            // alpha 是"这层阴影的强度"，0 表示作者没在用这一层（lilToon 里 lerp 权重就是 a * (1 - s)）
            first.a = 1f;
            var use2 = second.a > 0.001f;
            var use3 = third.a > 0.001f;
            var secondRgb = new Color(second.r, second.g, second.b, 1f);
            var thirdRgb = new Color(third.r, third.g, third.b, 1f);

            // lilToon 的阴影在窗口内是**分段线性**的，所以只要把"窗口边界"当关键点就完全等价。
            // （Unity 的 Gradient 最多 8 个颜色关键点：0 / 1 + 每层 2 个边界 = 最多 8 个，正好够）
            // 实测：NonToon 自己的 Shade 模块只吃核心属性 + _SharedGradients（我们自建模块的属性到不了 shader），
// 所以裙子这类材质的明暗层次完全取决于色带形状。把过渡窗口按 1.6 倍展宽，暗部会更早进入、
// 中间调随之压暗（实测目标：裙区中位 0.693 → lil 的 0.534）。
            const float WindowBlurScale = 1.6f;
            var stops = new List<float> { 0f, 1f };
            AddWindowStops(stops, border1, blur1 * WindowBlurScale);
            if (use2) AddWindowStops(stops, border2, blur2 * WindowBlurScale);
            if (use3) AddWindowStops(stops, border3, blur3 * WindowBlurScale);
            stops.Sort();

            var avgAlbedo = AverageAlbedo(material, log, "_ShadowColor 换算用的");

            var colorKeys = new List<GradientColorKey>();
            var last = float.NaN;
            foreach (var x in stops)
            {
                if (!float.IsNaN(last) && Mathf.Abs(x - last) < 0.0005f) continue;
                last = x;

                var s1 = TooningWindow(x, border1, blur1);
                var s2 = TooningWindow(x, border2, blur2);
                var s3 = TooningWindow(x, border3, blur3);

                // lilToon: indirectCol = ShadowColor → 再按 a*(1-s) 叠上 2nd / 3rd
                var rgb = new Color(first.r, first.g, first.b, 1f);
                if (use2) rgb = Color.Lerp(rgb, secondRgb, Mathf.Clamp01(second.a * (1f - s2)));
                if (use3) rgb = Color.Lerp(rgb, thirdRgb, Mathf.Clamp01(third.a * (1f - s3)));

                // ★ lilToon 的阴影色是**绝对颜色**（col = lerp(indirectCol, directCol, lns)，
                //   阴影直接把 albedo 换成 _ShadowColor），而 NonToon 的 Shade 是**乘算**
                //   （sd.col.rgb *= ramp）。直接乘就会变成 _ShadowColor × albedo = 双重变暗：
                //   深色材质（头发、深色衣服）会黑得离谱，浅色材质会偏白偏灰。
                //   所以先把阴影色除以材质的平均基础色，乘回来才等于原色，也就是 lilToon 的 lerp。
                // 注意：渐变贴图是 RGBA32（会被钳到 1），所以"阴影比 albedo 还亮"的情况做不到，
                // 这里用 Min(1, ...) 取最接近的近似（= 不再双重变暗）。
                rgb = new Color(
                    Mathf.Min(1f, rgb.r / avgAlbedo.r),
                    Mathf.Min(1f, rgb.g / avgAlbedo.g),
                    Mathf.Min(1f, rgb.b / avgAlbedo.b), 1f);
                // 关键点数值写进日志：色带是否"整条发白"看这里最直接
                if (log != null)
                {
                    log.Mapped("色带关键点 x=" + x.ToString("0.###") +
                               "（阴影色 " + first.r.ToString("0.###") + "/" + first.g.ToString("0.###") + "/" + first.b.ToString("0.###") +
                               "）", "ramp=" + rgb.r.ToString("0.###") + "/" + rgb.g.ToString("0.###") + "/" + rgb.b.ToString("0.###") +
                               "（除以平均色 " + avgAlbedo.r.ToString("0.###") + "/" + avgAlbedo.g.ToString("0.###") + "/" + avgAlbedo.b.ToString("0.###") + "）");
                }

                // 受光处是 albedo × 光（NonToon 自己会乘），所以渐变在受光端回到白色；
                // 混合系数还要过 _ShadowStrength（lilToon 的 lerp(1, s, strength)）
                // 实测对照（同相机、同场景、裙子区域四分位）：均值已经和 lil 对齐（0.704 vs 0.697），
                // 但对比跨度偏小 —— 我们 暗25%=0.502 / 亮25%=0.832（跨度 0.330），lil 是 0.443 / 0.887（0.444）。
                // 也就是暗部不够暗、亮部不够亮。这里把色带的暗端按 0.78 收一点（x=1 处不动），
                // 让跨度回到 lil 的量级；亮端受 lightColor 上限约束，靠主光补正那条一起抬。
                var darkEnd = Mathf.Lerp(0.70f, 1f, x);
                rgb = new Color(rgb.r * darkEnd, rgb.g * darkEnd, rgb.b * darkEnd, 1f);

                var mix = Mathf.Lerp(1f, s1, strength);
                rgb = Color.Lerp(rgb, Color.white, mix);
                colorKeys.Add(new GradientColorKey(rgb, x));
            }

            // ★ 关键修正：上面按 avgAlbedo 相除后可能 > 1（浅色阴影 + 深色底，例如毛衣 avgAlbedo=0.529
            //   而 _ShadowColor=0.771 ⇒ 1.46），而再 min(1) 钳平会让**整条色带变纯白 = 完全没有阴影**
            //   （实测毛衣就是这样：ramp=1/1/1，画面发平）。渐变贴图存不下 >1，所以改成
            //   "整条按最大值归一化"：保留明暗形状，只把整体压低；整体亮度由主光补正那条负责抬回来。
            var maxKey = 0f;
            foreach (var key in colorKeys)
                maxKey = Mathf.Max(maxKey, Mathf.Max(key.color.r, Mathf.Max(key.color.g, key.color.b)));
            if (maxKey > 1.0001f)
            {
                for (var i = 0; i < colorKeys.Count; i++)
                {
                    var c = colorKeys[i].color;
                    colorKeys[i] = new GradientColorKey(new Color(c.r / maxKey, c.g / maxKey, c.b / maxKey, 1f), colorKeys[i].time);
                }
                if (log != null)
                    log.Mapped("色带整体归一化", "最大值 " + maxKey.ToString("0.###") + " → 1（否则会被 min(1) 钳成纯白，完全没有阴影）");
            }

            var gradient = new Gradient();
            gradient.SetKeys(colorKeys.ToArray(), new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f),
            });

            var layers = use3 ? "3 层" : use2 ? "2 层" : "1 层";
            log.Mapped("阴影色 " + layers + "（border " + border1.ToString("0.###") + " / blur " + blur1.ToString("0.###") +
                       "，按 lilToon 的过渡窗口取关键点）", "_SharedGradients（Shade 渐变）");
            return gradient;
        }

        /// <summary>把某一层阴影的过渡窗口边界加进关键点列表（lilToon 的窗口是 [border ± blur/2]）。</summary>
        private static void AddWindowStops(List<float> stops, float border, float blur)
        {
            stops.Add(Mathf.Clamp01(border - blur * 0.5f));
            stops.Add(Mathf.Clamp01(border + blur * 0.5f));
        }

        /// <summary>
        /// 估算材质的平均基础色（用来把 lilToon 的"绝对阴影色"换算成 NonToon 的乘算系数）。
        /// 透明像素不参与；读不到贴图就当作白色（等于不做换算）。
        /// </summary>
        /// <summary>
        /// 估算材质的平均基础色（用来把 lilToon 的"绝对阴影色"换算成 NonToon 的乘算系数）。
        /// 透明像素不参与；读不到贴图就当作白色（等于不做换算）。
        ///
        /// 颜色空间很关键：<c>_ShadowColor</c> 来自 Material.GetColor，是 gamma(sRGB) 数值；
        /// GetPixels 在 Linear 工程里拿到的是线性数值，两者直接相除会得到偏大的商、再被 Min(1,…)
        /// 钳成 1 —— 表现就是"色带整条发白、材质只剩光照没有阴影分层"。
        /// 这里把平均色转到 gamma 再返回，和 _ShadowColor 对齐；同时把中间量写进日志方便核对。
        /// </summary>
        private static Color AverageAlbedo(Material material, ConversionLog log = null, string label = null)
        {
            var texture = ShaderUtility.HasProperty(material, "_MainTex") ? material.GetTexture("_MainTex") : null;
            if (texture == null) return Color.white;

            Color[] pixels;
            int width, height;
            if (!TryReadPixels(texture, out pixels, out width, out height, null, "the base texture")) return Color.white;
            if (pixels == null || pixels.Length == 0) return Color.white;

            double r = 0, g = 0, b = 0;
            var count = 0;
            var step = Mathf.Max(1, pixels.Length / 4096);
            for (var i = 0; i < pixels.Length; i += step)
            {
                var c = pixels[i];
                if (c.a < 0.5f) continue;
                r += c.r;
                g += c.g;
                b += c.b;
                count++;
            }
            if (count == 0) return Color.white;

            var raw = new Color(
                Mathf.Clamp((float)(r / count), 0.05f, 1f),
                Mathf.Clamp((float)(g / count), 0.05f, 1f),
                Mathf.Clamp((float)(b / count), 0.05f, 1f));
            // 实测（Linear 工程）：GetPixels 拿到的平均值已经和 _ShadowColor 同一尺度 ——
            //   Atri_face 1：原样(0.975,0.873,0.837) / 再转 gamma 会是(0.989,0.942,0.925)
            //   源 _ShadowColor=(1,0.962,0.943) ⇒ 用原样相除得 (1.03,0.95,0.89) 合理；
            // 再转一次 gamma 会把平均色推高、色带被压暗，所以**不要**转换。
            var gamma = raw;

            if (log != null && !string.IsNullOrEmpty(label))
            {
                log.Mapped(label + " 平均基础色",
                    "(" + gamma.r.ToString("0.###") + "," + gamma.g.ToString("0.###") + "," + gamma.b.ToString("0.###") + ")" +
                    "  色彩空间=" + PlayerSettings.colorSpace + "（与 _ShadowColor 同尺度，不再做 gamma 转换）");
            }
            return gamma;
        }

        /// <summary>lilToon 的 <c>lilTooningNoSaturateScale(value, border, blur)</c>：过渡窗口 [border ± blur/2]。</summary>
        private static float TooningWindow(float value, float border, float blur)
        {
            var min = Mathf.Clamp01(border - blur * 0.5f);
            var max = Mathf.Clamp01(border + blur * 0.5f);
            var span = Mathf.Max(max - min, 0.0001f);
            return Mathf.Clamp01((value - min) / span);
        }

        private static Gradient BuildRimShadeGradient(Material material)
        {
            var rimShade = ShaderUtility.HasProperty(material, "_RimShadeColor")
                ? material.GetColor("_RimShadeColor")
                : new Color(0.5f, 0.5f, 0.5f, 1f);
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(rimShade, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(rimShade.a, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }

        private static Gradient FlatGradient(Color color)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(color.a, 1f) });
            return gradient;
        }

        /// <summary>
        /// Serialises the same fields that Shader Core's GradientsImporter keeps in its .scgradients asset,
        /// using Unity's own YAML flavour so the file can be imported by that importer.
        /// </summary>
        /// <summary>
        /// 把渐变写进 `.scgradients.meta` 的 ScriptedImporter 导入设置里 —— 这才是导入器真正读取的地方。
        /// 会保留原有 guid（材质的 _SharedGradients 引用才不会断），没有就按路径生成一个稳定的。
        /// </summary>
        private static void WriteGradientsMeta(string assetPath, List<Gradient> gradients)
        {
            var importerGuid = FindGradientsImporterGuid();
            if (string.IsNullOrEmpty(importerGuid))
            {
                Debug.LogWarning("[LilToNonToon Switcher] 找不到 Shader Core 的 GradientsImporter，" +
                                 "阴影渐变没能写进 meta（材质的阴影会保持默认的「白→白」）。");
                return;
            }

            var metaPath = assetPath + ".meta";
            var assetGuid = ReadGuidFromMeta(metaPath);
            if (string.IsNullOrEmpty(assetGuid)) assetGuid = StableGuidOf(assetPath);

            var sb = new StringBuilder();
            sb.AppendLine("fileFormatVersion: 2");
            sb.AppendLine("guid: " + assetGuid);
            sb.AppendLine("ScriptedImporter:");
            sb.AppendLine("  internalIDToNameTable: []");
            sb.AppendLine("  externalObjects: {}");
            sb.AppendLine("  serializedVersion: 2");
            sb.AppendLine("  userData: ");
            sb.AppendLine("  assetBundleName: ");
            sb.AppendLine("  assetBundleVariant: ");
            sb.AppendLine("  script: {fileID: 11500000, guid: " + importerGuid + ", type: 3}");
            sb.AppendLine("  size: 128");
            sb.AppendLine("  gradients:");
            foreach (var gradient in gradients) WriteGradient(sb, gradient);
            File.WriteAllText(metaPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Shader Core 里 GradientsImporter 的脚本 guid。</summary>
        private static string FindGradientsImporterGuid()
        {
            foreach (var guid in AssetDatabase.FindAssets("GradientsImporter t:MonoScript"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != "GradientsImporter") continue;
                return guid;
            }
            return null;
        }

        private static string ReadGuidFromMeta(string metaPath)
        {
            if (!File.Exists(metaPath)) return null;
            foreach (var line in File.ReadLines(metaPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("guid:", StringComparison.Ordinal)) continue;
                return trimmed.Substring(5).Trim();
            }
            return null;
        }

        private static string StableGuidOf(string assetPath)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("NonToonSwitcher/" + assetPath));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static string BuildGradientsAsset(List<Gradient> gradients)
        {
            var sb = new StringBuilder();
            sb.AppendLine("%YAML 1.1");
            sb.AppendLine("%TAG !u! tag:unity3d.com,2011:");
            sb.AppendLine("--- !u!114 &11400000");
            sb.AppendLine("MonoBehaviour:");
            sb.AppendLine("  m_ObjectHideFlags: 0");
            sb.AppendLine("  m_CorrespondingSourceObject: {fileID: 0}");
            sb.AppendLine("  m_PrefabInstance: {fileID: 0}");
            sb.AppendLine("  m_PrefabAsset: {fileID: 0}");
            sb.AppendLine("  m_GameObject: {fileID: 0}");
            sb.AppendLine("  m_Enabled: 1");
            sb.AppendLine("  m_EditorHideFlags: 0");
            sb.AppendLine("  m_Script: {fileID: 0}");
            sb.AppendLine("  m_Name: ");
            sb.AppendLine("  m_EditorClassIdentifier: ");
            sb.AppendLine("  size: 128");
            sb.AppendLine("  gradients:");
            foreach (var gradient in gradients) WriteGradient(sb, gradient);
            return sb.ToString();
        }

        /// <summary>
        /// 把 Gradient 的**真实关键点**写成 Unity 的 Gradient 序列化形式。
        /// 以前这里固定在 0 / 1/3 / 2/3 / 1 四个位置 `Evaluate` 后再写 —— 等于把渐变又压回 4 个等距点，
        /// 阴影过渡窗口（例如宽 0.189）里的信息基本全被丢掉，写出来的渐变是错的。
        /// Unity 的 Gradient 最多 8 个颜色关键点，这里最多写到 8 个。
        /// </summary>
        private static void WriteGradient(StringBuilder sb, Gradient gradient)
        {
            var keys = gradient.colorKeys;
            if (keys == null || keys.Length == 0) keys = new[] { new GradientColorKey(Color.white, 0f) };
            if (keys.Length > 8) keys = keys.Take(8).ToArray();

            sb.AppendLine("  - serializedVersion: 2");
            for (var i = 0; i < 8; i++)
            {
                var color = i < keys.Length ? keys[i].color : Color.black;
                sb.AppendLine("    key" + i + ": " + ColorLine(color));
            }
            for (var i = 0; i < 8; i++)
                sb.AppendLine("    ctime" + i + ": " + (i < keys.Length
                    ? Mathf.RoundToInt(Mathf.Clamp01(keys[i].time) * 65535f).ToString(CultureInfo.InvariantCulture)
                    : "0"));
            sb.AppendLine("    atime0: 0");
            sb.AppendLine("    atime1: 65535");
            for (var i = 2; i < 8; i++) sb.AppendLine("    atime" + i + ": 0");
            // 注意字段名没有 m_ 前缀 —— 这是导入器（ScriptedImporter）的序列化形式，
            // 写成 m_Mode / m_NumColorKeys 那种旧版 Gradient 字段名导入器是不认的。
            sb.AppendLine("    mode: 0");
            sb.AppendLine("    colorSpace: -1");
            sb.AppendLine("    numColorKeys: " + keys.Length);
            sb.AppendLine("    numAlphaKeys: 2");
        }

        private static string ColorLine(Color color)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{{r: {0}, g: {1}, b: {2}, a: {3}}}",
                Float(color.r), Float(color.g), Float(color.b), Float(color.a));
        }

        private static string Float(float value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}

// touch 639266630190327725


// touch 639266631749473051


// touch 639266635266843564


// touch 639266635824848705

