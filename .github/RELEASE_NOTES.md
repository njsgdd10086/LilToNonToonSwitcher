**LilToNonToon Switcher 1.1.14** —— 补上「第二层 MatCap」，并修掉模块开关不生效的问题（金饰/装饰终于能对上了）。

## 一、第二层 MatCap 从来没转过

lilToon 有**两层** MatCap：`_MatCapTex`（第一层）和 `_MatCap2ndTex`（第二层）。
我们以前只转第一层 ✗ —— 所以走第二层的装饰（帽子的**羽毛 / 玫瑰**）转换后一直是灰的 ✗。

NonToon 恰好有两个槽 ✓，现在：

```
第一层（_MatCapTex，模式 0/1/2 → Add、3 → Multiply）→ 占一个槽
第二层（_MatCap2ndTex）                              → 用剩下那个槽
```

配套：`_MatCap2ndColor × _MatCap2ndBlend` 写进对应颜色 ✓、`_MatCap2ndBlendMask` 按第二层的槽位分配遮罩通道 ✓、
`_UseMatCap2nd = 0` 时留空 ✓。

## 二、模块开关「勾着但不生效」

NonToon 的模块开关带 `SCConstValue`，真正让它生效的是材质上的**关键字**
`<属性名大写>_<值>`（MatCap 是 `_JP_LILXYZW_NONTOON_MATCAPS_ENABLE_1`）。
我们以前只写 `_Enable` 整数 ✗ —— 表现就是「开关明明勾着，却要手动在 Inspector 里取消再勾一次才亮」✗。
现在整数 + 关键字都写 ✓，并在转换后**强制重新导入材质**（模块状态需要这次刷新 ✓）。

## 三、顺带一起修的

- 遮罩通道按**实际使用的槽位**分配（以前写死 `MatCapMultiply` ✗ → 数据烘进 R、模块读 A ✗，被乘成 ~0），写完还读回校验 ✓
- `_BumpMap` 现在**同时**接到 Details 模块的 `_Detail0NormalMap`（喂 `sd.N_detail`，Shade 模块真的用它算明暗 ✓），
  并把四层 `Detail*Boost` 钉成 1 ✓（详情层会 `albedo *= detailTex * boost`，boost 不是 1 会整体改亮度 ✗）
- 不需要烘焙时 `_BaseTexture` **指回源贴图** ✓（不再残留旧烘焙图 ✓）
- 烘焙贴图保持**原长宽比** ✓；MatCap 颜色**总是写入** ✓

## 已知限制

- **金属反射**：lilToon 的 `_UseReflection`（`_Metallic` / `_Smoothness` / 环境反射）NonToon 没有对应能力 ✗，
  只能近似成高光 —— 「靠反射变金」的部分会比原版偏灰 ✓。
- **织物质感 / 法线细节**：NonToon 是 toon 硬色阶 + 硬高光 ✗，法线扰动没有足够的输出通道 ✓ ——
  主 `_NormalMap`、Details 的 `_Detail0NormalMap`、调高 `_NormalScale`、降低 `_Roughness` 都试过 ✓，
  效果都不理想（降低 roughness 反而变塑料 ✗）。要完全一致只能自建 shader / 写模块 ✓。

## 升级后

ALCOM 更新到 1.1.14 → **重新转换**用了 MatCap（尤其是有第二层的）的材质。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
