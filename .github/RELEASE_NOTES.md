**lilToNonToon Switcher 1.0.0** —— 选中对象右键，把 lilToon 材质一键转换成 NonToon，并自动生成一键切换 shader 的开关。

## 安装

### VCC / ALCOM（推荐）

1. 打开 VCC（或 ALCOM）→ **Settings → Packages → Add Repository**
2. 填入仓库地址：

   ```
   https://njsgdd10086.github.io/LilToNonToonSwitcher/index.json
   ```

3. 回到项目的 **Manage Project**，在 `LilToNonToon Switcher` 上点 **Add**。

### unitypackage

下载下方附件或 `com.nontoon.switcher-1.0.0.zip`，解压后把 `Editor/` 与 `package.json` 放进
`Assets/LilToNonToonSwitcher/`；也可以直接用 unitypackage 导入。

## 依赖

| 包 | 用途 |
| --- | --- |
| `jp.lilxyzw.liltoon` | 转换来源 shader |
| `jp.lilxyzw.nontoon` + `jp.lilxyzw.shadercore` | 转换目标 shader |
| `nadena.dev.modular-avatar` + VRChat SDK3 Avatars | 生成切换开关（可选） |

## 用法

1. 在 Hierarchy 里选中 avatar 本体 / 衣服等对象
2. 右键 → `LilToNonToon` → **将选中对象转换为 NonToon**（或 `Ctrl+Shift+T`）
3. 材质输出为 `<原材质名>_nontoon.mat`，同时生成 `_NonToonSwitch`
   （MA Material Setter + 菜单开关，默认显示 NonToon，勾选后回到 lilToon）

完整说明见 [README](https://github.com/njsgdd10086/LilToNonToonSwitcher#readme)。

## 本次更新内容

- 右键一键转换 lilToon → NonToon，输出命名 `<名称>_nontoon.mat`
- 自动生成 Modular Avatar **MA Material Setter** + 菜单开关（可选 Material Swap 模式）
- 识别 lilToon 全部 65 个 shader，包括 `Hidden/lilToonOutline` 等隐藏变体
- 转换内容：基础贴图烘焙（`_MainTex × _Color`）、共享遮罩 `_SharedMask`、
  阴影渐变 `.scgradients`、渲染模式、描边、Stencil 等渲染状态
- 未支持的功能（Emission / Glitter / AudioLink / Dissolve 等）以警告写入转换日志，不会静默丢弃
- 完整中文界面与文档
