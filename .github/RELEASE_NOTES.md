**LilToNonToon Switcher 1.1.4** —— 修掉「半透明轻纱转完变实心」和「描边粗细和 lilToon 不一致」，并新增**描边宽度倍数**。

## 一、修复：半透明的轻纱 / 薄片转完变成实心

lilToon 的**透明遮罩**（`_AlphaMaskMode`）是**直接改 alpha** 的：

```hlsl
alphaMask = saturate(_AlphaMask.r * _AlphaMaskScale + _AlphaMaskValue);
mode 1 替换 / mode 2 相乘 / mode 3 相加 / mode 4 相减
```

而 NonToon 没有这个功能（`_SharedMask` 只喂给各模块做范围遮罩，改不了 alpha），所以这条链现在会**按同样的顺序烘进 `_BaseTexture` 的 Alpha**（主色 → 透明遮罩 → …），遮罩按它自己的 tiling/offset 采样，并把导入设置钉成 `alphaSource = FromInput`。

顺手修掉两个会让烘焙**静默跳过**的情况：

| 情况 | 以前 | 现在 |
| --- | --- | --- |
| **遮罩贴图没挂**（`fileID: 0`） | 判定为"没用遮罩"，整段跳过 | 算"用了遮罩"：lilToon 采样没设置过的贴图属性用的是 shader 默认白贴图（= 1），所以 `_AlphaMaskValue` 就是「整体透明度偏移」 |
| **主贴图没挂**（`_MainTex = fileID: 0`） | 烘焙器第一句就静默返回 | 以**白底贴图**参与烘焙，把算好的 Alpha 带出来 |

实测：

```
smooth white  planet_nontoon_Base.png   alpha 均值 = 0.6706   ← saturate(1×1 + (−0.33)) = 0.67 ✓
smooth white  ring_nontoon_Base.png     alpha 均值 = 0.6764（遮罩逐像素 × 0.73，黑区 alpha=0 与 lilToon 一致）✓
```

遮罩贴图读不出来（没勾 Read/Write）时会**警告**并按默认白遮罩（= 1）计算，而不是整段丢掉。

## 二、修复：描边粗细和 lilToon 不一致

两个 shader 的描边偏移**不在同一个空间**：

```
lilToon ：positionOS += outlineN * (_OutlineWidth * 0.01 * 宽度贴图)   → 过物体矩阵，会被对象缩放缩放
NonToon ：vertex.position（已是世界空间）+= outlineN * _OutlineWidth * 0.01 → 不受对象缩放影响
```

所以对象一旦被缩放，NonToon 的描边就会等比例偏粗/偏细。现在转换时按「使用该材质的渲染器」的**世界缩放**折算 `_OutlineWidth`（缩放 ≈ 1 时等于不改），同一材质被不同缩放共用时会警告并取平均，日志里会写明：

```
· 描边宽度  ->  0.07 → 0.021（对象缩放 0.3 × 手动倍数 1）
```

## 三、新增：描边宽度倍数（手动系数）

- 菜单 `Tools > LilToNonToon Switcher > 描边宽度倍数 >` → `0.25 / 0.5 / 0.75 / 1（默认） / 1.5`
- 设置窗口「高级设置」→ 滑条 0–2
- 最终公式：`描边宽度 = 原值 × 对象世界缩放 × 这个倍数`（改完重新转换后生效）

## 四、已知限制

lilToon 的 `_OutlineFixWidth` 会在**相机距离小于 1 米**时把描边按 `× saturate(距离)` 收窄（贴脸看最多细 20~30%）；NonToon 的描边没有随距离变化的机制，所以**极度贴近看**时 NonToon 仍会略粗一点。正常距离两者一致；需要的话用「描边宽度倍数」按材质补。

## 升级后

1. ALCOM 更新到 1.1.4；
2. 把轻纱 / 薄片 / 描边相关的材质**重新转换一次**（会覆盖同名的 `_nontoon.mat`，原 lilToon 材质不动）。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
