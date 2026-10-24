# PptInk 融绘（源码归档）

本归档为 **v0.9.7**（.NET 8 / WPF / Windows x64）源码。**本版已在 Linux + .NET 8 SDK 上以 `EnableWindowsTargeting` 交叉编译通过（0 错误 0 警告）**，并已产出可直接运行的 Windows x64 自包含便携包（含 .NET 运行时、Python 嵌入式运行时、NumPy、OpenCV）。本版聚焦用户反馈的 7 项问题：新手引导补隐藏工具栏说明、工具栏显示与设置同步、笔/荧光笔浮岛颜色与设置同步、AI 桥检测修复、擦除性能优化、滑块触摸优化、笔迹流畅感/笔锋/惯性优化。触摸、穿透、PPT COM 与希沃设备行为仍需真机验收。

PptInk 是面向 Windows 课堂放映场景的 PPT 手写批注工具，当前源码目标为 **.NET 8 / WPF / Windows x64**。项目包括单窗口分层舞台、触控/手写输入、墨迹历史与擦除、PowerPoint 检测与翻页、Lua 插件宿主，以及按需启动的 Python 本地形状识别服务。

> **产物说明：**本仓库同时包含源码与一份已构建的 Windows x64 便携包 `artifacts/PptInk-v0.9.7-portable-win-x64.zip`（自包含 .NET + 嵌入式 Python + NumPy + OpenCV，解压即可运行）。源码包亦支持在 Windows 上用 `scripts/build-portable.ps1` 重新构建。

## 目录

- `src/PptInk.App/`：WPF 主程序、Win32/COM 集成、墨迹引擎、统一舞台 UI、插件系统。
- `python/`：本地 AI 桥与手绘形状识别引擎。
- `plugins/`：Lua 插件示例。
- `docs/工程审计与整改报告.md`：工程审计范围、既有修改项和待真机验证项。
- `docs/触摸交互整改说明.md`：触摸事件、拖动所有权、翻页岛及画笔面板整改说明。
- `docs/设置中心与交互体验增强说明.md`：新增设置项、布局优化与验证边界。
- `docs/UIUX与性能优化记录.md`：动效会话、设置页状态、渲染热路径优化及 WPF 路由事件崩溃热修复记录。
- `.github/workflows/ci.yml`：Python 自动测试与 Windows .NET Release 构建门禁。

## 开发要求

- Windows 10/11 x64；Visual Studio 2022 或 .NET 8 SDK。
- Python 3.10+（CI 使用 3.12）；本地识别使用 NumPy，OpenCV 是可选增强依赖。
- PowerPoint / 触控屏 / 希沃硬件测试需要相应 Windows 真机环境，Linux CI 不能替代这些集成测试。

## 构建与便携包

在 Windows PowerShell 中：

```powershell
dotnet restore .\PptInk.sln
dotnet build .\PptInk.sln -c Release
```

生成 **Windows x64 自包含 .NET 便携包**（目标机器无需预装 .NET Runtime）：

```powershell
.\scripts\build-portable.ps1 -Configuration Release -Runtime win-x64
```

产物位于 `artifacts\PptInk-v0.9.7-portable-win-x64.zip`。该脚本会一并放入 Python 桥源码与示例插件。**默认不捆绑** Python 解释器与 NumPy；需要 AI 本地识别时，用 `-PythonRuntimePath <完整 Windows 嵌入式运行时目录>` 提供含 `python.exe` 与 NumPy 的运行时（脚本会复制到 `bridge\python\`）。不要把仅有 `python.exe`、却缺少 NumPy 的目录当作完整 AI 运行时。

`.github/workflows/ci.yml` 在 Windows runner 上会执行 Release 构建和便携包发布，并把 ZIP 保存为 Actions artifact；需要在 GitHub 上运行工作流后才能取得该构建产物。本次归档本身是源码 ZIP，没有假装成已编译二进制。

运行 Python 测试：

```powershell
python -m pip install -r .\python\requirements-dev.txt
python -m pytest .\python\tests -q
```

AI 桥服务协议是标准输入/输出的一行一个 JSON 对象。调试时可在 `python/` 目录运行：

```powershell
python -u .\ink_bridge.py
```

此命令启动交互式桥进程，不会启动 WPF 主程序。

## 发布前的硬门槛

1. Windows CI Release 构建通过，且依赖还原完成。
2. 真机覆盖：单屏/双屏、多 DPI、触控笔/手指/鼠标、希沃设备、PowerPoint 打开放映/强制退出重启。
3. 覆盖：轻触点墨、连续长笔、三种擦除和撤销/重做、切页还原、导出文件可打开、AI 开关反复切换、插件异常与关闭清理。
4. 用独立发布流程生成 x64 包，并确认 Python 嵌入式运行时和 NumPy 与目标系统架构匹配。

当前归档不应被视为已完成 Windows 构建或硬件验收的安装包。本轮可复验项目包括 Python 回归测试、XAML/XML 解析及 C# 结构静态检查；由于当前执行环境没有 .NET SDK/MSBuild，未声称这版 WPF 已成功编译。部署前应通过 Windows CI 执行 Release 构建，再在目标设备验证手指/手写笔/鼠标输入、面板定位、PowerPoint COM、真实笔迹与渲染性能；可使用程序内的「采样模式」（设置中心 / 托盘菜单）记录运行数据。


## Windows 发布构建说明（编译错误修复版）

本版修复了用户在 `dotnet publish` 时报告的 6 处 C# 编译错误：
- `OnboardingGuide.cs`：避免 lambda 捕获 `out` 参数（CS1628）。
- `Toolbar.Drag.cs`：补充 `System.Windows.Controls`，使 `Border` 类型可见。
- `Toolbar.Drag.cs`：将 `Stage` 与 `Toolbar` 的坐标根统一为 `IInputElement`，修复 `??` 两侧具体类型不兼容。

在 Windows x64 上可尝试：
```powershell
dotnet publish .\src\PptInk.App\PptInk.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\artifacts\win-x64
```

注意：此命令会自包含 .NET 运行时，但项目中 Python/AI 等外部运行环境仍需按 `installer/` 的打包逻辑检查并合并。此环境没有 Windows .NET SDK/MSBuild，故此 ZIP 尚未声称通过真实 C# 编译。


## v0.9.6 本轮界面整改

本轮修复舞台元素首次显示时的非有限坐标与左上角堆叠，并改为独占槽位布局：底部中央工具栏、左右翻页岛、分割副栏各占不同区域；所有普通浮岛/工具参数面板共享唯一工作槽；调试 HUD、状态徽标、Toast 也各有预留区域。拖动约束依照所属槽位执行，不扫描矩形相交，也不会因为布局冲突自动把工具栏收纳。普通放映下绘制层状态牌隐藏，插件设置只负责启用/禁用，工具组件从工具栏唤起，画笔/橡皮参数在工具栏浮岛调整。设置中心保留独立 Python 桥与合成图形识别的 AI 快速自检。

本轮 Python/源码契约测试为 39 项通过。当前环境没有 .NET SDK/MSBuild，发布归档仍是源码包；C# 编译与 WPF/PowerPoint 实机行为尚未通过 Windows 验证。详细布局规则见 `docs/本轮界面定位与快捷面板整改说明.md`。

## v0.9.7 本轮修复（用户反馈 7 项）

本轮针对用户反馈的 7 项问题逐条修复，并**首次在本环境完成真实 C#/XAML 编译与 Windows 便携包产出**：

1. **新手引导补隐藏工具栏说明**：`OnboardingSteps.cs` 新增「工具栏想隐就隐」步骤，说明长按拖动柄 0.6 秒收纳为胶囊、点按胶囊展开、拖动柄可左右移动。
2. **工具栏显示与设置同步**：三层自愈（`AppConfig.NormalizeToolbarLayout` / `Toolbar.RebuildLayout` / `UiOrchestrator.RebuildSegment`）——修复「分割标记残留但副岛为空」导致撤销/重做/AI/插件既不在主栏也不在副岛的问题；Ops 组被旧配置全隐藏时自动恢复可见；设置页提供「合并副岛回主栏」入口。
3. **笔浮岛颜色与设置同步**：`Toolbar.Palette.cs` 浮岛色板改为设置中心同源预设（笔 8 色 / 荧光笔 5 色）+ 最近用色去重合并；按工具区分标题、当前色、RGB 滑块与预览，荧光笔独立色板（修复原先只有红黄 2 色的硬编码色板问题）。
4. **AI 桥检测修复**：`PythonBridge.BridgeDir` 多路径探测桥脚本（bridge/python/仓库根/扁平布局）；握手超时 15s→25s、健康检查超时 18s→28s（嵌入式运行时冷启动在杀软扫描下可达 22s）；嵌入式运行时缺失时明确提示而非静默回退。
5. **擦除性能优化**：像素擦除只扫描**新增路径段**（游标跟踪）+ 包围盒预筛（`HitByPathFast`/`SplitStroke` 粗筛）；擦除判定节流至 ~125Hz；渲染层擦除手势期间跳过整页缓存重合成（4K 位图 Clear+Render 是擦除掉帧的首要来源）。
6. **滑块触摸优化**：`Tokens.xaml` 新增 `TouchSliderStyle`（28px Thumb / 6px 轨道 / 44px 高 / 点轨即跳 / 按下放大）；设置页 7 处滑块与浮岛滑块统一应用；画笔粗细/识别灵敏度/工具栏缩放/橡皮大小增加 −/+ 微调按钮。
7. **书写体验优化**：`SmoothingFilter` 引入 **OneEuro 自适应滤波**（慢写强防抖、快写低延迟，替代固定 EMA）；`StrokeData.BuildWidths` 增加**起笔渐入 + 收笔收锋**（`ApplyLiftTaper`）与**自适应宽度低通**；渲染层 `DrawBatch` 由折线升级为**二次贝塞尔曲线**，笔画真正圆润。

**构建验证（本环境实测）**：

```bash
export PATH=/opt/dotnet:$PATH
dotnet build PptInk.sln -c Release -p:EnableWindowsTargeting=true          # 0 Warning / 0 Error
dotnet publish src/PptInk.App/PptInk.App.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true \
  -o artifacts/win-x64                                                     # 产出 69MB 单文件 exe
```

便携包结构：`app/PptInk.exe`（自包含 .NET 8 + WPF）+ `app/bridge/`（Python 桥）+ `app/bridge/python/`（嵌入式 Python 3.12.10 + NumPy 2.5.4 + OpenCV 4.11）。详细记录见 `docs/书写与交互体验优化记录.md`。

## 许可证

本项目采用 **GNU Affero General Public License v3.0 (AGPL-3.0)** 授权，全文见 [LICENSE](./LICENSE)。

贡献前请阅读并同意 [CLA.md](./CLA.md)（贡献者许可协议）；贡献者名录见 [CONTRIBUTORS.md](./CONTRIBUTORS.md)。
