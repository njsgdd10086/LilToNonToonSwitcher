**LilToNonToon Switcher 1.1.6** —— 修掉「1.1.5 的阴影渐变修复其实没生效」，脸上阴影现在才真的按 lilToon 的公式烘。

## 一、为什么 1.1.5 没起作用

写 `.scgradients` 资产的那一步，在最后把 Gradient **又压回了 4 个等距点**：

```csharp
key0 = gradient.Evaluate(0f);        // 不管 Gradient 里有多少关键点
key1 = gradient.Evaluate(1f / 3f);   // 都只在这四个位置取值写出去
key2 = gradient.Evaluate(2f / 3f);
key3 = gradient.Evaluate(1f);
```

阴影过渡窗口（例如宽 **0.189**）在 0~1 里只占一小段，4 个等距点几乎全落在窗口外 ——
于是写出来的渐变是错的。实测你工程里那份**整条都是白色**，等于 NonToon 上完全没有阴影压暗。

现在**把 Gradient 的真实关键点原样写出去**（Unity 的 Gradient 最多 8 个关键点）。

## 二、关键点改用「过渡窗口边界」

lilToon 的阴影在窗口内是**分段线性**的，所以取 `0 / 1` 加上每层阴影的 `border ± blur/2`
（1 层 4 个、2 层 6 个、3 层 8 个）就与 lilToon **完全等价** —— 比密集采样更准。

以某张脸（border 0.117 / blur 0.189 / 第二层 0.214 / 0.073）为例，修复后写出的关键点是：

```
x=0.0000  rgb=(0.991, 0.908, 0.888)   ← 最深（第二层阴影色，不再是黑）
x=0.0225  rgb=(0.991, 0.908, 0.888)
x=0.1775  rgb=(0.998, 0.984, 0.980)
x=0.2115  rgb=(1.000, 1.000, 1.000)   ← 过渡窗口 [0.023, 0.212]，与 lilToon 一致
x=1.0000  rgb=(1.000, 1.000, 1.000)
```

## 三、补上 `_ShadowStrength`

lilToon 里它是 `lns.x = lerp(1.0, lns.x, _ShadowStrength)` —— 把受光系数往 1 拉，
也就是「阴影只有几成」。之前完全没用上，阴影会偏重；现在按同样比例收着
（例如强度 0.2 的脸，阴影就只有两成，很淡）。

## 四、其它

- 源材质用「阴影色贴图 / LUT」模式（`_ShadowColorType != 0`）时会给出警告：NonToon 只能按单一阴影色近似。

## 升级后

**重新转换**材质才会生效（渐变是烘焙资产）。ALCOM 更新到 1.1.6 → 重转脸部等材质。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
