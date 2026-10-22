using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 类 Mica 的静态实现：读桌面壁纸主色，把画布/侧栏基准色混入少量壁纸色调。
/// 与真 Mica（系统合成材质）的区别：只在启动/主题切换/开关切换时计算一次，
/// 之后纯静态渲染——本机 NVIDIA + 虚拟显示环境下真 Mica 会持续合成并可能
/// 黑屏（docs/stability/crash-history.md），此方案零合成、零额外 CPU。
/// </summary>
public static class WallpaperTintService
{
    // 与 Styles/Tokens.Color.xaml 的 AppCanvasBrush 基准色保持同步。
    public static readonly Color CanvasLight = Color.FromArgb(0xFF, 0xF5, 0xF8, 0xF6);
    public static readonly Color CanvasDark = Color.FromArgb(0xFF, 0x10, 0x16, 0x13);

    // 混入比例：深色画布更暗，稍高一点才可见；都足够小，不可能破坏对比度。
    public const double CanvasRatioLight = 0.07;
    public const double CanvasRatioDark = 0.10;

    private static readonly object Gate = new();
    private static Color? _dominant;
    private static bool _resolved;

    /// <summary>壁纸主色（带缓存）；读不到壁纸返回 null = 不染色。</summary>
    public static Color? GetDominantColor()
    {
        lock (Gate)
        {
            if (_resolved)
            {
                return _dominant;
            }

            _resolved = true;
            try
            {
                var path = FindWallpaper();
                if (path is not null)
                {
                    _dominant = SampleDominant(path);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("wallpaper tint skipped: " + ex.Message);
            }

            return _dominant;
        }
    }

    /// <summary>线性混合：result = base + (wallpaper - base) * ratio。</summary>
    public static Color Mix(Color baseColor, Color wallpaper, double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        return Color.FromArgb(
            0xFF,
            (byte)Math.Round(baseColor.R + (wallpaper.R - baseColor.R) * ratio),
            (byte)Math.Round(baseColor.G + (wallpaper.G - baseColor.G) * ratio),
            (byte)Math.Round(baseColor.B + (wallpaper.B - baseColor.B) * ratio));
    }

    private static string? FindWallpaper()
    {
        // 首选转码副本（设置里换壁纸后必更新，且一定是可解码图片）；
        // 兜底注册表里的原始路径（可能是 .bmp/网络位置）。
        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        if (File.Exists(transcoded))
        {
            return transcoded;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") is string registered && File.Exists(registered))
            {
                return registered;
            }
        }
        catch
        {
            // 注册表不可读时按“无壁纸”处理。
        }

        return null;
    }

    private static Color? SampleDominant(string path)
    {
        // 只在线程池线程调用：WinRT 异步 API 在这里做 sync-over-async 是安全的。
        using var stream = System.IO.File.OpenRead(path);
        using var ras = stream.AsRandomAccessStream();
        var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras)
            .AsTask().GetAwaiter().GetResult();
        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            ScaledWidth = 24,
            ScaledHeight = 24,
        };
        var pixels = decoder.GetPixelDataAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            transform,
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage)
            .AsTask().GetAwaiter().GetResult();
        var bytes = pixels.DetachPixelData();

        // 量化到每通道 4bit 的桶，取样本最多的桶求平均——比全图平均更能
        // 保留壁纸的真实主色（不会被小面积高光/阴影拖偏）。
        var buckets = new Dictionary<int, (int Count, int R, int G, int B)>();
        for (var i = 0; i + 3 < bytes.Length; i += 4)
        {
            var b = bytes[i];
            var g = bytes[i + 1];
            var r = bytes[i + 2];
            var key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
            var slot = buckets.TryGetValue(key, out var existing)
                ? existing
                : (Count: 0, R: 0, G: 0, B: 0);
            buckets[key] = (slot.Count + 1, slot.R + r, slot.G + g, slot.B + b);
        }

        if (buckets.Count == 0)
        {
            return null;
        }

        var best = buckets.Aggregate((a, b) => b.Value.Count > a.Value.Count ? b : a).Value;
        return Color.FromArgb(
            0xFF,
            (byte)(best.R / best.Count),
            (byte)(best.G / best.Count),
            (byte)(best.B / best.Count));
    }
}
