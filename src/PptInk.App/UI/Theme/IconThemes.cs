using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace PptInk.UI.Theme;

/// <summary>
/// 工具栏图标主题（v1.1.2）：内嵌 Material Symbols 三套风格（圆角/线性/锐利）的 Path Data。
/// 数据来源 @material-symbols/svg-400（Apache-2.0），随程序离线内嵌，无运行时网络依赖。
/// 主题缺失/解析失败时返回 false，由调用方回退 Icons.xaml 经典矢量。
/// </summary>
public static class IconThemes
{
    /// <summary>可用主题（与设置项取值一致；classic 由 Icons.xaml 承担，不在此列）。</summary>
    public static readonly string[] Kinds = { "rounded", "outlined", "sharp" };

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<(string Theme, string Key), Geometry> Cache = new();
    private static Dictionary<string, Dictionary<string, string>>? _data;
    private static bool _loaded;

    public static bool TryGet(string theme, string key, out Geometry geometry)
    {
        geometry = null!;
        if (string.IsNullOrWhiteSpace(theme) || theme == "classic") return false;

        EnsureLoaded();
        var data = _data;
        if (data is null || !data.TryGetValue(key, out var map) || !map.TryGetValue(theme, out var d))
            return false;

        geometry = Cache.GetOrAdd((theme, key), _ =>
        {
            var g = Geometry.Parse(d);
            g.Freeze();
            return g;
        });
        return true;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var asm = typeof(IconThemes).Assembly;
                var name = Array.Find(asm.GetManifestResourceNames(),
                    n => n.EndsWith("Assets.icons.json", StringComparison.OrdinalIgnoreCase));
                if (name is null) return;
                using var stream = asm.GetManifestResourceStream(name);
                if (stream is null) return;
                using var reader = new StreamReader(stream);
                _data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(reader.ReadToEnd());
            }
            catch
            {
                _data = null;   // 数据损坏：静默回退经典图标，不影响工具栏可用
            }
        }
    }
}