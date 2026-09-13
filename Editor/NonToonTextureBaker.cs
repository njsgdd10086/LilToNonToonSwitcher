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
            var source = nonToonMaterial.GetTexture("_BaseTexture");
            if (source == null) return null;

            var tint = ShaderUtility.HasProperty(lilToonMaterial, "_Color") ? lilToonMaterial.GetColor("_Color") : Color.white;
            var hires = tint.r != 1f || tint.g != 1f || tint.b != 1f;

            // Nothing to do when the colour is white - keep referencing the original texture.
            if (!hires)
            {
                log.Mapped("_MainTex（颜色为白）", "_BaseTexture（保留原贴图）");
                return null;
            }

            if (!TryReadPixels(source, out var pixels, out var width, out var height, log, "the base texture"))
            {
                log.Warn("_Color 不是白色，但基础贴图读取失败，颜色乘算没有烘焙进去。" +
                         "请手动设置转换后材质的颜色，或在贴图上勾选 Read/Write。");
                return null;
            }

            var alpha = tint.a;
            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                pixels[i] = new Color(pixel.r * tint.r, pixel.g * tint.g, pixel.b * tint.b, pixel.a * alpha);
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
                !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(source)) &&
                AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)) is TextureImporter sourceImporter)
            {
                importer.sRGBTexture = sourceImporter.sRGBTexture;
                importer.alphaSource = sourceImporter.alphaSource;
                importer.alphaIsTransparency = sourceImporter.alphaIsTransparency;
                importer.mipmapEnabled = sourceImporter.mipmapEnabled;
                importer.streamingMipmaps = sourceImporter.streamingMipmaps;
                importer.maxTextureSize = sourceImporter.maxTextureSize;
                importer.textureCompression = sourceImporter.textureCompression;
                importer.wrapMode = sourceImporter.wrapMode;
                importer.filterMode = sourceImporter.filterMode;
                importer.anisoLevel = sourceImporter.anisoLevel;
                importer.SaveAndReimport();
            }

            asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            log.Mapped("_MainTex x _Color", "_BaseTexture (baked PNG)");
            return asset;
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
