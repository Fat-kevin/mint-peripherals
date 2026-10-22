using System.Diagnostics;
using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceCenterPreview.App.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly string[] ThemeModeValues =
    [
        ThemeModes.Dark,
        ThemeModes.Light,
        ThemeModes.System,
    ];

    private static readonly string[] ThemeModeLabels =
    [
        "深色",
        "浅色",
        "跟随系统",
    ];

    private bool _loading;
    private bool _resetArmed;
    private DispatcherTimer? _resetTimer;
    private List<string> _backupFiles = new();

    public SettingsPage()
    {
        InitializeComponent();
        ThemeCombo.ItemsSource = ThemeModeLabels;
        LowBatteryThresholdCombo.ItemsSource = new[] { 0, 10, 15, 20, 25, 30, 40, 50, 60, 80 };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PageScroll.SizeChanged += (_, _) => PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
        LoadSettings();
        ResetScrollToTop();
    }

    private void ResetScrollToTop()
    {
        PageScroll.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(() => PageScroll.ChangeView(null, 0, null, true));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _resetTimer?.Stop();
        _resetTimer = null;
        _resetArmed = false;
        ResetButton.Content = "恢复默认设置";
    }

    private void LoadSettings()
    {
        _loading = true;
        var settings = App.Settings;
        HeadsetModuleSwitch.IsOn = settings.HeadsetModuleEnabled;
        MouseModuleSwitch.IsOn = settings.MouseModuleEnabled;
        RemoteModuleSwitch.IsOn = settings.RemoteModuleEnabled;
        MonitorModuleSwitch.IsOn = settings.BatteryMonitoringEnabled;
        WallpaperTintSwitch.IsOn = settings.WallpaperTintEnabled;
        var themeIndex = Array.IndexOf(ThemeModeValues, settings.ThemeMode);
        ThemeCombo.SelectedIndex = themeIndex >= 0 ? themeIndex : 0;
        HeadsetWriteSwitch.IsOn = settings.HeadsetWriteEnabled;
        MouseWriteSwitch.IsOn = settings.MouseWriteEnabled;
        RemoteWriteSwitch.IsOn = settings.RemoteWriteEnabled;
        NotifyConnectSwitch.IsOn = settings.NotifyOnConnect;
        NotifyLowBatterySwitch.IsOn = settings.NotifyOnLowBattery;
        NotifyWriteFailureSwitch.IsOn = settings.NotifyOnWriteFailure;
        LowBatteryThresholdCombo.SelectedItem = settings.LowBatteryThreshold;
        if (LowBatteryThresholdCombo.SelectedItem is null)
        {
            LowBatteryThresholdCombo.SelectedItem = 20;
        }

        var startupEnabled = StartupService.IsEnabled();
        StartWithWindowsSwitch.IsOn = startupEnabled;
        StartupStatus.Text = startupEnabled
            ? "当前用户启动项已开启。"
            : "当前用户启动项未开启。";

        SettingsPathText.Text = $"配置文件：{App.SettingsStore.SettingsPath}";
        _loading = false;
        RefreshBackupList();
    }

    private void RefreshBackupList(string? selectPath = null)
    {
        _backupFiles = AppBackupService.ListBackups().ToList();
        BackupCombo.ItemsSource = _backupFiles.Select(Path.GetFileName).ToList();
        var index = selectPath is null
            ? -1
            : _backupFiles.FindIndex(path => string.Equals(path, selectPath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            BackupCombo.SelectedIndex = index;
        }
    }

    private AppSettings CollectSettings() => new()
    {
        ThemeMode = ThemeCombo.SelectedIndex is >= 0 and < 3
            ? ThemeModeValues[ThemeCombo.SelectedIndex]
            : App.Settings.ThemeMode,
        WallpaperTintEnabled = WallpaperTintSwitch.IsOn,
        HeadsetModuleEnabled = HeadsetModuleSwitch.IsOn,
        MouseModuleEnabled = MouseModuleSwitch.IsOn,
        RemoteModuleEnabled = RemoteModuleSwitch.IsOn,
        BatteryMonitoringEnabled = MonitorModuleSwitch.IsOn,
        HeadsetWriteEnabled = HeadsetWriteSwitch.IsOn,
        MouseWriteEnabled = MouseWriteSwitch.IsOn,
        RemoteWriteEnabled = RemoteWriteSwitch.IsOn,
        NotifyOnConnect = NotifyConnectSwitch.IsOn,
        NotifyOnLowBattery = NotifyLowBatterySwitch.IsOn,
        NotifyOnWriteFailure = NotifyWriteFailureSwitch.IsOn,
        LowBatteryThreshold = LowBatteryThresholdCombo.SelectedItem is int threshold
            ? threshold
            : App.Settings.LowBatteryThreshold,
    };

    private void SaveSettings()
    {
        if (_loading)
        {
            return;
        }

        App.UpdateSettings(CollectSettings());
        LoadSettings();
    }

    private void Module_Toggled(object sender, RoutedEventArgs e) => SaveSettings();

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveSettings();

    private void WallpaperTint_Toggled(object sender, RoutedEventArgs e) => SaveSettings();

    private void Write_Toggled(object sender, RoutedEventArgs e) => SaveSettings();

    private void Notify_Toggled(object sender, RoutedEventArgs e) => SaveSettings();

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var result = StartupService.SetEnabled(StartWithWindowsSwitch.IsOn);
        StartupStatus.Text = result.Message;
        if (result.Ok)
        {
            return;
        }

        // 写入失败时回到注册表的实际状态，避免开关显示“已开启”但登录时不会启动。
        _loading = true;
        StartWithWindowsSwitch.IsOn = StartupService.IsEnabled();
        _loading = false;
    }

    private void Threshold_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SaveSettings();
    }

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            AppBackupService.BackupDirectory,
            $"薄荷外设备份-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await RunBusyAsync(ExportBackupButton, async () =>
        {
            await Task.Run(() => AppBackupService.CreateBackup(path));
            RefreshBackupList(path);
            BackupStatus.Text = $"备份已保存：{path}";
        });
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var index = BackupCombo.SelectedIndex;
        if (index < 0 || index >= _backupFiles.Count)
        {
            BackupStatus.Text = "请先在下拉框中选择一个备份文件。";
            return;
        }

        var path = _backupFiles[index];
        await RunBusyAsync(ImportBackupButton, async () =>
        {
            var settings = await Task.Run(() => AppBackupService.RestoreBackup(path));
            App.UpdateSettings(settings);
            App.RestartRemoteEngine();
            LoadSettings();
            BackupCombo.SelectedIndex = index;
            BackupStatus.Text = $"已导入：{Path.GetFileName(path)}；遥控器引擎已重启以读取新配置。";
        });
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!_resetArmed)
        {
            _resetArmed = true;
            ResetButton.Content = "再次点击确认恢复默认";
            BackupStatus.Text = "将把本应用设置恢复为默认值；遥控器映射文件不会删除。4 秒内再次点击确认。";
            _resetTimer?.Stop();
            _resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _resetTimer.Tick += (_, _) =>
            {
                _resetTimer?.Stop();
                _resetArmed = false;
                ResetButton.Content = "恢复默认设置";
                BackupStatus.Text = "已取消恢复默认。";
            };
            _resetTimer.Start();
            return;
        }

        _resetTimer?.Stop();
        _resetArmed = false;
        ResetButton.Content = "恢复默认设置";
        var startup = StartupService.SetEnabled(false);
        var defaults = App.SettingsStore.Reset();
        App.UpdateSettings(defaults);
        LoadSettings();
        BackupStatus.Text = startup.Ok
            ? "已恢复默认设置（遥控器映射文件保留，开机自启已关闭）。"
            : $"已恢复默认设置，但关闭开机自启失败：{startup.Message}";
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            AppBackupService.DiagnosticsDirectory,
            $"薄荷外设诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await RunBusyAsync(ExportDiagnosticsButton, async () =>
        {
            await Task.Run(() => AppBackupService.CreateDiagnostics(path));
            DiagnosticsStatus.Text = $"诊断包已保存：{path}";
        });
    }

    private void OpenBackup_Click(object sender, RoutedEventArgs e) => OpenFolder(AppBackupService.BackupDirectory);

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppBackupService.DiagnosticsDirectory);
        OpenFolder(AppBackupService.DiagnosticsDirectory);
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => OpenFolder(AppBackupService.ConfigDirectory);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppBackupService.LogsDirectory);

    private async Task RunBusyAsync(Button button, Func<Task> action)
    {
        button.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            BackupStatus.Text = $"操作失败：{ex.Message}";
            DiagnosticsStatus.Text = $"操作失败：{ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开资源管理器失败不弹层，由状态文本兜底。
        }
    }
}
