# PptInk 插件工作室 · 使用说明

本说明面向两类人：**想快速做出一个插件的老师/使用者**，与**要改代码的开发者**。
读完并照着做，你可以在几分钟内产出一个能装进 PptInk、可运行的插件。

---

## 1. 这是什么

PptInk 支持第三方插件：一个插件就是一个 **zip 包**，里面有 `manifest.json` 与一段 Lua 脚本，
由 PptInk 在沙箱里运行。**插件工作室**是专门用来"写这个 zip"的图形工具：

- 用表单填插件信息（不用手写 JSON）；
- 用表格设计面板元素（文本 / 滑条 / 按钮）；
- 用带语法高亮与补全的编辑器写 Lua；
- 按 PptInk 的规则自动校验，避免"装得进跑不起来"；
- 一键打包成 zip，或一键安装到 PptInk 插件目录。

> 插件脚本是**逻辑**，PptInk 负责渲染与安全；你不需要懂 WPF 或 COM。

---

## 2. 安装与启动

前置：Windows + .NET 8 SDK（开发用）。

```bash
# 构建界面程序
dotnet build tools/PptInk.PluginStudio/PptInk.PluginStudio.csproj -c Release
# 运行
bin/Release/net8.0-windows/PptInk.PluginStudio.exe
```

只想验证核心逻辑（任意平台）：`dotnet test tools/PptInk.PluginStudio.Tests`。

---

## 3. 界面总览

```
┌─────────────────────────────────────────────────────────────┐
│ 新建(向导) 打开工程 保存工程 导入插件包 示例库 校验 刷新预览    │
│ 打包zip 一键安装 生成说明 使用说明 操作指引                    │
├───────────────┬─────────────────────────────────────────────┤
│ 基本信息      │  脚本（Lua 编辑器：高亮 / 行号 / 补全）        │
│ 面板设计      │                                               │
│ 权限          │                                               │
├───────────────┼─────────────────────────────────────────────┤
│               │  校验结果 | 预览                              │
└───────────────┴─────────────────────────────────────────────┘
```

- **左栏**：插件配置（类型/Id/名称/面板/权限）。
- **中间**：Lua 代码编辑器。
- **右下**：校验结果（错误/警告，双击定位）与面板预览。

---

## 4. 快速上手（六步）

1. 点 **新建（向导）** → 选类型（面板 / 绘制 / 自由）→ 选模板 → 填基本信息。
2. 左栏「基本信息」核对 **Id**（全局唯一）、名称、版本、描述。
3. 面板类插件在「面板设计」增删元素，并让每个元素的 **OnEvent** 与脚本里的函数同名。
4. 右侧写 Lua 逻辑；输入字母或 **Ctrl+空格** 会弹出注入 API 补全。
5. 点 **校验**：红色错误必须清零（双击可跳到对应行/字段）。
6. 点 **打包 zip** 得到分发包，或 **一键安装** 到 `%LocalAppData%\PptInk\plugins\<Id>\`。

> 想先看效果？点 **示例库**，直接克隆"正倒计时 / 画板批注 / 随机点名"其中之一。

---

## 5. 三类插件

| 类型 | magic | 入口回调 | 适合做什么 |
| --- | --- | --- | --- |
| 面板类 Panel | 1 | `onActivate` | 计时器、计分板、投票——宿主渲染面板，你写逻辑 |
| 绘制类 Draw | 2 | `onStart` | 画图、标注、模板——调用绘制 SDK 落笔迹 |
| 自由类 Free | 3 | `onStart` | 截图、点名、快捷动作——自行接管交互 |

停止回调统一为 `onDeactivate`。**入口回调里的代码就是插件被启用时执行的入口。**

### 面板元素（仅面板类）

| 字段 | 说明 |
| --- | --- |
| `类型` | `text` 文本 / `slider` 滑条 / `button` 按钮 |
| `Id` | 文本元素建议填；脚本用 `updatePanelElement("Id", 值)` 更新它 |
| `文本` | 初始显示文本 |
| `Min` / `Max` / `值` | 仅滑条：范围与默认值，要求 `Min < Max` |
| `OnEvent` | 脚本中的函数名：按钮点击 / 滑条拖动时回调（滑条会传入当前值） |

---

## 6. 权限

在「权限」页勾选；首次在 PptInk 里启用含权限插件时会弹窗确认。

| 权限 | 含义 |
| --- | --- |
| `file` | 允许把截图写入插件专属目录（沙箱内，不能写到任意路径） |
| `clipboard` | 允许读写剪贴板 |
| `network` | 当前版本未注入网络能力，声明不生效 |

> 未声明的 API 在脚本里调用会被**校验直接拦下**（错误级）。

---

## 7. 注入 API 参考（沙箱内可用）

| 函数 | 签名 | 权限 |
| --- | --- | --- |
| `setPen` | `setPen(colorHex, thicknessMm, alpha)` | — |
| `drawPolyline` | `drawPolyline({ {x,y}, ... })` | — |
| `drawText` | `drawText(text, x, y, sizeMm)` | — |
| `eraseBehavior` | `eraseBehavior(mode)`（stroke/pixel/region/clear） | — |
| `startTimer` | `startTimer(intervalSeconds, callbackName)` | — |
| `stopTimers` | `stopTimers()` | — |
| `showToast` | `showToast(message)` | — |
| `closePanel` | `closePanel()` | — |
| `updatePanelElement` | `updatePanelElement(elementId, value)` | — |
| `readClipboard` | `readClipboard()` | clipboard |
| `writeClipboard` | `writeClipboard(text)` | clipboard |
| `captureScreen` | `captureScreen(savePath)` | file |

沙箱是**硬沙箱**：没有 `io`、`os`、`require` 等；每次回调有指令/时间预算（约 160ms），
超时会自动停用该插件并提示，避免拖动课堂。

---

## 8. 校验规则与常见报错

工具与 PptInk 主程序使用同一套规则，打包/安装前若存在**错误**会被拒绝。

| 位置 | 规则 |
| --- | --- |
| Id | 字母或数字开头，仅含字母/数字/`.`/`_`/`-`，≤100 |
| 名称 | 非空，≤120 |
| 入口 | 非空、非绝对路径、不含 `..`、≤240，且文件存在 |
| 版本/作者/描述 | ≤64 / ≤200 / ≤1000 |
| 元素 | ≤64 个；类型仅 text/slider/button；Id ≤80；文本 ≤500 |
| OnEvent | 按钮/滑条必填且为合法函数名；文本可空缺省 `noop` |
| 滑条 | min<max，范围建议 ±100000 |
| 面板尺寸/透明度 | 宽 160–600、高 100–500、α 0.3–1.0（越界为警告，打包时收敛） |

**常见报错对照**：

- *"入口脚本不存在"* → 检查「基本信息」里的入口文件名与脚本文件是否一致。
- *"按钮/滑条的 OnEvent 必须是合法的 Lua 函数名"* → OnEvent 只能字母/下划线开头。
- *"面板元素引用了未定义的脚本函数：xxx"* → 脚本里没有 `function xxx() ... end`。
- *"脚本调用了 xxx，但未声明其所需权限"* → 到「权限」页勾选对应权限。
- *Lua 语法错误（第 N 行）* → 双击该条，光标会跳到对应行。

---

## 9. 打包与安装

- **打包 zip**：生成 `<Id>-<版本>.zip`，可发给别人。对方在 PptInk 里「安装插件包」即可。
- **一键安装**：直接把当前插件写入 `%LocalAppData%\PptInk\plugins\<Id>\`。
  若已存在同 Id，工具会**备份旧目录 → 替换 → 失败自动回滚**，不会破坏已装版本。
- 安装后在 PptInk 设置中心「插件」页启用，或点工具栏「插件」按钮使用。

---

## 10. 导入已有插件 / 工程

- **导入插件包**：选择一个 zip → 反填到工作台，可用于修改别人的插件。
- **保存工程 / 打开工程**：把当前工作保存为 `project.ppstudio`（含清单、脚本与资源），下次继续。

---

## 11. 快捷键

| 操作 | 说明 |
| --- | --- |
| 输入字母 / `Ctrl+空格` | 触发注入 API 与回调名补全 |
| `Tab` / `Enter` | 在补全弹窗中确认选中项 |
| `↑` `↓` | 在补全列表中移动 |
| `Esc` | 关闭补全弹窗 |

---

## 12. 常见问题

- **面板不显示？** 确认是"面板类"，且 `manifest.panel` 至少有一个元素；面板由 PptInk 渲染，工作室里只做近似预览。
- **按钮点了没反应？** 按钮的 `OnEvent` 必须与脚本函数同名，且该函数在脚本里被定义。
- **滑条拖动没变化？** 回调函数要读取传入参数并 `updatePanelElement` 更新显示。
- **打包报"校验未通过"？** 看「校验结果」里的**错误**行，双击定位后修复；警告不阻断。
- **脚本里能用 `os.time`、`require` 吗？** 不能，硬沙箱未提供；需要时间可用 `startTimer` 计数。

---

## 13. 给开发者：与主程序保持一致

- `Core/Validation/ManifestValidator.cs`、`Core/Models/PluginManifest.cs` 的规则
  对齐主程序 `src/PptInk.App/Plugins/PluginManager.cs::ValidateManifest / NormalizeManifest`。
- `Core/Lua/LuaApiCatalog.cs` 对齐主程序 `src/PptInk.App/Plugins/ScriptPlugin.cs` 的注入清单。
- **主程序规则/API 变更时，须同步更新以上文件并跑 `dotnet test`**（测试即规则表）。

---

## 14. 已知限制

- 面板预览为**简化示意**，不追求与 PptInk 内渲染逐像素一致。
- WPF 界面只能在 Windows 构建；`Core` 与测试跨平台。
- 插件当前不验签（主程序亦同），只安装可信来源的包。