# PptInk 融绘

Windows 课堂放映用的 PPT 手写批注工具。.NET 8 + WPF，x64。

放映一开始工具栏自动浮出，直接在幻灯片上写字、圈重点、擦除。翻页岛固定在左右下角，
不会挡住 PPT 内容。AI 形状识别可选，识别推理跑在本地 Python 进程里。

## 运行

从 [Releases](https://github.com/zephyrox29-ux/PptInk/releases) 下载最新便携包
（`PptInk-v*-portable-win-x64.zip`），解压后双击「启动融绘.bat」。

- 主程序自带 .NET 8 运行时（自包含发布），目标机器不需要预装 .NET。
- 便携包以 `--portable` 启动，不写开机自启，删目录即卸载。
- AI 形状识别需要 Python 3.10+ 和 NumPy：便携包默认不带解释器；在设置中心开启 AI 后
  点「快速检测」，程序会自动配置（本机缺运行时会联网下载嵌入式 Python 3.12 与 NumPy，
  仅此一次；识别推理本身始终在本地，不联网）。首次冷启动要几秒到二十秒，之后常驻。
- 不启用 AI 时书写、擦除、翻页等全部核心功能不受影响。

## 构建

需要 .NET 8 SDK。

```
dotnet build PptInk.sln -c Release
```

Windows 上打便携包（版本号自动取自 csproj）：

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
- 触控手感、穿透、PowerPoint/WPS COM 翻页、希沃一体机行为、混合 DPI 多屏定位
  都得在真机上验收，本仓库的构建与自动化测试覆盖不到。
- 主程序是自包含单文件，杀毒软件可能误报或拖慢首次启动，把 `app/bridge/` 加白名单会好一些。
- 插件暂不做签名校验：插件脚本等同本机代码，只安装可信来源的包。

## 维护

由 [@xianfish](https://github.com/xianfish) 发起并维护；v1.1.0 起的整改与后续开发由
[Hydrotessera](https://github.com/nullAqua) 参与。版本历史见 [docs/CHANGELOG.md](./docs/CHANGELOG.md)。

## 许可证

本项目采用 **GNU Affero General Public License v3.0 (AGPL-3.0)** 授权，全文见 [LICENSE](./LICENSE)；
分发包内随附许可证全文与 [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md)（第三方组件声明）。

贡献前请阅读并同意 [CLA.md](./CLA.md)（贡献者许可协议）；贡献者名录见 [CONTRIBUTORS.md](./CONTRIBUTORS.md)。
