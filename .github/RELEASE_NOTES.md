**LilToNonToon Switcher 1.1.3** —— 修掉 1.1.2 里描边后移的**误判触发**，并把「为什么没后移」写进转换日志。

## 修复：没用到宽度遮罩的材质被白白后移了描边

1.1.2 判断"源材质有没有用描边宽度贴图"时只看了这一行：

```csharp
if (source.GetTexture("_OutlineWidthMask") == null) return;
```

问题在于：Unity 对**没设置过**的贴图属性会返回 shader 里声明的**默认贴图**（lilToon 声明的是 `"white"`），
那**不是 null**。所以那些 .mat 里压根没写这个属性、根本没用宽度遮罩的材质，也会被当成"用了遮罩"而后移描边。

实测（liltonon 工程里 24 个 lilToon 材质）：

| 状态 | 数量 | 1.1.2 的行为 | 1.1.3 的行为 |
| --- | --- | --- | --- |
| `_OutlineWidthMask` 挂了真实 PNG | 15 | 后移 ✅ | 后移 ✅ |
| 槽位是空的（`m_Texture: {fileID: 0}`） | 7 | 不处理 ✅ | 不处理 ✅ |
| **连属性都没写进 .mat** | **2** | **误判后移 ❌** | 不处理 ✅ |

现在会排除所有内置默认贴图（`whiteTexture` / `blackTexture` / `grayTexture` / `normalTexture` 等），
**只有真的挂了 PNG 才算数**。

> 之前被误判的材质，重新转换一次就会恢复：转换会把 `_OutlineZOffset` 按源材质的 `_OutlineZBias`（通常是 0）重写一遍。

## 新增：日志说明每个材质的结果

凡是"源材质挂了宽度贴图"的材质，转换报告里都会写清结果与原因：

```
· 描边宽度贴图（NonToon 没有这个功能） -> 描边整体后移 0.0007（_OutlineZOffset，倍数 1 × 描边宽度）…
· 描边宽度贴图（NonToon 没有这个功能） -> 描边宽度为 0，本来就没有描边，未做后移
· 描边宽度贴图（NonToon 没有这个功能） -> 后移倍数 = 0，未做处理（描边可能盖住嘴唇 / 眼睛）
· 描边宽度贴图（NonToon 没有这个功能） -> 原有 _OutlineZOffset 0.001 已经不小于后移量，保持不动
```

没挂宽度贴图的材质**不会**打印这一行 —— 也就是 **日志里没有这一行 = 没用宽度遮罩 = 不需要处理**。

## 什么情况下 `_OutlineZOffset` 会保持 0

1. 源材质没挂宽度贴图（最常见，这种材质 lilToon / NonToon 的描边行为本来就一样）；
2. `_OutlineWidth` 是 0 / 负数（本来就没有描边）；
3. 后移倍数被设成 0（菜单 `描边后移倍数 > 0（不处理）`）；
4. 材质原有的 `_OutlineZOffset` 已经 ≥ 算出来的后移量（不覆盖更大的值）。

## 升级后

ALCOM 更新到 1.1.3 → 把之前被描边问题影响的材质重新转换一次即可。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
