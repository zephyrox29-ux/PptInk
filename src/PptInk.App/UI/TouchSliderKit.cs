using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PptInk.UI;

/// <summary>
/// v0.9.8 触摸直驱滑块：把触摸位置直接换算成 Value，绕开 WPF Slider 内建的 Thumb 状态机。
///
/// 为什么需要它：大屏红外框上内建 Thumb 拖动经常「拖不动」或「拖到一半丢手」——
/// WPF 的 Thumb 依赖内建捕获状态机，在「触摸提升（promotion）+ 合成鼠标」双重路径下
/// 时序不稳；再加上自定义模板里 Track 两侧的 RepeatButton 无高度，实际命中区很窄。
/// 本助手改成「按下即定位、移动即跟随、松手即停」的直驱模型：
///   1) 触摸按下：捕获该触点，并立刻把 Thumb 送到指下（点哪跳哪，不用先对准 Thumb）；
///   2) 触摸移动：按 Track.ValueFromPoint 换算新值（手指滑出滑块范围也不断线）；
///   3) 触摸抬起：释放捕获；随后的合成鼠标事件在时间窗内忽略，避免二次跳动。
/// 鼠标路径保持 WPF 内建（桌面端本就正常），只在触摸会话窗口内让位。
/// </summary>
internal static class TouchSliderKit
{
    private sealed class State
    {
        public bool Attached;
        public TouchDevice? Device;   // 当前持有会话的触点
        public long TouchEndTick;     // 抬起时刻（抑制随后的合成鼠标）
    }

    private static readonly ConditionalWeakTable<Slider, State> States = new();
    private const long SuppressMouseMs = 700;   // 触摸抬起后忽略合成鼠标的时间窗

    /// <summary>挂载触摸直驱（幂等）。返回同一实例，便于链式调用。</summary>
    public static Slider Attach(Slider slider)
    {
        var st = States.GetOrCreateValue(slider);
        if (st.Attached) return slider;
        st.Attached = true;
        slider.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnTouchDown), true);
        slider.AddHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(OnTouchMove), true);
        slider.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnTouchUp), true);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
        return slider;
    }

    private static void OnTouchDown(object? sender, TouchEventArgs e)
    {
        if (sender is not Slider slider) return;
        var st = States.GetOrCreateValue(slider);
        if (st.Device is not null) return;            // 已有触点持有会话（多指只认首指）
        st.Device = e.TouchDevice;
        try { e.TouchDevice.Capture(slider); } catch { /* 设备同帧切换时忽略 */ }
        SetValueFromPoint(slider, e.GetTouchPoint(slider).Position);
        e.Handled = true;
    }

    private static void OnTouchMove(object? sender, TouchEventArgs e)
    {
        if (sender is not Slider slider) return;
        var st = States.GetOrCreateValue(slider);
        if (!ReferenceEquals(st.Device, e.TouchDevice)) return;   // 只认持有会话的触点
        SetValueFromPoint(slider, e.GetTouchPoint(slider).Position);
        e.Handled = true;
    }

    private static void OnTouchUp(object? sender, TouchEventArgs e)
    {
        if (sender is not Slider slider) return;
        var st = States.GetOrCreateValue(slider);
        if (!ReferenceEquals(st.Device, e.TouchDevice)) return;
        st.Device = null;
        st.TouchEndTick = Environment.TickCount64;
        try { e.TouchDevice.Capture(null); } catch { }
        e.Handled = true;
    }

    private static void OnMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;
        var st = States.GetOrCreateValue(slider);
        // 触摸抬起后短时间内到达的合成鼠标：忽略，交由触摸会话的结果
        if (Environment.TickCount64 - st.TouchEndTick < SuppressMouseMs)
            e.Handled = true;
    }

    /// <summary>把滑块内坐标换算成 Value：借 Track.ValueFromPoint 拿到与内建拖动完全一致的
    /// 值域映射（Thumb 半宽边距、Tick 对齐等细节由 Track 统一处理）。</summary>
    private static void SetValueFromPoint(Slider slider, Point pointInSlider)
    {
        if (slider.Template?.FindName("PART_Track", slider) is not Track track) return;
        Point p = slider.TranslatePoint(pointInSlider, track);
        double v = track.ValueFromPoint(p);
        if (!double.IsFinite(v)) return;
        slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum);
    }
}