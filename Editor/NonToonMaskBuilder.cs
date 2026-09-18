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
            };

            Color[] pixels = null;
            var width = 0;
            var height = 0;
            var used = new List<string>();
            var channelOwner = new string[4];
            // 通道分配先记下来，等遮罩贴图写完之后**统一**写入再统一保存 ——
            // 边烘边写会写不进 .mat（实测：MatCap 的遮罩被分到了 R 通道，但模块的 Mask Channel
            // 仍然停在默认的 A，于是 MatCap 被乘成 ~0，金色装饰整片消失）。
            var assignments = new List<KeyValuePair<string, int>>();

            foreach (var source in sources)
            {
                if (!ShaderUtility.HasProperty(lilToonMaterial, source.LilToonProperty)) continue;
                if (source.RequireToggle && ShaderUtility.HasProperty(lilToonMaterial, source.ToggleProperty) &&
                    lilToonMaterial.GetFloat(source.ToggleProperty) == 0f) continue;

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

            if (pixels == null) return;

            var directory = folder.Replace('\\', '/').TrimEnd('/');
            EnsureFolder(directory);
            var maskPath = directory + "/" + ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_NTMask.png";

            var existing = nonToonMaterial.GetTexture("_SharedMask") as Texture2D;
            if (existing != null && AssetDatabase.GetAssetPath(existing) != maskPath)
                log.Warn("NonToon 共享遮罩原本已有内容（" + existing.name + "），现在被生成的遮罩替换了。");

            WriteTexture(pixels, width, height, maskPath);
            ImportAsMask(maskPath);

            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
            if (asset == null)
            {
                log.Warn("生成的共享遮罩加载失败（路径：" + maskPath + "，文件存在：" +
                         File.Exists(maskPath) + "); the mask channel values were left at their defaults.");
                return;
            }

            nonToonMaterial.SetTexture("_SharedMask", asset);
            log.Mapped("lilToon 遮罩（" + string.Join("、", used) + "）", "_SharedMask");

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
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
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

        private static bool ReadPixels(Texture2D texture, out Color[] pixels, out int width, out int height,
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
    }
}
