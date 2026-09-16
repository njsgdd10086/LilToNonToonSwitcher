**LilToNonToon Switcher 1.1.5** —— 修掉「脸上出现硬边 + 阶梯状明暗分界」（脸颊一块灰蓝、分界线像台阶）。

## 原因

烘 lilToon 阴影色时，渐变的关键点**位置和软硬都不对**：

| | 旧做法 | lilToon 的真实行为 |
| --- | --- | --- |
| 分界位置 | 关键点放在 `1 − border`（**镜像了**） | 在 `x = saturate(dotNL × 0.5 + 0.5)` 空间里，过渡窗口是 `[border − blur/2, border + blur/2]` |
| 软硬 | **完全没用 `_ShadowBlur`** | 过渡宽度由 blur 决定 |
| 第三层阴影 | `_Shadow3rdColor` 的 alpha = 0（作者没启用）也被当成黑色关键点 | `lerp(indirect, third, a3 × (1 − s3))`，a3 = 0 时不生效 |
| 阴影强度 | alpha 被忽略 | 每层阴影色的 **alpha 就是强度** |

两个后果叠在一起：分界线跑到了「接近受光」的位置，过渡段又只有 `blur` 那么窄（例如 0.117），
而 NonToon 采样的是低分辨率渐变贴图 —— 于是就出现了**硬边 + 阶梯**。

## 现在

按 lilToon 的公式（`lil_common_frag.hlsl` 的 `lilTooningScale` + 阴影色叠加顺序），在同一个 `x` 空间里
**采样 24 个关键点**烘成 Shade 渐变：

- 过渡窗口 `[border ± blur/2]`（和 lilToon 一模一样）；
- 每层阴影色的 **alpha 当强度**；
- `alpha ≈ 0` 的层直接跳过（不再把「没启用的第三层」当成黑色用）；
- 受光端回到白色（受光处的 `albedo × 光` 由 NonToon 自己算）。

转换日志会写明实际用的层数与窗口：

```
· 阴影色 2 层（border 0.117 / blur 0.189，按 lilToon 的过渡窗口采样）  ->  _SharedGradients（Shade 渐变）
```

## 升级后

1. ALCOM 更新到 1.1.5；
2. **重新转换**脸部（以及任何用 lilToon 阴影色的）材质 —— 渐变是烘焙出来的资产，不重转不会变。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
