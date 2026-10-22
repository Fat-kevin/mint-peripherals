using DeviceCenterPreview.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// Runtime theme plumbing. ThemeDictionaries drive almost all XAML; the two
/// code-built tables (hub cards / remote mapping) ask this helper for the brush
/// that belongs to the element's actual (not application's) theme.
/// </summary>
public static class AppTheme
{
    private static bool? _lightOverride;

    /// <summary>Raised after the main window's actual theme changes, so code-built controls can re-resolve brushes.</summary>
    public static event EventHandler? ThemeChanged;

    public static ElementTheme Resolve(string? preference) => ThemeModes.Normalize(preference) switch
    {
        ThemeModes.Light => ElementTheme.Light,
        ThemeModes.System => ElementTheme.Default,
        _ => ElementTheme.Dark,
    };

    public static bool IsLight(FrameworkElement element) =>
        _lightOverride ?? ResolveIsLight(element);

    /// <summary>
    /// Record the shell's resolved theme. Explicit Dark/Light come from the saved
    /// preference; System falls back to the shell's actual theme. Controls inside
    /// cached pages can briefly report a stale ActualTheme, so the shell is the
    /// single source of truth for code-built brushes.
    /// </summary>
    public static void SyncFrom(FrameworkElement root)
    {
        var light = ResolveIsLight(root);
        if (_lightOverride == light)
        {
            return;
        }

        _lightOverride = light;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool ResolveIsLight(FrameworkElement element)
    {
        var mode = ThemeModes.Normalize(App.Settings.ThemeMode);
        if (mode == ThemeModes.Light)
        {
            return true;
        }

        if (mode == ThemeModes.Dark)
        {
            return false;
        }

        return element.ActualTheme == ElementTheme.Light ||
               (element.ActualTheme == ElementTheme.Default &&
                Application.Current.RequestedTheme == ApplicationTheme.Light);
    }

    /// <summary>
    /// Look up a brush in the Light/Default theme dictionary that applies to
    /// this element. Application.Current.Resources[key] resolves against the
    /// application theme, so it is not safe after a per-window theme switch.
    /// </summary>
    public static Brush GetBrush(FrameworkElement element, string key)
    {
        var themeKey = IsLight(element)
            ? "Light"
            : "Default";
        if (TryFindInTheme(Application.Current.Resources, themeKey, key, out var value) &&
            value is Brush themed)
        {
            return themed;
        }

        return Application.Current.Resources.TryGetValue(key, out value) && value is Brush fallback
            ? fallback
            : new SolidColorBrush(Colors.Transparent);
    }

    public static void ApplyTitleBar(Window window, FrameworkElement root)
    {
        try
        {
            var bar = window.AppWindow.TitleBar;
            var light = IsLight(root);
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = light
                ? Color.FromArgb(255, 31, 35, 43)
                : Color.FromArgb(255, 245, 247, 251);
            bar.ButtonInactiveForegroundColor = light
                ? Color.FromArgb(255, 122, 130, 143)
                : Color.FromArgb(255, 124, 133, 148);
            bar.ButtonHoverBackgroundColor = light
                ? Color.FromArgb(255, 232, 235, 240)
                : Color.FromArgb(255, 45, 49, 59);
            bar.ButtonHoverForegroundColor = light
                ? Color.FromArgb(255, 22, 24, 29)
                : Color.FromArgb(255, 255, 255, 255);
            bar.ButtonPressedBackgroundColor = light
                ? Color.FromArgb(255, 221, 225, 232)
                : Color.FromArgb(255, 58, 64, 80);
            bar.ButtonPressedForegroundColor = light
                ? Color.FromArgb(255, 22, 24, 29)
                : Color.FromArgb(255, 255, 255, 255);
        }
        catch
        {
            // A non-standard presenter/titlebar must never block the app.
        }
    }

    private static bool TryFindInTheme(
        ResourceDictionary dictionary,
        string themeKey,
        string key,
        out object? value)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themeValue) &&
            themeValue is ResourceDictionary theme &&
            theme.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (TryFindInTheme(merged, themeKey, key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }
}
