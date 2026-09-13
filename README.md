# LilToNonToon Switcher

**lilToon → NonToon 一键转换 + 切换开关（Unity 编辑器扩展）**

选中对象右键即可：把对象里的 **lilToon** 材质转换成 **NonToon**、保存到指定文件夹，
并自动创建 **Modular Avatar 的 MA Material Setter + 菜单开关**（一键切换 shader 的开关）。

[English summary ↓](#english-summary)

---

## 一、环境要求

| 包 | 用途 | 是否必需 |
| --- | --- | --- |
| `jp.lilxyzw.liltoon` | 转换来源 shader | 必需 |
| `jp.lilxyzw.nontoon` + `jp.lilxyzw.shadercore` | 转换目标 shader | 必需 |
| `nadena.dev.modular-avatar` | 切换组件（Material Setter / Swap、Menu Item） | 只在需要做开关时必需 |
| VRChat SDK3 Avatars | 把菜单装进 avatar | 只在需要菜单开关时必需 |

都可以从 VCC / ALCOM 的 VPM 安装。针对 NonToon 0.1.x 编写；属性名是**运行时从 shader 里读的**，
所以后续 NonToon 增删模块、改名字也能跟上。

> lilToon 一共有 65 个 shader，绝大多数材质用的是隐藏变体（`Hidden/lilToonOutline`、
> `Hidden/lilToonTransparent` 等）。本工具全部识别。

## 二、安装

### 方式 1：VCC / ALCOM（推荐）

1. 打开 VCC（或 ALCOM）→ **Settings → Packages → Add Repository**
2. 填入仓库地址：

   ```
   https://njsgdd10086.github.io/LilToNonToonSwitcher/index.json
   ```

3. 回到项目的 **Manage Project**，在 `LilToNonToon Switcher` 上点 **Add**。
   之后有新版本时，VCC / ALCOM 会直接提示升级。

### 方式 2：unitypackage

1. 从 Releases 下载 `LilToNonToonSwitcher.unitypackage`。
2. 拖进 Unity 项目 → Import。
3. 菜单里出现 `Tools > LilToNonToon Switcher` 就装好了。

### 方式 3：直接放进项目

把 `Editor/` 文件夹、`package.json` 以及配套的 `.meta` 文件放到 `Assets/LilToNonToonSwitcher/` 下即可。
仓库里已经包含固定 GUID 的 `.meta` 文件，导入不会每次产生新 GUID。

> **升级注意**：如果装过旧版本，请先在 Project 窗口里删除 `Assets/LilToNonToonSwitcher` 文件夹，
> 再导入新版本，避免新旧两套同名脚本同时存在。

## 三、用法（右键一键转换）

1. 在 Hierarchy 里**选中 avatar 本体 / 衣服等对象**（可以多选）。
2. **右键 → `LilToNonToon` → `将选中对象转换为 NonToon`**（快捷键 `Ctrl+Shift+T`，Tools 菜单里也有）。
3. 自动完成下面这些事：

```
选中对象
 ├─ 找出其中的 lilToon 材质（含 Hidden/lilToon* 隐藏变体）
 ├─ 生成 NonToon 材质，保存到 Assets/NonToonConverted/<对象名>/
 │   （同时烘焙 _MainTex×_Color、共享遮罩、阴影渐变）
 └─ 创建 Modular Avatar 切换对象
     └─ _NonToonSwitch
         ├─ MA Material Setter（默认；每条 = 对象 + 材质槽 + NonToon 材质）
         └─ MA Menu Item     （Toggle「NonToon」）
```

4. 上传后 Expression Menu 里会多一个 **NonToon** 开关，勾一下就能在 lilToon / NonToon 之间切换。

> **重要**：默认（Material Setter 模式）**不会**把 Renderer 上的材质换掉——Renderer 保持原来的 lilToon，
> NonToon 材质只出现在 Setter 的条目里，由开关负责切换。
> 这样开关才有效：如果槽里已经是 NonToon，开关就等于没有作用了。

### 输出文件夹与命名

默认输出到 `Assets/NonToonConverted/<选中对象名>/`，可在
`Tools > LilToNonToon Switcher > 设置与转换窗口` 里修改。

生成的材质命名为 **`<原材质名>_nontoon.mat`**，例如 `衣服.mat` → `衣服_nontoon.mat`。
配套生成的贴图/渐变沿用同一前缀，便于对应：

```
Assets/NonToonConverted/衣服/
├─ 衣服_nontoon.mat                    ← 转换后的材质
├─ 衣服_nontoon_Base.png               ← _MainTex × _Color 烘焙
├─ 衣服_nontoon_NTMask.png             ← 汇总后的 _SharedMask
└─ 衣服_nontoon_Gradients.scgradients  ← 阴影渐变
```

同名冲突时自动加序号（`衣服_nontoon_2.mat`）；原材质名已以 `_nontoon` 结尾时不会重复追加。

### 开关的默认状态

| 设置 | 转换后看到的效果 | 开关勾选时 |
| --- | --- | --- |
| **将 NonToon 设为默认**（默认开启） | NonToon | 变回 lilToon |
| 关闭 | lilToon | 变成 NonToon |

用的是 MA 组件上的 `Inverted`。若 MA 版本不支持，会自动退化成关闭时的行为并在日志里提示。

### 切换组件类型

在设置窗口里可选：

| 模式 | 生成的组件 | Renderer 材质 | 说明 |
| --- | --- | --- | --- |
| **MA Material Setter**（默认） | `ModularAvatarMaterialSetter` | **保持 lilToon** | 每条记录「对象 + 材质槽索引 + NonToon 材质」，能精确控制到某个 Renderer 的某一槽 |
| MA Material Swap | `ModularAvatarMaterialSwap` | 替换为 NonToon | 只按「原材质 → 新材质」成对替换，作用于 Root 下所有 Renderer |

两种模式都配合 `MA Menu Item` 生成开关。MA 在构建 avatar 时自动生成切换动画，
不需要手写动画与 Animator。

### 全部菜单项

| 菜单 | 作用 |
| --- | --- |
| `GameObject > LilToNonToon > 将选中对象转换为 NonToon %#t` | 按当前设置转换 + 建开关（`Ctrl+Shift+T`） |
| `GameObject > LilToNonToon > 转换并打开设置窗口` | 打开设置窗口 |
| `GameObject > LilToNonToon > 只转换材质（不建开关）` | 只转材质，不需要 Modular Avatar |
| `GameObject > LilToNonToon > 查看转换日志` | 打开设置窗口并查看日志 |
| `Tools > LilToNonToon Switcher > 转换选中对象为 NonToon` | Tools 菜单入口 |
| `Tools > LilToNonToon Switcher > 设置与转换窗口` | 设置窗口 |
| `Tools > LilToNonToon Switcher > 环境检查` | 输出 lilToon / NonToon / MA 的安装情况到 Console |
| `Tools > LilToNonToon Switcher > 导出为 unitypackage` | 源码放在 `Assets/LilToNonToonSwitcher` 时可重新打包 |

## 四、转换了哪些东西

对照表在 `Editor/NonToonPropertyMap.cs`。分三步进行：

1. **显式对照表**：语义会变的（渲染模式、遮罩、渐变、描边等）
2. **同名属性自动复制**：`_Cull`、`_ZWrite`、`_Stencil*`、`_Cutoff` 等
3. **归一化名称模糊匹配**：改过名字但类型相同的属性

| lilToon | NonToon | 说明 |
| --- | --- | --- |
| `_MainTex` × `_Color` | `_BaseTexture` | 生成带颜色乘算的 PNG（Scale/Offset 一并继承） |
| `_BumpMap` / `_BumpScale` | `_NormalMap` / `_NormalScale` | 直接复制 |
| `_Cutoff` | `_Cutoff` | 直接复制 |
| `_TransparentMode` | `_RenderingMode` + Blend/Queue | 分别对应 Opaque / Cutout / Transparent |
| `_OutlineColor` / `_OutlineWidth` / `_OutlineZBias` | `_OutlineColor` / `_OutlineWidth` / `_OutlineZOffset` | 近似复制 |
| `_OutlineVertexR2Width` | `_OutlineFromVertexColor` | 用顶点色控制描边宽度 |
| `_AlphaMask` | `_SharedMask`（对应通道） | 烘焙进共享遮罩 |
| `_RimColorTex` / `_BacklightColorTex` / `_MatCapBlendMask` / `_ReflectionColorTex` | `_SharedMask` | 读取各模块的 Mask Channel 设置，写进对应通道 |
| `_ShadowColor` / `_Shadow2ndColor` / `_Shadow3rdColor` + Border | `_SharedGradients`（`.scgradients`）+ `_ShadeGradientIndex` | 生成阴影渐变 |
| `_RimColor` / `_RimBorder` / `_RimBlur` | `_RimLightColor` / `_RimLightRange` | 由边界与模糊近似出 Range |
| `_MatCapTex` / `_MatCapColor` / `_MatCapBlendMode` | `_MatCapMultiply*` / `_MatCapAdd*` | 按混合方式选择加算/乘算 |
| `_ReflectionColor` | `_SpecularColor` | 原样复制（**不**乘 `_Reflectance`，否则高光会被压黑） |
| `_Smoothness` | `_Roughness` | `roughness = 1 - smoothness`；`_UseReflection` 关闭时保持 NonToon 默认值 |
| `_Stencil*`、`_OutlineStencil*`、`_Cull`、`_ZWrite`、`_SrcBlend`、`_DstBlend`、`_AlphaToMask` | 同名 | 直接复制 |

### 无法转换的功能

NonToon 0.1.x 里没有对应项的功能，**不会被悄悄丢掉**，而是作为警告写进转换日志：

- Emission（自发光）/ Emission2nd、Glitter、AudioLink、Dissolve、Parallax、Tessellation
- Main 2nd / 3rd（贴花）、UV 动画
- MatCap 2nd、Reflection Cube、Metallic
- Fur 的细节设置、Gem、Refraction
- 描边的贴图与宽度遮罩（只搬颜色和粗细）

## 五、转换日志

- 设置里打开「输出日志到 Console」后，转换内容、近似处理、未支持功能都会打到 Console。
- 窗口下方的**转换结果**里也能看到同样的内容。

## 六、注意

- 转换**不会修改原来的 lilToon 材质**，而是新建一份 NonToon 材质文件。
- 改设置后重新转换会覆盖同名的输出材质（同名但不同来源的材质会以 `_2` 等后缀避让）。
- lilToon 与 NonToon 的反射、阴影模型不同，转换结果请务必在 Unity 里目视确认，
  再调 Roughness、渐变和各模块设置。
- 上传前确认 MA Material Setter / Swap 的条目只覆盖了想替换的那些 Renderer。

## 七、文件结构

```
LilToNonToonSwitcher/
├─ package.json
├─ README.md
├─ CHANGELOG.md
├─ LICENSE
└─ Editor/
   ├─ NonToonSwitcherMenu.cs       … 右键 / Tools 菜单
   ├─ NonToonSwitcherWindow.cs     … 设置与转换窗口
   ├─ NonToonSwitcherSettings.cs   … 设置保存（ProjectSettings）
   ├─ NonToonConverter.cs          … 转换主体（检测 → 转换 → 替换 → 建开关）
   ├─ NonToonPropertyMap.cs        … lilToon → NonToon 对照表
   ├─ NonToonMaskBuilder.cs        … 共享遮罩（_SharedMask）生成
   ├─ NonToonTextureBaker.cs       … 基础贴图 / 阴影渐变生成
   ├─ NonToonSwitcherBuilder.cs    … Modular Avatar 联动（Material Setter / Swap、Menu Item）
   ├─ NonToonPackager.cs           … 重新打包 unitypackage
   ├─ ShaderUtility.cs             … shader 判定与属性读写
   └─ ConversionLog.cs             … 日志
```

## 八、实现备注

**整型属性**：Unity 对 Shader Core 这类 shader 的整型属性，`Material.SetInt()` 会静默失效——
调用不报错、内存里 `GetInt()` 也读得回来，但 `AssetDatabase.SaveAssets()` 写进 `.mat` 的仍是默认值 0；
反过来，文件里确实是 `_RenderingMode: 2` 的材质，`GetInt()` 也读成 0。
因此整型属性统一走 `SerializedObject` 的 `m_SavedProperties.m_Ints` 读写
（也就是 NonToon 自己 Inspector 落盘的形式），渲染模式、渐变色索引才能正确保存。

**Modular Avatar 联动**全部用反射实现，所以 MA 版本变化不会导致本工具编译报错
（联动失败时会在日志里给出警告）。

## 九、开源说明

- 许可证：**MIT**（见 [LICENSE](LICENSE)）。
- 本项目的**代码为独立实现**，没有复制其他转换工具的源码。转换思路参考了社区同类工具
  （例如 BOOTH 上的 *lil to non toon converter*）以及 lilToon / NonToon / Shader Core 公开的
  shader 与编辑器脚本行为。
- 依赖的第三方项目均属其各自作者，本仓库不包含它们的代码：
  [lilToon](https://github.com/lilxyzw/lilToon)、[NonToon](https://github.com/lilxyzw/NonToon)、
  [Shader Core](https://github.com/lilxyzw/Shader-Core)、
  [Modular Avatar](https://github.com/bdunderscore/modular-avatar)。
- 转换是**近似**的：lilToon 与 NonToon 的光照/反射模型不同，转换后请目视确认并微调。

## 十、发布流程（维护者）

推一个 `v*` 标签即会自动完成打包与索引更新：

```bash
# 1) 先改 package.json 的 version（例如 1.0.1），提交
git commit -am "chore: 版本 1.0.1"
git push

# 2) 打标签并推送，触发 .github/workflows/release.yml
git tag v1.0.1
git push origin v1.0.1
```

工作流会：

1. 校验标签与 `package.json` 版本一致
2. 把包内容打成 `com.nontoon.switcher-<版本>.zip`（zip 根目录直接是 `package.json`、`Editor/`，VPM 要求）
3. 创建 GitHub Release 并上传该 zip
4. 由 Release 资产生成 `vpm.json` / `index.json`，推送到 `gh-pages` 分支

VPM 索引地址（稳定不变）：

```
https://njsgdd10086.github.io/LilToNonToonSwitcher/index.json
```

> 首次发布后，需要在仓库 **Settings → Pages** 里把 Source 设为 **Deploy from a branch → gh-pages / (root)**，
> 上面的索引地址才会生效。

## English summary

**LilToNonToon Switcher** is a Unity Editor extension that converts the lilToon materials of the
selected object(s) into NonToon, saves them as `<name>_nontoon.mat`, and builds a Modular Avatar
**Material Setter + Menu Item** so that a single Expression Menu toggle switches the avatar
between lilToon and NonToon.

Requirements: `jp.lilxyzw.liltoon`, `jp.lilxyzw.nontoon` (+ `jp.lilxyzw.shadercore`) and, for the
toggle, `nadena.dev.modular-avatar` plus the VRChat SDK3 Avatars package.

Usage: select the object in the Hierarchy → right click → `LilToNonToon` →
`Convert Selection to NonToon` (`Ctrl+Shift+T`).

License: MIT. The code is an independent implementation; no source code of other converters was
copied. See the Chinese sections above for the full documentation.
