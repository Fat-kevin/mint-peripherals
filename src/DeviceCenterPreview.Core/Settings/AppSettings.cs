using System.Text.Json;

namespace DeviceCenterPreview.Core.Settings;

/// <summary>外观模式的持久化值。不把 ElementTheme 放进 Core，避免 UI 依赖。</summary>
public static class ThemeModes
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string System = "System";

    public static string Normalize(string? value) => value switch
    {
        Light => Light,
        System => System,
        Dark => Dark,
        _ => Light,
    };
}

/// <summary>
/// 「薄荷外设」自己的设置。与 VigilWatt 的 AppSettings 无关：后者是宿主库的默认值，
/// 这里只保存本应用真正会执行的开关，避免出现“界面有开关、实际不生效”的假 UI。
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>界面外观。Dark / Light / System；默认浅色薄荷。</summary>
    public string ThemeMode { get; init; } = ThemeModes.Light;

    /// <summary>壁纸染色：读桌面壁纸主色给画布混入静态环境色（类 Mica 的零合成实现）。</summary>
    public bool WallpaperTintEnabled { get; init; } = true;

    /// <summary>耳机模块；关闭后不启动 SPP 扫描，页面显示“已关闭”。</summary>
    public bool HeadsetModuleEnabled { get; init; } = true;

    public bool MouseModuleEnabled { get; init; } = true;

    public bool RemoteModuleEnabled { get; init; } = true;

    /// <summary>常驻电量监控；关闭后不启动 VigilWatt DeviceManager，仍保留三个控制模块。</summary>
    public bool BatteryMonitoringEnabled { get; init; } = true;

    /// <summary>写入总闸。默认开启以保持“用户点控件就写入”的原行为，关闭后必须真的拦截。</summary>
    public bool HeadsetWriteEnabled { get; init; } = true;

    public bool MouseWriteEnabled { get; init; } = true;

    public bool RemoteWriteEnabled { get; init; } = true;

    public bool NotifyOnConnect { get; init; } = true;

    public bool NotifyOnLowBattery { get; init; } = true;

    public bool NotifyOnWriteFailure { get; init; } = true;

    /// <summary>低于等于该值提醒；0 表示关闭低电提醒。</summary>
    public int LowBatteryThreshold { get; init; } = 20;

    public static AppSettings Default { get; } = new();

    /// <summary>把外部文件里的非法值收敛到可用范围；未知字段由 JSON 反序列化忽略。</summary>
    public AppSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        ThemeMode = ThemeModes.Normalize(ThemeMode),
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 0, 100),
    };

    public bool IsModuleEnabled(string module) => module switch
    {
        "headset" => HeadsetModuleEnabled,
        "mouse" => MouseModuleEnabled,
        "remote" => RemoteModuleEnabled,
        "monitor" => BatteryMonitoringEnabled,
        _ => false,
    };

    public bool IsWriteEnabled(string module) => module switch
    {
        "headset" => HeadsetWriteEnabled,
        "mouse" => MouseWriteEnabled,
        "remote" => RemoteWriteEnabled,
        _ => false,
    };
}

/// <summary>
/// settings.json 的加载/保存。写入走临时文件再替换，避免断电留下半个 JSON；
/// 读到损坏文件时保留现场（.corrupt-时间戳）而不是静默覆盖。
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();

    public AppSettingsStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeviceCenterPreview");
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
    }

    public string RootDirectory { get; }

    public string SettingsPath { get; }

    public string? LastLoadWarning { get; private set; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            LastLoadWarning = null;
            if (!File.Exists(SettingsPath))
            {
                return AppSettings.Default;
            }

            try
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is null)
                {
                    throw new JsonException("settings.json 反序列化为 null");
                }

                return settings.Normalize();
            }
            catch (Exception ex)
            {
                LastLoadWarning = $"settings.json 读取失败，已使用默认值：{ex.Message}";
                TryPreserveCorruptFile();
                return AppSettings.Default;
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(RootDirectory);
            var normalized = settings.Normalize();
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
            LastLoadWarning = null;
        }
    }

    public AppSettings Reset()
    {
        var defaults = AppSettings.Default;
        Save(defaults);
        return defaults;
    }

    /// <summary>输入校验用：只反序列化并收敛，不写盘。</summary>
    public static AppSettings Parse(string json) =>
        (JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.Default).Normalize();

    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings.Normalize(), JsonOptions);

    private void TryPreserveCorruptFile()
    {
        try
        {
            var backup = SettingsPath + $".corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(SettingsPath, backup, overwrite: false);
        }
        catch
        {
            // 保留现场失败不能阻止应用启动。
        }
    }
}
