using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using PptInk.Core;
using PptInk.UI;
using Application = System.Windows.Application;

namespace PptInk;

/// <summary>
/// 托盘小图标（可选隐藏）。WinForms NotifyIcon 互操作，避免引第三方托盘库。
/// 菜单克制：仅"设置 / 隐藏图标 / 退出"，课堂场景不打扰。
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppStateMachine _state;
    private readonly UiOrchestrator _ui;
    private bool _hiddenByMenu;   // 托盘菜单「隐藏」所致（区别于启动配置 HideTrayIcon 的常驻隐藏）

    public TrayIconController(Application app, AppStateMachine state, UiOrchestrator ui)
    {
        _state = state;
        _ui = ui;

        var menu = new ContextMenuStrip();
        menu.Items.Add("手动唤起工具栏（未放映也可书写）", null, (_, _) => _ui.ForceActivate());
        // —— 手动锁定：勾选后任何场景不再自动隐让（风险表中的"手动锁定 UI"入口，运行时态不持久化） ——
        var lockItem = new ToolStripMenuItem("锁定工具栏显示（不自动隐让）")
        {
            CheckOnClick = true,
            Checked = _state.UiLocked,
            ToolTipText = "勾选后：希沃白板/全屏视频前台时也不再自动收回，放映结束才收回。",
        };
        lockItem.CheckedChanged += (_, _) => _state.UiLocked = lockItem.Checked;
        menu.Items.Add(lockItem);
        menu.Items.Add(new ToolStripSeparator());

        // —— 新手引导：勾选 = 每次进入放映自动提示一次；走完引导自动取消勾选 ——
        var onboardingItem = new ToolStripMenuItem("新手引导（放映时提示）")
        {
            CheckOnClick = true,
            Checked = AppConfig.Current.EnableOnboarding,
            ToolTipText = "勾选后：每次开始放映且工具栏出现时，自动播放约 30 秒的引导动画。",
        };
        // 打开菜单时同步最新状态（引导走完会自动取消勾选）
        menu.Opening += (_, _) => onboardingItem.Checked = AppConfig.Current.EnableOnboarding;
        onboardingItem.CheckedChanged += (_, _) =>
        {
            AppConfig.Current.EnableOnboarding = onboardingItem.Checked;
            AppConfig.Current.Save();
        };
        menu.Items.Add(onboardingItem);
        menu.Items.Add("立即播放一次新手引导", null, (_, _) => _ui.StartOnboarding(force: true));
        menu.Items.Add(new ToolStripSeparator());

        // 审查修复：原为普通项（无勾选态、不持久化、不同步 PalmRejection）→ 点了没反应。
        // 改为勾选项 + 经 UiOrchestrator 落配置并实时同步手掌抑制。
        // v0.3.1：语义更新为「严格手掌抑制」（原「仅指尖书写」的笔让位语义已随
        // 主动笔支持移除；现仅收紧手掌判定倍率 4 → 2.5）
        var fingerItem = new ToolStripMenuItem("严格手掌抑制")
        {
            CheckOnClick = true,
            Checked = AppConfig.Current.OnlyFingerInk,
            ToolTipText = "手掌抑制更敏感（接触面积超正常书写 2.5 倍即判手掌；默认 4 倍）",
        };
        fingerItem.CheckedChanged += (_, _) => _ui.SetOnlyFingerInk(fingerItem.Checked);
        menu.Items.Add(fingerItem);
        menu.Opening += (_, _) => fingerItem.Checked = AppConfig.Current.OnlyFingerInk;   // 设置页改动同步回勾选态

        // —— v0.4.0 调试模式：诊断面板（输入通道/接触面积/压感分层/报点率/档案校准/FPS）——
        var debugItem = new ToolStripMenuItem("调试模式（诊断面板）")
        {
            CheckOnClick = true,
            Checked = AppConfig.Current.DebugMode,
            ToolTipText = "屏幕角落显示策略各层数据：输入通道、接触面积、压感探测、报点率、档案与运行时校准、渲染情况。面向售后定位与调参。",
        };
        debugItem.CheckedChanged += (_, _) => _ui.SetDebugMode(debugItem.Checked);
        menu.Items.Add(debugItem);
        menu.Opening += (_, _) => debugItem.Checked = AppConfig.Current.DebugMode;

        menu.Items.Add("导出板书（PNG/SVG/JSON）", null, (_, _) => _ui.ExportInk());
        // v1.1.1 真机验收配套：环境快照 + 配置 + 最近日志（不含板书/截图），排障可直发
        menu.Items.Add("导出诊断包（排障用）", null, (_, _) => _ui.ExportDiagnostics());
        menu.Items.Add("设置…", null, (_, _) => _ui.OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("隐藏托盘图标（下次放映自动恢复）", null, (_, _) => Hide());
        menu.Items.Add("退出融绘", null, (_, _) =>
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown()));

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "融绘 · 已驻留（检测 PPT 放映中）",
            Visible = false,
            Icon = LoadIcon(),
        };
        _notifyIcon.DoubleClick += (_, _) =>
        {
            if (_state.Phase == AppPhase.S0Background) _ui.ForceActivate();
        };
    }

    public void Initialize(bool hideByDefault)
    {
        if (!hideByDefault) Show();
    }

    public void Show()
    {
        _hiddenByMenu = false;
        _notifyIcon.Visible = true;
    }

    public void Hide()
    {
        _hiddenByMenu = true;
        _notifyIcon.Visible = false;
    }

    /// <summary>兑现托盘菜单项「隐藏托盘图标（下次放映自动恢复）」的承诺：放映唤起（S1）时
    /// 恢复被菜单隐藏的图标。托盘是设置/导出/退出的唯一常规入口（OnExplicitShutdown），
    /// 进程内永不恢复会令教师对运行中的程序失去全部控制面。启动配置 HideTrayIcon 的隐藏
    /// 不经此恢复（设置页语义：重启才生效）。</summary>
    public void RestoreIfHiddenByMenu()
    {
        if (_hiddenByMenu) Show();
    }

    private static Icon LoadIcon()
    {
        // 嵌入资源优先；缺失时画一个极简 16px 圆点，保证不因资源问题崩掉托盘
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/tray.ico", UriKind.Absolute))?.Stream;
            if (stream is not null) return new Icon(stream);
        }
        catch { }
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(24, 26, 32));
            g.FillEllipse(System.Drawing.Brushes.OrangeRed, 4, 4, 8, 8);
        }
        // v1.1.1：记录自建 HICON，Dispose 时 DestroyIcon —— Icon.FromHandle 的句柄
        // 不随 Icon/NotifyIcon 释放，每次重建托盘泄漏一枚 GDI 句柄。
        // 修复：重建前先销毁上一枚自建 HICON，否则本方法被再次调用时旧句柄被直接覆盖而泄漏。
        if (_ownedHicon != IntPtr.Zero)
        {
            try { DestroyIcon(_ownedHicon); } catch { }
            _ownedHicon = IntPtr.Zero;
        }
        _ownedHicon = bmp.GetHicon();
        return Icon.FromHandle(_ownedHicon);
    }

    private static IntPtr _ownedHicon = IntPtr.Zero;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_ownedHicon != IntPtr.Zero)
        {
            try { DestroyIcon(_ownedHicon); } catch { }
            _ownedHicon = IntPtr.Zero;
        }
    }
}
