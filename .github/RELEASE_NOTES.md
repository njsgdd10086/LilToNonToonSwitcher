**LilToNonToon Switcher 1.1.0** —— 修掉两个转换质量问题：**用色调校正 / HDR 主色改色的衣服颜色变回去**、**镂空 / 透明材质转成不透明**。

## 修复内容

### 1. 色调校正（HSV / Gamma）与渐变映射现在会被烘焙

lilToon 的主色处理顺序是：

```
贴图 → 色调校正 HSV/Gamma（可被 _MainColorAdjustMask 限制范围）→ 渐变映射 → × _Color（含 Alpha / HDR）
```

之前只做了最后一步 `_MainTex × _Color`，**而且 `_Color` 是白色（HDR 白也算白）时直接沿用原贴图** ——
所以那些「用 HDR 主色 + HSV/Gamma 调色」的衣服，转换后颜色就回到了原始状态。

现在整条链都会烘焙进 `_BaseTexture`（线性工程下按线性空间算，和 shader 里一致），
数值全为默认时依旧直接沿用原贴图。

### 2. 镂空 / 透明的材质不再变成不透明

lilToon 的 Inspector 一旦选了渲染模式，材质会被换成对应的**隐藏变体**
（`Hidden/lilToonCutout`、`Hidden/lilToonTransparentOutline`、`Hidden/lilToonTwoPassTransparent` …），
这些材质的 `_TransparentMode` 往往还是 0，旧代码只看属性不看 shader 名，于是判成 Opaque。

现在先按 **shader 名字**判断，再退回属性；并且：

- Opaque → `_RenderingMode 0`、queue 默认；
- Cutout → `_RenderingMode 1`、queue 2450、`_AlphaToMask`（有抖动贴图时不用）；
- Transparent → `_RenderingMode 2`、queue 2460、`_ZWrite 0`（和 lilToon 的透明变体一致）。

### 3. 顺带修掉读取贴图时的颜色空间不一致

没勾 Read/Write 的贴图会走 RenderTexture 兜底，那条路以前返回**线性化后**的数值，
和 `GetPixels()` 的原始数值不一样，颜色乘算会算得过暗。现在两条路径返回同一份数据。

## 已经验证过

在你的工程里跑了一遍（16 项检查全过）：

- 饱和度设 0 → 烘焙结果确实是灰度（最大通道差 0.012）；
- 伽马 2 → 平均亮度 0.315 → 0.116；主色 0.5 → 0.315 → 0.222；
- 默认值 → 不生成烘焙贴图（沿用原贴图）；
- `Hidden/lilToonCutout` 的材质 → `_RenderingMode 1` / queue 2450；
- `Hidden/lilToonTransparentOutline` 的材质 → `_RenderingMode 2` / queue 2460 / `_ZWrite 0`。

## 升级后请这样做

1. ALCOM 更新到 1.1.0；
2. **重新转换**那几件出问题的衣服（转换不会动原 lilToon 材质，直接再转一次即可覆盖同名输出）；
3. 在 Unity 里对比一下颜色和镂空 / 透明是否正常。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
