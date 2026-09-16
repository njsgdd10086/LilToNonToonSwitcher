// LilToNonToon Switcher
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
                width = Mathf.Clamp(Mathf.NextPowerOfTwo(texture.width), 4, 4096);
                height = Mathf.Clamp(Mathf.NextPowerOfTwo(texture.height), 4, 4096);
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
            out List<int> bakedIndices, ConversionLog log)
        {
            bakedIndices = new List<int>();
            var gradients = new List<Gradient>();
            var used = new List<string>();

            var shadeEnabled = !ShaderUtility.HasProperty(lilToonMaterial, "_UseShadow") ||
                               lilToonMaterial.GetFloat("_UseShadow") != 0f;
            if (shadeEnabled)
            {
                gradients.Add(BuildShadeGradient(lilToonMaterial, log));
                bakedIndices.Add(0);
                used.Add("_ShadowColor");
            }

            if (ShaderUtility.HasProperty(lilToonMaterial, "_UseRimShade") &&
                lilToonMaterial.GetFloat("_UseRimShade") != 0f)
            {
                bakedIndices.Add(gradients.Count);
                gradients.Add(BuildRimShadeGradient(lilToonMaterial));
                used.Add("_RimShadeColor");
            }

            if (gradients.Count == 0) return null;

            var name = ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_Gradients." + GradientsExtension;
            var path = folder.TrimEnd('/') + "/" + name;
            File.WriteAllText(path, BuildGradientsAsset(gradients), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
            {
                log.Warn("生成的渐变数组导入失败，NonToon 的阴影颜色仍是默认值。");
                bakedIndices.Clear();
                return null;
            }

            log.Mapped(string.Join(" + ", used) + "，共 " + gradients.Count + " 层",
                "_SharedGradients（生成的 " + GradientsExtension + "）");
            return asset;
        }

        private static Gradient BuildShadeGradient(Material material, ConversionLog log)
        {
            var first = ShaderUtility.HasProperty(material, "_ShadowColor") ? material.GetColor("_ShadowColor") : Color.white;
            var second = ShaderUtility.HasProperty(material, "_Shadow2ndColor")
                ? material.GetColor("_Shadow2ndColor")
                : new Color(0.68f, 0.66f, 0.79f, 1f);
            var third = ShaderUtility.HasProperty(material, "_Shadow3rdColor")
                ? material.GetColor("_Shadow3rdColor")
                : Color.black;

            // lilToon: colour 1 is used inside the shadow, colour 2 / 3 deepen it further.
            if (second == Color.clear) second = first;
            if (third == Color.clear) third = second;

            var border1 = ShaderUtility.HasProperty(material, "_ShadowBorder") ? material.GetFloat("_ShadowBorder") : 0.5f;
            var border2 = ShaderUtility.HasProperty(material, "_Shadow2ndBorder") ? material.GetFloat("_Shadow2ndBorder") : 0.15f;
            var border3 = ShaderUtility.HasProperty(material, "_Shadow3rdBorder") ? material.GetFloat("_Shadow3rdBorder") : 0.25f;

            // NonToon samples the ramp with a 0..1 shade factor; 0 is fully shaded, 1 is lit.
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(third, 0f),
                    new GradientColorKey(second, Mathf.Clamp01(1f - border2) * 0.5f),
                    new GradientColorKey(first, Mathf.Clamp01(1f - border1)),
                    new GradientColorKey(Color.white, 1f),
                },
                new[]
                {
                    new GradientAlphaKey(third.a, 0f),
                    new GradientAlphaKey(second.a, Mathf.Clamp01(1f - border2) * 0.5f),
                    new GradientAlphaKey(first.a, Mathf.Clamp01(1f - border1)),
                    new GradientAlphaKey(1f, 1f),
                });
            return gradient;
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

        private static void WriteGradient(StringBuilder sb, Gradient gradient)
        {
            sb.AppendLine("  - serializedVersion: 2");
            sb.AppendLine("    key0: " + ColorLine(gradient.Evaluate(0f)));
            sb.AppendLine("    key1: " + ColorLine(gradient.Evaluate(1f / 3f)));
            sb.AppendLine("    key2: " + ColorLine(gradient.Evaluate(2f / 3f)));
            sb.AppendLine("    key3: " + ColorLine(gradient.Evaluate(1f)));
            sb.AppendLine("    key4: " + ColorLine(Color.black));
            sb.AppendLine("    key5: " + ColorLine(Color.black));
            sb.AppendLine("    key6: " + ColorLine(Color.black));
            sb.AppendLine("    key7: " + ColorLine(Color.black));
            sb.AppendLine("    ctime0: 0");
            sb.AppendLine("    ctime1: 21845");
            sb.AppendLine("    ctime2: 43690");
            sb.AppendLine("    ctime3: 65535");
            sb.AppendLine("    ctime4: 0");
            sb.AppendLine("    ctime5: 0");
            sb.AppendLine("    ctime6: 0");
            sb.AppendLine("    ctime7: 0");
            sb.AppendLine("    atime0: 0");
            sb.AppendLine("    atime1: 65535");
            sb.AppendLine("    atime2: 0");
            sb.AppendLine("    atime3: 0");
            sb.AppendLine("    atime4: 0");
            sb.AppendLine("    atime5: 0");
            sb.AppendLine("    atime6: 0");
            sb.AppendLine("    atime7: 0");
            sb.AppendLine("    m_Mode: 0");
            sb.AppendLine("    m_ColorSpace: -1");
            sb.AppendLine("    m_NumColorKeys: 4");
            sb.AppendLine("    m_NumAlphaKeys: 2");
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
