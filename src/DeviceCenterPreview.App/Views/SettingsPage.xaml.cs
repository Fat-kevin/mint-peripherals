using System.Diagnostics;
using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceCenterPreview.App.Views;

public sealed partial class SettingsPage : Page, IThemeAware
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

    private static readonly string[] TaskbarStyleValues =
    [
        "icon+percent",
        "percent",
        "icon",
    ];

    private static readonly string[] TaskbarStyleLabels =
    [
        "图标 + 百分比",
        "仅百分比",
        "仅图标",
    ];

    private static readonly string[] TaskbarPositionValues =
    [
        "auto",
        "corner",
        "tray",
        "start",
    ];

    private static readonly string[] TaskbarPositionLabels =
    [
        "自动（推荐）",
        "左下角",
        "通知区域左侧",
        "任务栏左侧",
    ];

    private static readonly string[] TaskbarFontSizeValues =
    [
        "small",
        "medium",
        "large",
    ];

    private static readonly string[] TaskbarFontSizeLabels =
    [
        "小",
        "中",
        "大",
    ];

    // 任务栏条右键「任务栏显示设置」需要在不改主窗口导航实现的前提下切到「外观」分类。
    private static SettingsPage? _instance;
    private static int _pendingCategory = -1;

    private readonly FrameworkElement[] _categories;
    private readonly Button[] _tabs;
    private int _category;
    private bool _loading;
    private bool _resetArmed;
    private DispatcherTimer? _resetTimer;
    private List<string> _backupFiles = new();

    public SettingsPage()
    {
        InitializeComponent();
        _categories = [Category0, Category1, Category2, Category3, Category4, Category5];
        _tabs = [Tab0, Tab1, Tab2, Tab3, Tab4, Tab5];
        ThemeCombo.ItemsSource = ThemeModeLabels;
        TaskbarStyleCombo.ItemsSource = TaskbarStyleLabels;
        TaskbarPositionCombo.ItemsSource = TaskbarPositionLabels;
        TaskbarFontSizeCombo.ItemsSource = TaskbarFontSizeLabels;
        LowBatteryThresholdCombo.ItemsSource = new[] { 0, 10, 15, 20, 25, 30, 40, 50, 60, 80 };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PageScroll.SizeChanged += (_, _) => PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
        ApplyCategory(0);
    }

    /// <summary>供 App 在任务栏条右键「任务栏显示设置」时调用：切到指定分类（0=外观）。</summary>
    public static void RequestCategory(int category)
    {
        var page = _instance;
        if (page is not null)
        {
            page.ApplyCategory(category);
            return;
        }

        _pendingCategory = category;
    }

    private void Category_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var selected))
        {
            ApplyCategory(selected);
        }
    }

    private void ApplyCategory(int selected)
    {
        if (_categories is null)
        {
            return;
        }

        _category = Math.Clamp(selected, 0, _categories.Length - 1);
        for (var index = 0; index < _categories.Length; index++)
        {
            _categories[index].Visibility = index == _category ? Visibility.Visible : Visibility.Collapsed;
        }

        var ink = AppTheme.GetBrush(this, "AppInkBrush");
        var onInk = AppTheme.GetBrush(this, "AppOnInkBrush");
        var idle = AppTheme.GetBrush(this, "AppTextSecondaryBrush");
        for (var index = 0; index < _tabs.Length; index++)
        {
            var active = index == _category;
            _tabs[index].Background = active ? ink : null;
            _tabs[index].Foreground = active ? onInk : idle;
        }

        PageScroll.ChangeView(null, 0, null, true);
    }

    public void OnAppThemeChanged() => ApplyCategory(_category);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _instance = this;
        if (_pendingCategory >= 0)
        {
            ApplyCategory(_pendingCategory);
            _pendingCategory = -1;
        }

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
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

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

        TaskbarStripSwitch.IsOn = settings.TaskbarStripEnabled;
        var taskbarStyleIndex = Array.IndexOf(TaskbarStyleValues, settings.TaskbarStripStyle);
        TaskbarStyleCombo.SelectedIndex = taskbarStyleIndex >= 0 ? taskbarStyleIndex : 0;
        var taskbarFontSizeIndex = Array.IndexOf(TaskbarFontSizeValues, settings.TaskbarStripFontSize);
        TaskbarFontSizeCombo.SelectedIndex = taskbarFontSizeIndex >= 0 ? taskbarFontSizeIndex : 1;
        var taskbarPositionIndex = Array.IndexOf(TaskbarPositionValues, settings.TaskbarStripPosition);
        TaskbarPositionCombo.SelectedIndex = taskbarPositionIndex >= 0 ? taskbarPositionIndex : 0;
        TaskbarBackgroundSwitch.IsOn = settings.TaskbarStripBackground;
        TaskbarSleepingSwitch.IsOn = settings.TaskbarStripShowSleeping;
        OtherBatteryDevicesSwitch.IsOn = settings.ShowOtherBatteryDevices;

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
        TaskbarStripEnabled = TaskbarStripSwitch.IsOn,
        TaskbarStripStyle = TaskbarStyleCombo.SelectedIndex is >= 0 and < 3
            ? TaskbarStyleValues[TaskbarStyleCombo.SelectedIndex]
            : App.Settings.TaskbarStripStyle,
        TaskbarStripFontSize = TaskbarFontSizeCombo.SelectedIndex is >= 0 and < 3
            ? TaskbarFontSizeValues[TaskbarFontSizeCombo.SelectedIndex]
            : App.Settings.TaskbarStripFontSize,
        TaskbarStripPosition = TaskbarPositionCombo.SelectedIndex is >= 0 and < 4
            ? TaskbarPositionValues[TaskbarPositionCombo.SelectedIndex]
            : App.Settings.TaskbarStripPosition,
        TaskbarStripBackground = TaskbarBackgroundSwitch.IsOn,
        TaskbarStripShowSleeping = TaskbarSleepingSwitch.IsOn,
        ShowOtherBatteryDevices = OtherBatteryDevicesSwitch.IsOn,
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

    private void Taskbar_Toggled(object sender, RoutedEventArgs e) => SaveSettings();

    private void Taskbar_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveSettings();

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
