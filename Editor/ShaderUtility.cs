// LilToNonToon Switcher - written for lilToon 2.x / NonToon 0.1.x / Shader Core 0.1.x
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    public enum ShaderKind
    {
        Unknown,
        LilToon,
        NonToon
    }

    /// <summary>
    /// Shader / material introspection helpers.
    /// Everything is done by display name and property type at runtime, so the tool keeps working
    /// when lilToon or NonToon add or rename properties.
    /// </summary>
    public static class ShaderUtility
    {
        public const string NonToonShaderName = "NonToon";
        /// <summary>Fallback in case the shader was renamed to something like "NonToon/NonToon".</summary>
        public const string NonToonShaderPackageFolder = "jp.lilxyzw.nontoon";

        // Shader Core prefixes module properties with "_" + uniqueID.Replace('.', '_').
        public const string NtModulePrefix = "_jp_lilxyzw_nontoon_";
        public const string NtShadePrefix = NtModulePrefix + "shade_";
        public const string NtDetailsPrefix = NtModulePrefix + "details_";
        public const string NtMatCapsPrefix = NtModulePrefix + "matcaps_";
        public const string NtRimLightPrefix = NtModulePrefix + "rimlight_";
        public const string NtRimShadePrefix = NtModulePrefix + "rimshade_";
        public const string NtSpecularPrefix = NtModulePrefix + "specular_";
        public const string NtBacklightPrefix = NtModulePrefix + "backlight_";
        public const string NtDistanceFadePrefix = NtModulePrefix + "distancefade_";
        public const string NtLightenPrefix = NtModulePrefix + "lighten_";
        public const string NtHairSpecularPrefix = NtModulePrefix + "hairspecular_";

        public static ShaderKind Detect(Shader shader)
        {
            if (shader == null) return ShaderKind.Unknown;
            var name = shader.name ?? "";
            if (name == NonToonShaderName || name.StartsWith("NonToon/", StringComparison.Ordinal))
                return ShaderKind.NonToon;

            // lilToon 2.x ships 65 shaders. Most materials point at a hidden variant
            // ("Hidden/lilToonOutline", "Hidden/lilToonTransparentOutline", ...), never at the
            // user facing "lilToon" shader, so matching only prefixes would miss them.
            if (name.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) >= 0)
                return ShaderKind.LilToon;
            if (name.StartsWith("Hidden/lts", StringComparison.Ordinal)) return ShaderKind.LilToon;
            if (name.StartsWith("_lil/", StringComparison.Ordinal)) return ShaderKind.LilToon;

            return ShaderKind.Unknown;
        }

        public static bool IsLilToon(Shader shader) { return Detect(shader) == ShaderKind.LilToon; }
        public static bool IsNonToon(Shader shader) { return Detect(shader) == ShaderKind.NonToon; }

        /// <summary>
        /// True when lilToon is installed. The user facing "lilToon" shader is what Shader.Find can see,
        /// but a project may only contain materials with hidden variants, so the package is checked too.
        /// </summary>
        public static bool IsLilToonAvailable
        {
            get
            {
                if (Shader.Find("lilToon") != null) return true;
                foreach (var guid in AssetDatabase.FindAssets("t:Shader"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if (shader != null && IsLilToon(shader)) return true;
                }
                return false;
            }
        }

        /// <summary>Finds the NonToon shader asset. Returns null (and logs reason) when it is not installed.</summary>
        public static Shader FindNonToonShader()
        {
            var direct = Shader.Find(NonToonShaderName);
            if (direct != null) return direct;

            // The shader name may change; accept anything coming from the NonToon package folder.
            foreach (var guid in AssetDatabase.FindAssets("t:Shader"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (path.IndexOf(NonToonShaderPackageFolder, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null) continue;
                if (Path.GetFileNameWithoutExtension(path).IndexOf("NonToon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                return shader;
            }
            return null;
        }

        public static bool HasProperty(Shader shader, string name)
        {
            return shader != null && !string.IsNullOrEmpty(name) && shader.FindPropertyIndex(name) >= 0;
        }

        public static bool HasProperty(Material material, string name)
        {
            return material != null && HasProperty(material.shader, name);
        }

        public static UnityEngine.Rendering.ShaderPropertyType PropertyType(Shader shader, string name)
        {
            var index = shader.FindPropertyIndex(name);
            if (index < 0) return UnityEngine.Rendering.ShaderPropertyType.Float;
            return shader.GetPropertyType(index);
        }

        public static string PropertyDisplayName(Shader shader, string name)
        {
            var index = shader.FindPropertyIndex(name);
            if (index < 0) return null;
            return shader.GetPropertyDescription(index);
        }

        /// <summary>
        /// The first property of <paramref name="shader"/> whose display name contains
        /// <paramref name="displayNameContains"/> and whose real name contains <paramref name="nameContains"/>.
        /// Used to locate Mask Channel style properties of optional Shader Core modules.
        /// </summary>
        public static string FindPropertyByDisplayName(Shader shader, string nameContains, string displayNameContains)
        {
            if (shader == null) return null;
            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                if (!string.IsNullOrEmpty(nameContains) &&
                    name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(displayNameContains) &&
                    shader.GetPropertyDescription(i).IndexOf(displayNameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                return name;
            }
            return null;
        }

        public static bool IsTextureProperty(Shader shader, string name)
        {
            var type = PropertyType(shader, name);
            return type == UnityEngine.Rendering.ShaderPropertyType.Texture;
        }

        public static bool IsIntegerProperty(Shader shader, string name)
        {
            var type = PropertyType(shader, name);
            return type == UnityEngine.Rendering.ShaderPropertyType.Int;
        }

        /// <summary>Copies a property value between materials when both shaders declare the same type.</summary>
        public static bool CopyProperty(Material source, Material destination, string name)
        {
            if (source == null || destination == null) return false;
            if (!HasProperty(source, name) || !HasProperty(destination, name)) return false;
            var from = PropertyType(source.shader, name);
            var to = PropertyType(destination.shader, name);
            if (from != to) return false;

            switch (to)
            {
                case UnityEngine.Rendering.ShaderPropertyType.Color:
                    destination.SetColor(name, source.GetColor(name));
                    return true;
                case UnityEngine.Rendering.ShaderPropertyType.Vector:
                    destination.SetVector(name, source.GetVector(name));
                    return true;
                case UnityEngine.Rendering.ShaderPropertyType.Float:
                    destination.SetFloat(name, source.GetFloat(name));
                    return true;
                case UnityEngine.Rendering.ShaderPropertyType.Int:
                    destination.SetInt(name, Mathf.RoundToInt(source.GetFloat(name)));
                    return true;
                case UnityEngine.Rendering.ShaderPropertyType.Range:
                    destination.SetFloat(name, source.GetFloat(name));
                    return true;
                case UnityEngine.Rendering.ShaderPropertyType.Texture:
                    destination.SetTexture(name, source.GetTexture(name));
                    destination.SetTextureScale(name, source.GetTextureScale(name));
                    destination.SetTextureOffset(name, source.GetTextureOffset(name));
                    return true;
            }
            return false;
        }

        /// <summary>Copy a float, rounding when the destination property is an integer one.</summary>
        public static bool SetFloatValue(Material destination, string name, float value)
        {
            if (!HasProperty(destination, name)) return false;
            if (IsIntegerProperty(destination.shader, name)) destination.SetInt(name, Mathf.RoundToInt(value));
            else destination.SetFloat(name, value);
            return true;
        }

        public static bool SetIntValue(Material destination, string name, int value)
        {
            if (!HasProperty(destination, name)) return false;
            destination.SetInt(name, value);
            RecentInts[Key(destination, name)] = value;
            return true;
        }

        // ------------------------------------------------------------------ integer properties
        //
        // Unity does not persist Material.SetInt() for integer shader properties of Shader Core shaders:
        // the call succeeds and Material.GetInt() reads the value back, but AssetDatabase.SaveAssets()
        // writes the property default to the .mat file. The same applies in reverse - a .mat that really
        // contains "_RenderingMode: 2" reads back as 0.
        //
        // The serialized property list is authoritative (it is what the shader reads and what NonToon's
        // own inspector writes), so integer properties are read and written through SerializedObject here.

        private static SerializedObject GetSerialized(Material material)
        {
            var so = new SerializedObject(material);
            so.Update();
            return so;
        }

        /// <summary>
        /// Reads an integer shader property. Material.GetInt is deliberately not used: for properties declared
        /// as uint (NonToon's toggles) Unity logs "doesn't have a float or range property" and returns 0, so the
        /// value would be wrong as well as noisy. Values written through SetIntPersistent are remembered for the
        /// session, everything else comes from the serialized property list.
        /// </summary>
        public static int GetIntValue(Material material, string name)
        {
            if (material == null || string.IsNullOrEmpty(name)) return 0;
            if (!HasProperty(material, name)) return 0;
            if (RecentInts.TryGetValue(Key(material, name), out var recent)) return recent;
            return ReadSerializedInt(material, name, 0);
        }

        // Values written this session, so a fresh material reports what was just set even before it is saved.
        private static readonly Dictionary<string, int> RecentInts = new Dictionary<string, int>(StringComparer.Ordinal);

        private static string Key(Material material, string name)
        {
            return material.GetInstanceID() + "|" + name;
        }

        /// <summary>Reads an integer property straight from the serialized data (never calls Material.GetInt).</summary>
        public static int ReadSerializedInt(Material material, string name, int fallback)
        {
            if (material == null || string.IsNullOrEmpty(name)) return fallback;

            try
            {
                var so = GetSerialized(material);
                var ints = so.FindProperty("m_SavedProperties.m_Ints");
                if (ints != null && ints.isArray)
                {
                    for (var i = 0; i < ints.arraySize; i++)
                    {
                        var element = ints.GetArrayElementAtIndex(i);
                        if (element.FindPropertyRelative("first").stringValue != name) continue;
                        return element.FindPropertyRelative("second").intValue;
                    }
                }

                // Some properties are stored as floats even though the shader declares them as integers.
                var floats = so.FindProperty("m_SavedProperties.m_Floats");
                if (floats != null && floats.isArray)
                {
                    for (var i = 0; i < floats.arraySize; i++)
                    {
                        var element = floats.GetArrayElementAtIndex(i);
                        if (element.FindPropertyRelative("first").stringValue != name) continue;
                        return Mathf.RoundToInt(element.FindPropertyRelative("second").floatValue);
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to whatever the caller passed in.
            }

            return fallback;
        }

        /// <summary>
        /// Writes an integer and makes sure it survives serialization. The material must already be an
        /// asset for the value to be stored; the in-memory value is set as well so later passes see it.
        /// </summary>
        public static bool SetIntPersistent(Material material, string name, int value)
        {
            if (material == null || !HasProperty(material, name)) return false;
            material.SetInt(name, value);
            RecentInts[Key(material, name)] = value;

            try
            {
                var so = GetSerialized(material);
                var ints = so.FindProperty("m_SavedProperties.m_Ints");
                if (ints != null && ints.isArray)
                {
                    for (var i = 0; i < ints.arraySize; i++)
                    {
                        var element = ints.GetArrayElementAtIndex(i);
                        if (element.FindPropertyRelative("first").stringValue != name) continue;
                        element.FindPropertyRelative("second").intValue = value;
                        so.ApplyModifiedPropertiesWithoutUndo();
                        EditorUtility.SetDirty(material);
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // The in-memory value is still set; the caller only loses persistence.
            }
            return false;
        }

        /// <summary>Makes every integer value of a material survive serialization.</summary>
        public static void PersistIntegers(Material material)
        {
            if (material == null || material.shader == null) return;
            var shader = material.shader;
            var count = shader.GetPropertyCount();

            // Pass 1: freeze the current values. GetInt is safe for values assigned in this session, but it
            // logs an error for integer properties that have no float backing, so it is called exactly once
            // per property and only here.
            var values = new List<KeyValuePair<string, int>>();
            for (var i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Int) continue;
                var name = shader.GetPropertyName(i);
                if (string.IsNullOrEmpty(name)) continue;
                values.Add(new KeyValuePair<string, int>(name, GetIntValue(material, name)));
            }

            // Pass 2: write them through the serialized property list.
            foreach (var pair in values) SetIntPersistent(material, pair.Key, pair.Value);
        }

        public static bool SetColorValue(Material destination, string name, Color value)
        {
            if (!HasProperty(destination, name)) return false;
            destination.SetColor(name, value);
            return true;
        }

        public static bool SetTextureValue(Material destination, string name, Texture value)
        {
            if (!HasProperty(destination, name)) return false;
            destination.SetTexture(name, value);
            return true;
        }

        /// <summary>
        /// Creates an asset folder including every missing parent ("Assets/A/B/C" works even when nothing
        /// below Assets exists yet). Returns false when the path is outside of Assets or cannot be created.
        /// </summary>
        public static bool TryCreateFolderRecursive(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            folder = ToAssetPath(folder).Replace('\\', '/').TrimEnd('/');
            if (folder == "Assets") return true;
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal)) return false;
            if (AssetDatabase.IsValidFolder(folder)) return true;

            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    var created = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(created)) return false;
                }
                current = next;
            }
            return AssetDatabase.IsValidFolder(folder);
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Material";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                foreach (var c in invalid) if (chars[i] == c) { chars[i] = '_'; break; }
            }
            var result = new string(chars).Trim();
            return string.IsNullOrEmpty(result) ? "Material" : result;
        }

        public static string ToAssetPath(string absoluteOrRelative)
        {
            var path = absoluteOrRelative.Replace('\\', '/');
            if (path.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
                path = "Assets" + path.Substring(Application.dataPath.Length);
            return path;
        }
    }
}
