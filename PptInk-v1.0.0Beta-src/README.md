# PptInk 融绘

Windows 课堂放映用的 PPT 手写批注工具。.NET 8 + WPF，x64。

放映一开始工具栏自动浮出，直接在幻灯片上写字、圈重点、擦除。翻页岛固定在左右下角，
不会挡住 PPT 内容。AI 形状识别可选，跑在本地 Python 进程里，不联网。

## 运行

解压 `artifacts/PptInk-v1.0.0Beta-portable-win-x64.zip`，双击「启动融绘.bat」。

包里已经装了 .NET 8 运行时、嵌入式 Python 3.12、NumPy 和 OpenCV，目标机器不需要
预装任何东西。首次跑 AI 识别时 Python 冷启动要几秒到二十秒（看机器和杀毒软件），
之后常驻，不再等。

## 构建

需要 .NET 8 SDK。

```
dotnet build PptInk.sln -c Release
```

Windows 上打便携包：

```powershell
.\scripts\build-portable.ps1 -Configuration Release -Runtime win-x64
```

非 Windows 环境也能交叉编译出 Windows 产物：

```bash
dotnet build PptInk.sln -c Release -p:EnableWindowsTargeting=true
dotnet publish src/PptInk.App/PptInk.App.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:EnableWindowsTargeting=true -o out
```

## 测试

```
python -m pip install -r python/requirements-dev.txt
python -m pytest python/tests -q
```

## 目录

```
src/PptInk.App/     WPF 主程序：墨迹引擎、舞台 UI、PowerPoint 集成、插件宿主
python/             AI 桥（stdio 行式 JSON）与手绘形状识别
plugins/            Lua 插件示例
scripts/            便携包构建脚本
docs/CHANGELOG.md   版本历史
```

## 已知限制

- 只能在 Windows 10/11 x64 上运行（WPF 的限制）。
- 触控手感、穿透、PowerPoint COM 翻页、希沃设备行为都得在真机上验收，本仓库的构建覆盖不到。
- 主程序是自包含单文件，杀毒软件可能误报或拖慢首次启动，把 `app/bridge/` 加白名单会好一些。