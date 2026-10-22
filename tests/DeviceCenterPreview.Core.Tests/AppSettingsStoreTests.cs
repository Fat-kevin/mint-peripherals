using DeviceCenterPreview.Core.Settings;
using Xunit;

namespace DeviceCenterPreview.Core.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeviceCenterPreview.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var store = new AppSettingsStore(_root);

        var settings = store.Load();

        Assert.True(settings.HeadsetModuleEnabled);
        Assert.True(settings.HeadsetWriteEnabled);
        Assert.Equal(20, settings.LowBatteryThreshold);
        Assert.Equal(ThemeModes.Light, settings.ThemeMode);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new AppSettingsStore(_root);
        var expected = AppSettings.Default with
        {
            MouseModuleEnabled = false,
            RemoteWriteEnabled = false,
            NotifyOnLowBattery = false,
            LowBatteryThreshold = 35,
            ThemeMode = ThemeModes.Light,
        };

        store.Save(expected);

        Assert.Equal(expected, store.Load());
    }

    [Fact]
    public void Load_normalizes_unknown_theme_to_light()
    {
        var store = new AppSettingsStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.SettingsPath, """{"themeMode":"neon"}""");

        Assert.Equal(ThemeModes.Light, store.Load().ThemeMode);
    }

    [Fact]
    public void Load_clamps_out_of_range_threshold()
    {
        var store = new AppSettingsStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.SettingsPath, """{"lowBatteryThreshold":999}""");

        Assert.Equal(100, store.Load().LowBatteryThreshold);
    }

    [Fact]
    public void Corrupt_file_is_preserved_and_defaults_are_returned()
    {
        var store = new AppSettingsStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.SettingsPath, "{ not json");

        var settings = store.Load();

        Assert.Equal(AppSettings.Default, settings);
        Assert.NotNull(store.LastLoadWarning);
        Assert.NotEmpty(Directory.GetFiles(_root, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Reset_writes_defaults()
    {
        var store = new AppSettingsStore(_root);
        store.Save(AppSettings.Default with { RemoteModuleEnabled = false });

        var reset = store.Reset();

        Assert.True(reset.RemoteModuleEnabled);
        Assert.True(store.Load().RemoteModuleEnabled);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 测试临时目录清理失败不影响断言。
        }
    }
}
