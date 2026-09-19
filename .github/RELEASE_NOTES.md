**LilToNonToon Switcher 1.1.15 —— 织物模块拆分为独立的 NonToon Modules 包**

## [1.1.15] - 2026-09-17

这一版做了**架构调整**：把 Shader Core 模块从插件里拆出去，做成独立的模块包。功能不变，但模块可以单独安装使用了。

### 变更

- **织物/法线细节模块搬到新包 `com.nontoon.modules`**（仓库 NonToon Modules）✓ ——
  本插件现在通过 VPM 依赖**订阅**它（`vpmDependencies`）✓，插件里不再包含任何 Shader Core 模块文件 ✓；
- 转换时会调用模块包的 API **自动勾选**织物模块（幂等 ✓，没登记就登记 + 重新生成 NonToon shader ✓）；
- 调用的方式是**反射** ✓ —— 模块包万一没装，本插件仍能正常编译 ✓，只在转换日志里给一条明确提示 ✓；
- 菜单新增 `Tools/LilToNonToon Switcher/打开 NonToon 模块管理` ✓（跳到模块包的勾选界面 ✓）。

### 说明

- 升级后请让 VCC/ALCOM 一并安装 `com.nontoon.modules`（依赖会自动带上 ✓）；
- **如果之前手动把本插件的 `Shaders` 目录拷进过工程**，请删掉那份旧副本 ✗ ——
  同一个模块 id 出现两次会被 Shader Core 重复编入 shader 导致编译错误；
  模块包的勾选界面发现重复时也会在 Console 里提示该删哪一份 ✓。

### 模块本身没变

织物模块的算法与默认值同 1.1.14（强度 0.25 ✓、方向 0.35 / -0.5 ✓、软压缩 ✓），只是换了家。
