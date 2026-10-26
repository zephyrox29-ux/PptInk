# PptInk 插件工作室（PptInk.PluginStudio）

面向 PptInk Lua 插件的图形化创作工具：**向导 + 工作台**双模式，可视化设计面板、
内置 Lua 编辑器（语法高亮 / 补全 / 语法检查）、按主程序规则校验、一键打包 zip 与一键安装。

> 📖 **完整操作手册见 [USAGE.md](USAGE.md)**（也可在程序内点工具栏「使用说明」打开）。
>
> 本工具是独立桌面程序，**不修改、也不依赖** PptInk 主程序运行。

## 工程结构

```
tools/PptInk.PluginStudio.Core/   跨平台纯逻辑库（net8.0）：模型 / 校验 / 打包 / 安装 / 导入 / 模板
tools/PptInk.PluginStudio/        WPF 界面（net8.0-windows）：向导、工作台、编辑器、预览
tools/PptInk.PluginStudio.Tests/  xUnit：Core 的纯逻辑测试（Linux / Windows 均可运行）
```

> 逻辑与界面分层，是为了让核心逻辑能在**任意平台**用 `dotnet test` 验证；WPF 界面只能在 Windows 构建。

## 构建

前置：.NET 8 SDK。

```bash
# 核心逻辑 + 测试（跨平台）
dotnet build tools/PptInk.PluginStudio.Core/PptInk.PluginStudio.Core.csproj
dotnet test  tools/PptInk.PluginStudio.Tests/PptInk.PluginStudio.Tests.csproj

# WPF 界面（需 Windows）
dotnet build tools/PptInk.PluginStudio/PptInk.PluginStudio.csproj
```

## 快速上手（详见 USAGE.md）

1. 启动后点「新建（向导）」：选择类型（面板 / 绘制 / 自由）→ 选模板 → 填基本信息。
2. 工作台左栏配置：`基本信息`（Id/名称/版本/作者/描述/入口）、`面板设计`（增删 text/slider/button）、`权限`（file / clipboard）。
3. 右侧编辑器写 Lua 逻辑；输入字母或 `Ctrl+空格` 弹出注入 API 补全。
4. 点「校验」：错误/警告列在下方，双击 Lua 错误可定位到行。
5. 校验通过后「打包 zip」得到可分发插件包，或「一键安装」到 `%LocalAppData%\PptInk\plugins\<Id>\`。
6. 「生成说明」导出一份可分享的 Markdown；「使用说明」打开完整手册。

## 三类插件与入口回调

| 类型 | magic | 入口回调 | 说明 |
| --- | --- | --- | --- |
| 面板类 Panel | 1 | `onActivate` | 宿主渲染面板元素，脚本写逻辑 |
| 绘制类 Draw | 2 | `onStart` | 调用绘制 SDK 落矢量墨迹 |
| 自由类 Free | 3 | `onStart` | 自行接管交互 |

停止回调统一为 `onDeactivate`。

## 注入 API 与权限

通用：`setPen` `drawPolyline` `drawText` `eraseBehavior` `startTimer` `stopTimers` `showToast` `closePanel` `updatePanelElement`。
`clipboard` 权限追加：`readClipboard` `writeClipboard`。`file` 权限追加：`captureScreen`。
`network` 当前不生效。

## 与主程序一致性的维护

`Core/Validation/ManifestValidator.cs` 与 `Core/Models/PluginManifest.cs` 的规则，
对齐主程序 `src/PptInk.App/Plugins/PluginManager.cs::ValidateManifest / NormalizeManifest`；
`Core/Lua/LuaApiCatalog.cs` 对齐 `src/PptInk.App/Plugins/ScriptPlugin.cs` 的注入清单。
**主程序规则变更时须同步更新，并跑一遍测试。**

## 安全

- 打包/安装前校验入口与资源路径，拒绝 `..` 越界与绝对路径（与主程序 `InstallFromZip` 同口径）。
- Lua 语法检查仅 `LoadString` **解析**，不执行用户脚本。
- 安装含"备份 → 替换 → 失败回滚"，不破坏已装版本。

## 依赖

- Core：MoonSharp 2.0.0（与主程序同版本，仅用于 Lua 解析）。
- 界面：AvalonEdit（编辑器）。