# PptInk 插件工作室（PptInk.PluginStudio）设计文档

- 日期：2026-10-25
- 状态：已确认（用户批准，含"全功能工作室"范围）
- 关联：PptInk 插件系统 `src/PptInk.App/Plugins/`（manifest + MoonSharp Lua 沙箱）

## 1. 背景与目标

PptInk 支持第三方 Lua 插件：插件包是一个 zip，内含 `manifest.json` 与入口脚本，
由主程序 `PluginManager.InstallFromZip` 安装到 `%LocalAppData%\PptInk\plugins\<Id>\`，
运行时在 MoonSharp `Preset_HardSandbox` 沙箱中执行（见 `ScriptPlugin.cs`）。

目前作者要手工编写 `manifest.json` 与 Lua 脚本、手工打 zip，容易出现：
字段拼写/取值非法、`Entry` 指向缺失文件、面板元素声明不合规（`OnEvent` 非标识符、
元素数超限、尺寸越界）、脚本调用未注入或未授权的 API、包结构与主程序期望不一致等，
导致"装得进、跑不起来"或直接安装失败。

**目标**：提供一个独立的 Windows 图形化工具（`PptInk.PluginStudio`），
让作者用表单 + 可视化设计器 + 代码编辑器完成插件创作，内置与主程序一致的校验，
一键打包 zip 并可直接安装到主程序插件目录，降低插件生态的入门与出错成本。

## 2. 非目标

- 不修改 PptInk 主程序任何代码（主程序工程与行为保持不变）。
- 不实现插件签名/验签（主程序当前也不验签）。
- 不执行用户脚本逻辑（仅做语法解析校验，不运行，避免安全与副作用）。
- 不做插件商店/在线分发（只产出可分享的 zip 与说明）。

## 3. 术语

- 插件（Plugin）：一个 zip 包，含 `manifest.json` + 入口脚本（+ 可选资源）。
- 三类：`Panel`(magic=1) 宿主渲染面板；`Draw`(magic=2) 调用绘制 SDK；`Free`(magic=3) 自由接管。
- 工程（Project）：工具内的编辑单元，含 manifest 数据、Lua 源码与资源，可存为 `.ppstudio`。
- 注入 API：主程序注入沙箱的全局函数集合（见 §7）。

## 4. 工程结构

```
tools/PptInk.PluginStudio.Core/       # 跨平台纯逻辑库（net8.0，无 WPF 依赖）—— 可在此 Linux 环境编译与测试
  PptInk.PluginStudio.Core.csproj      # 引用 MoonSharp 2.0.0（纯托管，跨平台）
  Models/
    PluginKind.cs
    PluginManifest.cs
    PanelDecl.cs
    PanelElement.cs
    ProjectModel.cs                    # manifest + script + resources
  Validation/
    ManifestValidator.cs               # 对齐主程序 ValidateManifest/NormalizeManifest
    ValidationIssue.cs                 # 级别(错误/警告) + 字段定位 + 文案
    LuaSyntaxChecker.cs                # MoonSharp LoadString 仅解析，返回行列错误
    CrossConsistencyChecker.cs         # API 权限/回调定义/入口存在性
  Lua/
    LuaApiCatalog.cs                   # 注入 API 签名与面板回调名清单
  Packaging/
    PackageBuilder.cs                  # 打包 zip
    PackageInstaller.cs                # 安装到插件目录（备份/替换/回滚；根目录可注入以便测试）
    PackageImporter.cs                 # 导入 zip/目录 → 工程
    PackageLayout.cs                   # zip 内结构常量与命名
  Templates/
    TemplateLibrary.cs                 # 三类模板 + 示例库（内置示例源码）
    ReadmeGenerator.cs                 # 生成安装/分享说明
  AppPaths.cs                          # 路径解析（可通过环境变量覆盖根目录，便于跨平台测试）
  PluginStudioLog.cs                   # 轻量日志接口（默认写文件，可注入 NullLogger）

tools/PptInk.PluginStudio/             # WPF 界面（net8.0-windows, UseWPF）—— 需 Windows 构建
  PptInk.PluginStudio.csproj           # 引用 Core + AvalonEdit
  App.xaml / App.xaml.cs
  Views/
    WizardWindow.xaml                  # 新建向导
    WorkbenchWindow.xaml               # 主工作台
    Sections/BasicInfoView.xaml        # 基本信息
    Sections/PanelDesignerView.xaml    # 面板设计器
    Sections/PermissionsView.xaml      # 权限
    PreviewPane.xaml                   # 简化预览
    ValidationPane.xaml                # 校验结果清单
    HelpOverlay.xaml                   # 操作指引（可逐页高亮关键控件）
  ViewModels/
    WizardViewModel.cs
    WorkbenchViewModel.cs
    BasicInfoViewModel.cs
    PanelDesignerViewModel.cs
    ValidationViewModel.cs
  Services/
    AvalonLuaEditorHost.cs             # AvalonEdit 宿主（高亮/补全/跳转定位）
    FileDialogs.cs
    PreviewRenderer.cs                 # 简化预览渲染（按 manifest 画近似面板）
  Assets/                              # 图标、模板资源

tools/PptInk.PluginStudio.Tests/       # xUnit：只引用 Core → 可在 Linux/Windows 均编译运行
PptInk.sln                             # 加入上述三个项目
```

> 拆分理由：WPF（`net8.0-windows`）只能在 Windows 上构建；把模型、校验、打包、导入、
> 模板等**纯逻辑放进跨平台 Core 库**，可在开发机上（含本次 Linux 环境）用 `dotnet build`/
> `dotnet test` 真实验证，界面层留待 Windows 构建。这也是能在此环境"完成验证"的关键。

约定：`Models` 与 `ManifestValidator` 自包含一份（不引用主程序程序集），
文件头注明"同步来源：`src/PptInk.App/Plugins/PluginManager.cs::ValidateManifest / NormalizeManifest`，
主程序规则变更需同步"，并在测试中固定规则表以防漂移。

## 5. 数据模型

与主程序 `PluginContract.cs` 对齐：

- `PluginKind`：Panel=1 / Draw=2 / Free=3。
- `PluginManifest`：`Magic, Id, Name, Version, Author, Description, Entry, Panel?, Permissions[], Signature?`。
- `PanelDecl`：`Dock(float/left/right/bottom), Width, Height, Alpha, ShowInIsland, Elements[]`。
- `PanelElement`：`Type(text/slider/button), Id?, Text?, Min?, Max?, Value?, OnEvent`。
- `ProjectModel`：`PluginManifest Manifest` + `string LuaSource` + `List<ResourceFile> Resources` + `string ProjectPath`。

## 6. 功能需求

### 6.1 双模式

- FR-1 新建走向导：选类型（三类）→ 选模板 → 填基本信息 → 生成面板/脚本骨架 → 进入工作台。
- FR-2 工作台：左栏「基本信息 / 面板设计 / 权限」分页；中间 Lua 编辑器；底部「校验 + 简化预览」；顶部工具栏。
- FR-3 顶部工具栏动作：新建、打开工程(.ppstudio)、导入 zip/目录、示例库、校验、预览开关、打包、安装；并提供「操作指引」。

### 6.2 表单与面板设计器

- FR-4 基本信息表单：Id（实时正则提示）、名称、版本、作者、描述、类型（三选一）、入口文件名。
- FR-5 权限：file / clipboard 勾选（含各自能力说明）；network 标注"当前不生效"。
- FR-6 面板设计器（仅 Panel 类）：Dock、Width、Height、Alpha 控件（带合法区间提示）；
  元素表可增删改 text/slider/button，编辑 `id/text/min/max/value/onEvent`；
  滑条 min<max 校验；按钮/滑条 `onEvent` 必须为 Lua 标识符。
- FR-7 非 Panel 类隐藏面板设计器，改显示对应"入口约定"提示（onStart / onDeactivate）。

### 6.3 编辑器与校验

- FR-8 AvalonEdit：Lua 语法高亮、行号、括号匹配、缩进。
- FR-9 自动补全：数据源为 `LuaApiCatalog`（§7 的注入全局函数 + 当前面板 `onEvent` 回调名 + Lua 关键字）。
- FR-10 Lua 语法检查：用 MoonSharp `Script.LoadString` **仅解析**，捕获 `SyntaxErrorException` 并定位行列。
- FR-11 Manifest 校验：`ManifestValidator` 完全对齐主程序规则（§8）。
- FR-12 交叉一致性：脚本中调用的注入 API 是否与 `Permissions` 匹配（clipboard/file）；
  面板元素引用的 `onEvent` 是否在脚本中出现 `function onEvent` 定义；`Entry` 是否存在于工程资源。
- FR-13 校验结果以清单呈现，可点击定位到对应控件或编辑器行列；问题不阻断编辑，但阻断打包与安装。

### 6.4 打包 / 安装 / 导入 / 示例 / 说明

- FR-14 打包：输出 `<Id>-<Version>.zip`，结构 = `manifest.json` + 入口脚本 + 资源；文件名同路径安全。
- FR-15 一键安装：校验通过后复制到 `%LocalAppData%\PptInk\plugins\<Id>\`；
  若目标已存在则"备份 → 替换 → 失败回滚"（语义对齐主程序 `InstallFromZip`）。
- FR-16 导入反向编辑：打开 zip 或插件目录 → 解析 manifest + 入口脚本 → 反填工程；畸形包给出可读原因。
- FR-17 示例库：内置示例可一键克隆为当前工程；
  首批：`面板类·正倒计时`、`绘制类·画板批注`、`自由类·随机点名`。
- FR-18 说明生成：生成可分享的安装说明（含安装步骤、权限含义、注入 API 参考）；可写入包内 `README.md` 或单独导出。

### 6.5 工程文件

- FR-19 保存/打开 `.ppstudio`（JSON：manifest + 脚本相对路径 + 资源清单）；工程目录布局为
  `<工程名>/project.ppstudio` + `main.lua` + `resources/`。

## 7. 注入 API 清单（补全与交叉校验的数据源）

通用：`setPen(color, thicknessMm, alpha)`、`drawPolyline({{x,y},...})`、`drawText(text,x,y,sizeMm)`、
`eraseBehavior(mode)`、`startTimer(sec, callback)`、`stopTimers()`、`showToast(msg)`、
`closePanel()`、`updatePanelElement(id, value)`。
`clipboard` 权限追加：`readClipboard()`、`writeClipboard(text)`。
`file` 权限追加：`captureScreen(savePath)`。

入口约定：Panel 类调用 `onActivate`；Draw/Free 类调用 `onStart`；停止时调用 `onDeactivate`。

## 8. 校验规则表（对齐主程序）

| 规则 | 约束（与 `PluginManager` 一致） |
|---|---|
| magic | 1/2/3 |
| id | 正则 `^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$` |
| name | 非空，≤120，落库前 Trim |
| author/description/version | 长度 ≤200 / ≤1000 / ≤64 |
| entry | 非空、非绝对路径、≤240；解析后须在包内且存在；脚本 ≤256KB |
| permissions | 仅允许 file/network/clipboard；去重小写 |
| panel.dock | left/right/bottom/float |
| panel.width/height/alpha | clamp：160–600 / 100–500 / 0.3–1.0 |
| panel.elements | ≤64；type 仅 text/slider/button |
| element.id/text | ≤80 / ≤500 |
| element.onEvent | 标识符 `^[A-Za-z_][A-Za-z0-9_]{0,63}$`；button/slider 必填，text 可空缺省 noop |
| slider.min/max | clamp ±100000，max>min，value 落在区间 |

## 9. 数据流

新建 / 导入 / 示例 → `ProjectModel`（内存）→ 表单 / 设计器 / 编辑器修改 →
实时校验（Manifest + Lua + 交叉）→ 通过后 打包 zip 或 安装。任一步失败给出可读原因与定位。

## 10. 错误处理

- 校验问题清单化，分级（错误/警告）；错误阻断打包/安装，警告提示但不阻断。
- 安装失败回滚备份目录；导入畸形包给出具体原因（缺 manifest / 越界路径 / 反序列化失败等）。
- 所有 IO 异常给可读提示并写入工具日志 `%LocalAppData%\PptInk\PluginStudio\logs`。

## 11. UI 友好与操作指引（用户强调）

- 首启显示"快速上手"覆盖层：分步高亮关键控件（新建 → 选模板 → 填表 → 写脚本 → 校验 → 打包/安装）。
- 每个字段就近提供"这是什么/取值范围"提示（ToolTip + 行内说明），错误即时反馈在字段旁。
- 向导用大按钮与图解式步骤条，非专业用户也能走完；工作台为进阶用户保留效率路径。
- 手册/API 参考在工具内可打开（Help 面板），并生成可分享的 Markdown 安装说明。

## 12. 测试策略

`tools/PptInk.PluginStudio.Tests`（xUnit，纯逻辑，不依赖 UI）：

- `ManifestValidatorTests`：§8 规则表逐条正/反用例（含边界 clamp 与正则）。
- `PackageBuilderTests`：产物 zip 结构、文件名、manifest 往返一致性。
- `PackageInstallerTests`：安装、覆盖（备份/回滚）、失败还原。
- `PackageImporterTests`：zip/目录往返（build→import→字段相等）；畸形包报错。
- `LuaApiCatalogTests`：API 清单与权限映射一致。

## 13. 构建与交付

- 新增 `tools/PptInk.PluginStudio.Core`（跨平台逻辑库）、`tools/PptInk.PluginStudio`（WPF 界面）、
  `tools/PptInk.PluginStudio.Tests`（xUnit）并加入 `PptInk.sln`。
- 验证分层：Core + Tests 可在 Linux/Windows 用 `dotnet build` / `dotnet test` 验证；
  WPF 界面需在 Windows 用 `dotnet build PptInk.sln` 验证。
- 分支 `feat/plugin-studio` → 推送 → PR →（用户验证后）合并到 `main`。
- 依赖：AvalonEdit（编辑器）、MoonSharp 2.0.0（语法解析）。

## 14. 风险与限制

- **构建环境限制**：本次实现所在环境为 Linux 且无 .NET SDK，**无法编译/运行 WPF**。
  交付物为源码 + PR，编译与真机验证需在用户的 Windows + .NET 8 环境执行
  （`dotnet build PptInk.sln`）。合并前以用户侧构建/冒烟为准。
- 规则漂移：`ManifestValidator` 与主程序规则需靠同步注释 + 测试固定；主程序规则变更须同步。
- AvalonEdit 与主程序无共用；工具体积与依赖仅作用于工具，不影响主程序。

## 15. 验收标准

1. 能通过向导新建三类插件之一，并在工作台完成编辑。
2. 非法 manifest（Id/entry/元素）被校验拦截并定位；合法 manifest 打包成功。
3. 打包 zip 能被主程序 `InstallFromZip` 成功安装（用户环境实测）。
4. 导入既有 zip 能正确反填；安装/覆盖具备回滚。
5. Lua 语法错误能定位到行列；补全可提示注入 API。
6. 测试项目用例全绿（用户环境 `dotnet test`）。