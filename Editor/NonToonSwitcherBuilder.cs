// LilToNonToon Switcher
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    public enum SwitcherKind
    {
        None,
        ModularAvatar,
    }

    /// <summary>Which Modular Avatar component performs the switch.</summary>
    public enum SwitcherMode
    {
        /// <summary>
        /// MA Material Setter: one entry per renderer slot ("object + material index + material").
        /// This is the most explicit form and is what most lilToon -> NonToon tools generate.
        /// </summary>
        MaterialSetter = 0,

        /// <summary>MA Material Swap: "from material -> to material" pairs, applied under a root object.</summary>
        MaterialSwap = 1,
    }

    public sealed class SwitchPair
    {
        public Material Original;    // lilToon material currently on the renderers
        public Material Converted;   // NonToon material that was created next to it
        public Renderer Renderer;    // renderer that carries it (used by the Material Setter entries)
        public int MaterialIndex;    // slot inside that renderer
    }

    /// <summary>
    /// Builds the object that performs the lilToon &lt;-&gt; NonToon switch.
    ///
    /// Modular Avatar is used through reflection so that this tool compiles (and the rest of it keeps working)
    /// no matter which MA version is installed. MA Material Swap plus a MA Menu Item is everything that is
    /// needed: MA generates the toggle animation when the avatar is built, so no animator or animation clip
    /// has to be authored by hand.
    /// </summary>
    public static class NonToonSwitcherBuilder
    {
        private const string MaAssemblyHint = "nadena.dev.modular-avatar";
        private const string MaterialSwapTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMaterialSwap, " + MaAssemblyHint;
        private const string MaterialSetterTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMaterialSetter, " + MaAssemblyHint;
        private const string MaterialSwitchObjectTypeName = "nadena.dev.modular_avatar.core.MaterialSwitchObject, " + MaAssemblyHint;
        private const string MenuItemTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMenuItem, " + MaAssemblyHint;
        private const string MenuInstallerTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller, " + MaAssemblyHint;
        private const string ObjectReferenceTypeName = "nadena.dev.modular_avatar.core.AvatarObjectReference, " + MaAssemblyHint;

        public static bool IsModularAvatarInstalled
        {
            get { return FindType(MaterialSwapTypeName) != null; }
        }

        private static Type FindType(string assemblyQualifiedName)
        {
            var type = Type.GetType(assemblyQualifiedName, false);
            if (type != null) return type;

            // The assembly name may differ between MA distributions; fall back to a scan.
            var simpleName = assemblyQualifiedName.Split(',')[0].Trim();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(simpleName, false);
                }
                catch (Exception)
                {
                    type = null;
                }
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>
        /// Creates the "_NonToonSwitch" object with the colour changer component and, optionally, a menu toggle.
        /// </summary>
        public static GameObject Build(
            GameObject parent,
            GameObject swapRoot,
            IList<SwitchPair> pairs,
            bool createMenuToggle,
            string menuParameter,
            string menuLabel,
            bool nonToonOnByDefault,
            SwitcherMode mode,
            ConversionResult result)
        {
            if (!IsModularAvatarInstalled)
            {
                result.Error("未安装 Modular Avatar (nadena.dev.modular-avatar)，因此没有创建 shader 切换对象。" +
                             "请先安装 Modular Avatar，再重新转换一次。");
                return null;
            }

            var switcher = new GameObject("_NonToonSwitch");
            Undo.RegisterCreatedObjectUndo(switcher, "创建 NonToon 切换对象");
            switcher.transform.SetParent(parent != null ? parent.transform : null, false);
            switcher.transform.localPosition = Vector3.zero;
            switcher.transform.localRotation = Quaternion.identity;
            switcher.transform.localScale = Vector3.one;

            var ok = mode == SwitcherMode.MaterialSetter
                ? AddMaterialSetter(switcher, pairs, nonToonOnByDefault, result)
                : AddMaterialSwap(switcher, swapRoot, pairs, nonToonOnByDefault, result);

            if (!ok)
            {
                UnityEngine.Object.DestroyImmediate(switcher);
                return null;
            }

            if (createMenuToggle) AddMenuToggle(switcher, menuParameter, menuLabel, result);

            result.CreatedObjects.Add(switcher);
            result.SwitchObject = switcher;
            return switcher;
        }

        /// <summary>
        /// MA Material Setter: one entry per (renderer, slot). While the toggle is off the setter applies the
        /// NonToon materials; while it is on they are replaced by the original lilToon materials.
        /// </summary>
        private static bool AddMaterialSetter(GameObject host, IList<SwitchPair> pairs,
            bool nonToonOnByDefault, ConversionResult result)
        {
            var setterType = FindType(MaterialSetterTypeName);
            if (setterType == null)
            {
                result.Error("找不到 MA Material Setter 组件类型，请更新 Modular Avatar。");
                return false;
            }

            var component = host.AddComponent(setterType);
            if (component == null)
            {
                result.Error("无法把 MA Material Setter 添加到 " + host.name + " 上。");
                return false;
            }

            var objectsProperty = setterType.GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance);
            var list = objectsProperty != null ? objectsProperty.GetValue(component, null) as IList : null;
            if (list == null)
            {
                result.Error("当前 Modular Avatar 版本的 MA Material Setter 没有 Objects 列表。");
                return false;
            }

            var entryType = FindType(MaterialSwitchObjectTypeName)
                            ?? (objectsProperty.PropertyType.IsGenericType
                                ? objectsProperty.PropertyType.GetGenericArguments()[0]
                                : null);
            if (entryType == null)
            {
                result.Error("无法解析 MA Material Setter 的条目类型。");
                return false;
            }

            var objectField = entryType.GetField("Object", BindingFlags.Public | BindingFlags.Instance);
            var materialField = entryType.GetField("Material", BindingFlags.Public | BindingFlags.Instance);
            var indexField = entryType.GetField("MaterialIndex", BindingFlags.Public | BindingFlags.Instance);
            var referenceType = FindType(ObjectReferenceTypeName);
            if (objectField == null || materialField == null || indexField == null || referenceType == null)
            {
                result.Error("无法解析 MA Material Setter 的条目字段。");
                return false;
            }

            var added = 0;
            foreach (var pair in pairs)
            {
                if (pair.Renderer == null || pair.Converted == null) continue;

                var reference = Activator.CreateInstance(referenceType);
                var setMethod = referenceType.GetMethod("Set", new[] { typeof(GameObject) });
                if (setMethod != null) setMethod.Invoke(reference, new object[] { pair.Renderer.gameObject });

                var entry = Activator.CreateInstance(entryType);
                objectField.SetValue(entry, reference);
                materialField.SetValue(entry, pair.Converted);
                indexField.SetValue(entry, pair.MaterialIndex);
                list.Add(entry);
                added++;
            }

            if (added == 0)
            {
                result.Error("没有任何 Renderer 材质槽可以写成 MA Material Setter 的条目。");
                return false;
            }

            var invertedProperty = setterType.GetProperty("Inverted", BindingFlags.Public | BindingFlags.Instance);
            if (invertedProperty != null && invertedProperty.CanWrite)
                invertedProperty.SetValue(component, nonToonOnByDefault, null);
            else if (nonToonOnByDefault)
                result.Warn("当前 Modular Avatar 版本没有 Inverted 选项，开关会变成「默认 lilToon、勾选后变 NonToon」。");

            EditorUtility.SetDirty(component);
            return true;
        }

        private static bool AddMaterialSwap(GameObject host, GameObject swapRoot, IList<SwitchPair> pairs,
            bool nonToonOnByDefault, ConversionResult result)
        {
            var swapType = FindType(MaterialSwapTypeName);
            if (swapType == null)
            {
                result.Error("找不到 MA Material Swap 组件类型。");
                return false;
            }

            var component = host.AddComponent(swapType);
            if (component == null)
            {
                result.Error("无法把 MA Material Swap 添加到 " + host.name + " 上。");
                return false;
            }

            // Root: only renderers below this object are affected.
            var rootProperty = swapType.GetProperty("Root", BindingFlags.Public | BindingFlags.Instance);
            if (rootProperty != null)
            {
                var reference = rootProperty.GetValue(component, null);
                if (reference == null && rootProperty.CanWrite)
                {
                    var referenceType = FindType(ObjectReferenceTypeName);
                    if (referenceType != null)
                    {
                        reference = Activator.CreateInstance(referenceType);
                        rootProperty.SetValue(component, reference, null);
                    }
                }
                if (reference != null && swapRoot != null)
                {
                    var setMethod = reference.GetType().GetMethod("Set", new[] { typeof(GameObject) });
                    if (setMethod != null) setMethod.Invoke(reference, new object[] { swapRoot });
                }
            }
            else
            {
                result.Warn("当前 Modular Avatar 版本的 MA Material Swap 没有 Root 属性，替换范围会变成整个 avatar。");
            }

            // Swaps: original lilToon material -> converted NonToon material.
            var swapsProperty = swapType.GetProperty("Swaps", BindingFlags.Public | BindingFlags.Instance);
            var swaps = swapsProperty != null ? swapsProperty.GetValue(component, null) as IList : null;
            if (swaps == null)
            {
                result.Error("当前 Modular Avatar 版本的 MA Material Swap 没有 Swaps 列表。");
                return false;
            }

            var matSwapType = swapsProperty.PropertyType.IsGenericType
                ? swapsProperty.PropertyType.GetGenericArguments()[0]
                : FindNestedType(swapType, "MatSwap");
            if (matSwapType == null)
            {
                result.Error("无法解析 MA Material Swap 的条目类型。");
                return false;
            }

            var fromField = matSwapType.GetField("From", BindingFlags.Public | BindingFlags.Instance);
            var toField = matSwapType.GetField("To", BindingFlags.Public | BindingFlags.Instance);
            if (fromField == null || toField == null)
            {
                result.Error("无法解析 MA Material Swap 的条目字段。");
                return false;
            }

            foreach (var pair in pairs)
            {
                if (pair.Original == null || pair.Converted == null) continue;
                var entry = Activator.CreateInstance(matSwapType);
                fromField.SetValue(entry, pair.Original);
                toField.SetValue(entry, pair.Converted);
                swaps.Add(entry);
            }

            // Reversed switch: NonToon is shown while the toggle is off.
            var invertedProperty = swapType.GetProperty("Inverted", BindingFlags.Public | BindingFlags.Instance);
            if (invertedProperty != null && invertedProperty.CanWrite)
                invertedProperty.SetValue(component, nonToonOnByDefault, null);
            else if (nonToonOnByDefault)
                result.Warn("当前 Modular Avatar 版本不支持 Inverted，开关会变成「默认 lilToon、勾选后变 NonToon」。");

            SetQuickSwapMode(swapType, component);

            EditorUtility.SetDirty(component);
            return true;
        }

        /// <summary>Enables MA's quick swap arrows in the inspector (uses the public property when available).</summary>
        private static void SetQuickSwapMode(Type swapType, Component component)
        {
            var property = swapType.GetProperty("QuickSwapMode", BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite && property.PropertyType.IsEnum)
            {
                try
                {
                    property.SetValue(component, Enum.ToObject(property.PropertyType, 1), null); // SameDirectory
                    return;
                }
                catch (Exception)
                {
                    // fall through to the serialized field
                }
            }

            var field = swapType.GetField("m_quickSwapMode", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType.IsEnum)
            {
                try
                {
                    field.SetValue(component, Enum.ToObject(field.FieldType, 1));
                }
                catch (Exception)
                {
                    // purely cosmetic, ignore
                }
            }
        }

        private static void AddMenuToggle(GameObject host, string parameterName, string label, ConversionResult result)
        {
            var menuItemType = FindType(MenuItemTypeName);
            if (menuItemType == null)
            {
                result.Warn("找不到 MA Menu Item，切换对象已创建但没有菜单开关。");
                return;
            }

            var item = host.AddComponent(menuItemType);
            if (item == null)
            {
                result.Warn("无法添加 MA Menu Item。");
                return;
            }

            SetMember(item, "label", label);
            SetMember(item, "isSynced", true);
            SetMember(item, "isSaved", true);
            SetMember(item, "isDefault", false);

            // PortableControl is the API that exists both with and without the VRChat SDK; the Control
            // property (VRChat SDK builds) is the one the MA inspector actually shows, so set both.
            var control = GetMember(item, "PortableControl");
            if (control != null)
            {
                SetMember(control, "Name", label);
                SetMember(control, "Parameter", parameterName);
                SetMember(control, "Value", 1f);
                var typeProperty = control.GetType().GetProperty("Type");
                if (typeProperty != null && typeProperty.PropertyType.IsEnum)
                {
                    try
                    {
                        typeProperty.SetValue(control, Enum.ToObject(typeProperty.PropertyType, 102), null); // Toggle
                        if (typeProperty.GetValue(control, null).ToString() != "Toggle")
                            typeProperty.SetValue(control, Enum.ToObject(typeProperty.PropertyType, 1), null);
                    }
                    catch (Exception exception)
                    {
                        result.Warn("无法把 MA Menu Item 的类型设为 Toggle：" + exception.Message);
                    }
                }
            }
            else
            {
                result.Warn("无法设置 MA Menu Item 的控件，请手动填写菜单显示名和参数名。");
            }

            // Install the menu item into the avatar's expression menu.
            var installerType = FindType(MenuInstallerTypeName);
            if (installerType != null)
            {
                var installer = host.AddComponent(installerType);
                SetMember(installer, "menuToAppend", null);
            }

            EditorUtility.SetDirty(item);
        }

        private static void SetMember(object target, string name, object value)
        {
            if (target == null) return;
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                try
                {
                    property.SetValue(target, value, null);
                    return;
                }
                catch (Exception)
                {
                    // try the field below
                }
            }
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                try
                {
                    field.SetValue(target, value);
                }
                catch (Exception)
                {
                    // ignore
                }
            }
        }

        private static object GetMember(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                try
                {
                    return property.GetValue(target, null);
                }
                catch (Exception)
                {
                    // try the field below
                }
            }
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                try
                {
                    return field.GetValue(target);
                }
                catch (Exception)
                {
                    // ignore
                }
            }
            return null;
        }

        private static Type FindNestedType(Type type, string nestedName)
        {
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (nested.Name == nestedName) return nested;
            }
            return null;
        }
    }
}
