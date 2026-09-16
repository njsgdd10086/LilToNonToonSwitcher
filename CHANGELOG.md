# 更新日志

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.1.3] - 2026-09-16

### 修复

- **描边后移会被误判触发**：1.1.2 判断"源材质有没有用描边宽度贴图"时只看了
  `GetTexture("_OutlineWidthMask") != null`，但 Unity 对**没设置过**的贴图属性会返回
  shader 里声明的默认贴图（lilToon 写的是 `"white"`），那**不是 null**。
  于是那些压根没写这个属性、根本没用宽度遮罩的材质（实测 liltonon 工程里 24 个 lilToon 材质中有 2 个）
  也会被白白后移描边。现在会排除所有内置默认贴图（`whiteTexture` / `blackTexture` /
  `grayTexture` / `normalTexture` 等），只有真的挂了 PNG 才算数。

### 新增

- **描边后移的日志写细了**：凡是"源材质挂了宽度贴图"的材质都会说明自己的结果，包括没后移的原因：

  ```
  · 描边宽度贴图（NonToon 没有这个功能） -> 描边整体后移 0.0007（_OutlineZOffset，倍数 1 × 描边宽度）…
  · 描边宽度贴图（NonToon 没有这个功能） -> 描边宽度为 0，本来就没有描边，未做后移
  · 描边宽度贴图（NonToon 没有这个功能） -> 后移倍数 = 0，未做处理（描边可能盖住嘴唇 / 眼睛）
  · 描边宽度贴图（NonToon 没有这个功能） -> 原有 _OutlineZOffset 0.001 已经不小于后移量，保持不动
  ```

  没挂宽度贴图的材质不会打印这一行（否则每个材质都多一行），所以
  **日志里没有这一行 = 没用宽度遮罩 = 不需要处理**。

## [1.1.2] - 2026-09-16

### 修复

- **嘴唇 / 眼睛附近被描边"糊住"、看起来像脸被撕破**：
  lilToon 的描边宽度贴图（`_OutlineWidthMask`）NonToon 没有对应功能 —— 作者常用它把五官附近的描边宽度压成 0，
  而 NonToon 的描边是**均匀**的反向外扩壳，于是嘴腔内壁的壳照样外扩、压在嘴唇上，
  并且它采样的是该处 UV 的基础贴图颜色（正是嘴部那块深红），看起来就是"脸被撕破"。

  现在转换时会把描边**整体沿视线方向后移**（写进 `_OutlineZOffset`）：

  ```
  后移量 = _OutlineWidth × 0.01 × 倍数（默认倍数 1）
  ```

  `× 0.01` 是 NonToon 自己的换算（`pos += N * _OutlineWidth * 0.01`），所以后移量正好等于描边自身的外扩距离：
  描边壳上任何顶点都不会比原位更靠近相机 → 有表面挡着的地方必然被深度测试剔除；
  剪影处法线垂直于视线、后移不改变屏幕位置，描边依旧保留。

  只在「源材质的 `_OutlineWidthMask` 上真的挂了贴图」时生效，其它材质完全不动；
  原本 `_OutlineZOffset` 就更大的材质不会被改小。

### 新增

- **描边后移倍数可调**（默认 1）：菜单 `Tools > LilToNonToon Switcher > 描边后移倍数 >`
  可选 `0（不处理）/ 0.5 / 1 / 1.5 / 2`，设置窗口「高级设置」里也有 0–3 的滑条。
  调小＝描边更明显但凹处风险回升，调大＝更保险但描边可能被邻近几何吃掉。
- **转换方式三选一**（菜单 `Tools > LilToNonToon Switcher > 转换方式 >`，窗口里有同样的下拉）：
  1. **保留原材质 + 建切换开关**（默认，和以前一样）；
  2. **直接替换成 NonToon（原地替换）**：不建开关，回退只能靠 `Ctrl+Z` / 版本管理；
  3. **复制一份 `_nontoon` 后替换**：把选中对象复制成 `<名字>_nontoon`，材质换在副本上，
     **原对象保留 lilToon 材质但自动取消勾选**（想回退就把它勾回来）。

  第 3 种的细节：副本里会自动删掉之前生成的 `_NonToonSwitch`（否则副本里既有 NonToon 材质、
  又有会切回 lilToon 的开关，互相打架）；重复转换会复用已有的同名副本；副本创建与取消勾选都走 Undo；
  转换报告里会写明副本名字，并提示"场景里有两个 Avatar 描述符，上传时选 `_nontoon` 那一份"。
  副本是 `Instantiate` 出来的普通对象，不会挂回 prefab。

## [1.1.1] - 2026-09-15

### 修复

- **透明材质转完「该实心的地方透了、前后遮挡也乱了」**（1.1.0 引入）。
  1.1.0 判断出透明模式后，会把 `_ZWrite` 强制设成 0、Blend 设成 `SrcAlpha / OneMinusSrcAlpha`、
  渲染队列设成 2460 —— 但 lilToon 的混合方式 / `_ZWrite` / `_Cull` / `_AlphaToMask` / 渲染队列
  **全都是材质驱动的**（pass 里写的是 `Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]`、
  `ZWrite [_ZWrite]`，渲染队列也能被材质覆盖），作者经常故意调成
  「透明混合但仍然写深度、还待在几何队列里」（例如 MANUKA 的脸、头发就是
  `Blend One OneMinusSrcAlpha` + `_ZWrite 1` + 队列 2000 / 2450）。
  强制改成 NonToon 的默认值以后，这些部件就会透出背后的东西，遮挡顺序也跟着乱。

  现在 `_RenderingMode` 只负责 NonToon 怎么处理 alpha（不透明强制 1 / 镂空做剪切 / 透明保留），
  以下这些**全部沿用原材质**：

  ```
  _Cull、_SrcBlend、_DstBlend、_SrcBlendAlpha、_DstBlendAlpha、_ZWrite、_AlphaToMask
  渲染队列：原材质自己设过就照搬，否则用原 shader 声明的队列（透明 2460 / 镂空 2450 / 不透明 2000）
  ```

  只有原材质没有这些属性时，才退回 NonToon 自己那套模式默认值（和它的渲染模式下拉框一致）。
  转换日志会多一行 `渲染状态（沿用原材质）`，写明实际沿用了哪些值、队列是多少。

## [1.1.0] - 2026-09-15

### 修复

- **用 HDR 主色 / 色调校正改色的衣服，转换后颜色变回去了**：
  之前只把 `_MainTex × _Color` 烘焙进 `_BaseTexture`，而且 `_Color` 是白色（HDR 白也算白色）时直接沿用原贴图，
  于是 lilToon 的「色调校正」（HSV / Gamma + `_MainColorAdjustMask`）和「渐变映射」
  （`_MainGradationTex` / `_MainGradationStrength`）全都没了。
  现在按 lilToon 的处理顺序完整烘焙：

  ```
  贴图 → 色调校正 HSV/Gamma → 渐变映射 → 色调校正遮罩（lerp）→ × _Color（含 Alpha / HDR）
  ```

  数值全为默认时仍然直接沿用原贴图（不会白白生成一张 PNG）。
- **原来是「镂空 / 透明」的材质，转完变成不透明**：lilToon 的 Inspector 一旦选了渲染模式，
  材质会被换成对应的隐藏变体（`Hidden/lilToonCutout`、`Hidden/lilToonTransparentOutline`、
  `Hidden/lilToonTwoPassTransparent` …），这些材质的 `_TransparentMode` 往往还是 0，
  旧代码只看属性、不看 shader 名，于是判成 Opaque。
  现在先按 **shader 名字**判断，再退回属性；透明模式会一并把 `_ZWrite` 关掉（和 lilToon 的透明变体一致），
  Blend / RenderQueue 的数值与 NonToon 自己的渲染模式下拉框完全一致。
- **没有勾选 Read/Write 的贴图，读取时颜色空间不一致**：走 RenderTexture 兜底的那条路以前返回线性化后的数值，
  和 `GetPixels()` 的原始数值不一样，颜色乘算 / 色调校正会算得过暗。现在两条路径返回同一份数据。

### 新增

- 转换日志会写明渲染模式是**按 shader 名**判断出来的
  （例如 `rendering mode Cutout（按 shader 名判断：Hidden/lilToonCutout）`）；
  主色烘焙也会列出到底烘焙了哪几项（色调校正 / 渐变映射 / 遮罩 / 主色）。

## [1.0.3] - 2026-09-13

### 新增

- 菜单里新增「是否复用已有 `_NonToonSwitch`」的开关（`Tools > LilToNonToon Switcher >` 下两项互斥打勾）：
  - **转换时复用已有的 _NonToonSwitch**（默认）
  - **转换时总是新建 _NonToonSwitch**
  设置窗口里也有对应复选框，设置保存在 ProjectSettings 中

## [1.0.2] - 2026-09-13

### 修复

- **同一个 avatar 下重复转换不再堆积开关**：已存在 `_NonToonSwitch` 时改为把新的材质条目追加进去，
  菜单项也不会重复创建（之前每转换一次就新建一个开关，转几次就会出现几个）
- 没有 avatar 时，切换对象改为挂在公共祖先的父级／场景根，不再塞进被选中的对象内部
- 输出文件夹改为**递归创建**：`Assets/A/B/C` 这种多层路径即使中间目录都不存在也能建出来
  （之前会直接报「找不到上级目录」而中止转换）

## [1.0.1] - 2026-09-13

### 修复

- 发布流水线：把包校验与 VPM 索引生成抽成 `scripts/` 下的脚本，本地与 CI 共用同一份逻辑
- 发布流水线：gh-pages 任务改为在仓库根目录运行，修复找不到脚本导致的失败
- 发布流水线：Release 上传后校验附件确实存在，避免出现「只有标签、没有包」的情况
- `.meta` 文件统一使用 LF 换行

## [1.0.0] - 2026-09-13

首个版本。

### 新增

- 右键选中对象 → 批量把 lilToon 材质转换为 NonToon
- 转换结果保存为 `<原材质名>_nontoon.mat`，连带烘焙的贴图与渐变使用同一前缀
- 自动生成 Modular Avatar 的 **MA Material Setter**（对象 + 材质槽 + 材质）+ 菜单开关，实现一键切换 shader
- 也可选择 MA Material Swap 模式
- 识别 lilToon 全部 65 个 shader（含 `Hidden/lilToonOutline` 等隐藏变体）
- 属性转换：
  - `_MainTex` × `_Color` 烘焙为 PNG 写入 `_BaseTexture`
  - `_BumpMap` / `_BumpScale` → `_NormalMap` / `_NormalScale`
  - `_TransparentMode` → `_RenderingMode`（Opaque / Cutout / Transparent）+ Blend / RenderQueue
  - `_OutlineColor` / `_OutlineWidth` / `_OutlineZBias`
  - lilToon 的各个遮罩写入 `_SharedMask` 的对应通道
  - 由阴影色、边缘阴影色生成 Shader Core 的 Gradient（`.scgradients`）
  - `_Smoothness` → `_Roughness`、`_ReflectionColor` → `_SpecularColor`
  - Stencil / Cull / ZWrite / Blend 等渲染状态
- 转换日志（Console 与窗口）。未支持的功能以警告记录，不会静默丢弃
- 设置窗口（输出文件夹、开关默认状态、菜单名、各项烘焙开关）
- `Tools > LilToNonToon Switcher > 导出为 unitypackage`
- VPM 分发：推送 `v*` 标签自动打包、创建 Release 并更新 VPM 索引

### 修复

- 解决 Shader Core 的 shader 上 `Material.SetInt` 不落盘的问题（整型属性改走 SerializedObject 读写）
- 解决烘焙资源 import 导致材质内存值回滚的问题（保存后重新取回实例）
- 修复 RimShade 渐变索引越界导致边缘发黑的问题
- 修复 Material Setter 模式下原地替换材质、导致开关失效的问题
- 修复 `_ReflectionColor` 被 reflectance 压暗、高光几乎不可见的问题
