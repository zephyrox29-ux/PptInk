using System.Windows;
using PptInk.Core;
using PptInk.UI;
using PptInk.Plugins;

// 注意：本文件不 using System.Drawing（与 WPF Media 的 Brushes/Color/Point/Path 全面 CS0104 歧义）；
// 唯一 GDI+ 使用（ScreenCapture 截图）走全限定名。
// v0.9.0 单窗口架构：插件面板族（计时器/聚光灯/点名/板中板）全部降维为舞台 Plugin 层
// 元素（见 UI/PluginPanels.cs），本文件只保留插件逻辑与截图工具。

namespace PptInk.Builtin;

// ==================== 正倒计时 ====================
public sealed class TimerPlugin : IPlugin
{
    private TimerElement? _panel;
    public PluginManifest Manifest { get; } = new()
    {
        Magic = (int)PluginKind.Panel, Id = "builtin.timer", Name = "正/倒计时",
        Entry = "-", Description = "课堂计时：正计时与倒计时",
        Panel = new PanelDecl { Dock = "float", Width = 220, Height = 150, Alpha = 0.9 },
    };

    public void Activate(IPluginHostApi api)
    {
        _panel ??= StageHost.Mount(new TimerElement());
        _panel.ShowPanel();
    }

    public void Deactivate()
    {
        // 停用须停内部计时器再隐藏：HidePanel 只折叠不停表，倒计时/正计时会在隐藏后空转
        _panel?.StopForHost();
        _panel?.HidePanel();
    }
}

// ==================== 焦点聚光灯遮罩 ====================
public sealed class SpotlightPlugin : IPlugin
{
    private SpotlightElement? _panel;
    public PluginManifest Manifest { get; } = new()
    {
        Magic = (int)PluginKind.Draw, Id = "builtin.spotlight", Name = "焦点聚光灯",
        Entry = "-", Description = "暗罩遮蔽，透明圆聚焦局部",
    };

    public void Activate(IPluginHostApi api)
    {
        // 全屏遮罩几何绑定放映屏：每次激活重建（旧窗口 ??= 复用有双坑 ——
        // 点暗区关闭后再激活必崩、换屏后几何错位）
        _panel?.Close();
        _panel = StageHost.Mount(new SpotlightElement());
        _panel.ShowPanel();
    }

    public void Deactivate() => _panel?.HidePanel();
}

// ==================== 截图快照 ====================
public sealed class SnapshotPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Magic = (int)PluginKind.Free, Id = "builtin.snapshot", Name = "截图快照",
        Entry = "-", Description = "全屏快照存桌面，可回贴展示",
        Permissions = new[] { "file", "clipboard" },
    };

    public void Activate(IPluginHostApi api)
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"PptInk快照-{DateTime.Now:HHmmss}.png");
        // GDI 截图 + PNG 编码是纯计算（4K 上实测量级数百毫秒），不依赖 UI 线程 ——
        // 在 UI 线程同步执行会让整个舞台（输入/计时器）冻结编码时长。挪到线程池执行，
        // 激活先给「进行中」反馈；Toast.Show 线程安全（内部封送 UI 线程），完成后回提示。
        api.ShowToast("正在截图…");
        Task.Run(() =>
        {
            try
            {
                ScreenCapture.CaptureActiveShow(path);
                api.ShowToast($"快照已保存：{System.IO.Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Core.AppLog.Instance.Warn($"快照保存失败：{ex.Message}");
                api.ShowToast("快照保存失败");
            }
        });
    }

    public void Deactivate() { }
}

internal static class ScreenCapture
{
    public static void CaptureActiveShow(string savePath)
    {
        // 放映屏感知 + 高 DPI 修正（审查修复）：截 PPT 放映所在屏（未定位回退主屏口径）；
        // ShowScreen.Bounds 为 DIP，GDI 截图需物理像素，否则 125%/150% 机型截到不完整画面。
        var b = ShowScreen.Bounds;
        double scale = ShowScreen.DpiScale;
        int x = (int)Math.Round(b.Left * scale);
        int y = (int)Math.Round(b.Top * scale);
        int w = Math.Max(1, (int)Math.Round(b.Width * scale));
        int h = Math.Max(1, (int)Math.Round(b.Height * scale));
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, bmp.Size);
        bmp.Save(savePath, System.Drawing.Imaging.ImageFormat.Png);
    }
}

// ==================== 随机点名 ====================
public sealed class RandomPickPlugin : IPlugin
{
    private PickElement? _panel;
    public PluginManifest Manifest { get; } = new()
    {
        Magic = (int)PluginKind.Panel, Id = "builtin.randompick", Name = "随机点名",
        Entry = "-", Description = "名单滚动抽取，大字展示",
        Permissions = new[] { "clipboard" },
    };

    public void Activate(IPluginHostApi api)
    {
        _panel ??= StageHost.Mount(new PickElement());
        _panel.ShowPanel();
    }

    public void Deactivate()
    {
        // 停用须停内部计时器再隐藏：HidePanel 只折叠不停表，70ms 滚动会在隐藏后空转
        _panel?.StopForHost();
        _panel?.HidePanel();
    }
}

// ==================== 板中板（画中画白板） ====================
public sealed class BoardInBoardPlugin : IPlugin
{
    private BoardElement? _panel;
    public PluginManifest Manifest { get; } = new()
    {
        Magic = (int)PluginKind.Draw, Id = "builtin.boardinboard", Name = "板中板",
        Entry = "-", Description = "画中画白板：原生 WPF InkCanvas 小窗",
    };

    public void Activate(IPluginHostApi api)
    {
        _panel ??= StageHost.Mount(new BoardElement());
        _panel.ShowPanel();
    }

    public void Deactivate() => _panel?.HidePanel();
}