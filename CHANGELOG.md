# 更新日志

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

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
