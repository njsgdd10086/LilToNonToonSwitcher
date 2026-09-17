**LilToNonToon Switcher 1.1.8** —— 修边缘光：作者没启用时不再凭空多出一圈，形状也按 lilToon 的菲涅尔幂换算。

## 一、不该有的边缘光

lilToon 的 `_UseRim` 是这层效果的开关，作者关掉时（= 0）颜色值仍然留在材质里。
我们之前无条件照搬 `_RimColor` —— 于是在**作者根本没启用边缘光的材质上凭空多出一圈边缘光**，
头发上最明显（`_RimColor` 是 HDR 的 `(1.344, 1.344, 1.344)`，很亮）。

现在 `_UseRim = 0` 时把颜色写成黑色、范围推到 `(1, 1)`（等于关掉），日志会写明原因：

```
· _UseRim = 0（作者没启用边缘光）  ->  _jp_lilxyzw_nontoon_rimlight_RimLightColor = 黑色（关掉）
```

## 二、边缘光的形状：菲涅尔幂没换算

lilToon 的边缘光是这样算的：

```
f = 1 - dot(N, V)                      ← 菲涅尔
f = pow(f, _RimFresnelPower)           ← 幂次（头发 2.4、皮肤 3）
再用 _RimBorder / _RimBlur 卡阈值      ← 注意：卡在**幂次空间**
```

NonToon 是在**原始空间**做 smoothstep。我们之前直接把 `border ± blur/2` 填进范围，
等于用了错误的曲线 —— 边缘光过宽、贴不到轮廓上。

现在按数学关系换算回原始空间：

```
阈值位置 = border^(1/power)
宽度     = blur / (power · center^(power-1))      ← f^p 的导数
```

实测换算结果与**另一个转换插件**（同样是 NonToon，独立做的换算）几乎重合，说明这个推导是对的：

| 材质 | `_RimBorder` / `_RimBlur` / power | 现在 | 另一个插件 |
| --- | --- | --- | --- |
| `M_Hair` | 0.51 / 0.36 / 2.4 | **(0.644, 0.866)** | (0.60, 0.90) |
| `M_Body_Skin` | 0.739 / 0.661 / 3 | **(0.769, 1.000)** | (0.75, 0.98) |

## 三、其它

- 边缘光颜色现在会乘 `_RimMainStrength`（lilToon 里它作用在边缘光强度上，等价于缩放颜色）。
- 源材质没写 `_UseRim`（老材质）时仍按"启用"处理，行为不变。

## 升级后

ALCOM 更新到 1.1.8 → **重新转换**用了边缘光的材质（皮肤、衣服等）。
提醒：如果某个材质在 lilToon 里 `_UseRim = 0`，重转后那一圈光会消失 —— 这是对的。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
