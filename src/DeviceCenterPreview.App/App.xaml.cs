using System.Threading;
using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Services;
using DeviceCenterPreview.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeviceCenterPreview.App;

public partial class App : Application
{
    private const string MutexName = "DeviceCenterPreview_SingleInstance";
    private const string ActivateEventName = "DeviceCenterPreview_Activate";

    public static IPeripheralService PeripheralService { get; private set; } = null!;

    public static AppSettings Settings { get; private set; } = AppSettings.Default;

    public static AppSettingsStore SettingsStore { get; } = new();

    public static Window? MainWindow { get; private set; }

    public static bool IsExiting { get; private set; }

    public static event EventHandler<AppNotification>? NotificationRaised;

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateSignal;
    private static RealPeripheralService? _realService;
    private static bool _trayHintShown;
    private Window? _window;
    private PreviewTaskbarPill? _taskbarPill;
    private TrayService? _tray;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) => LogCrash("XAML", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Task", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _taskbarPill?.Dispose();
            _tray?.Dispose();
            _realService?.Dispose();
            HeldKeyGuard.ReleaseModifiers("process exit");
            ReleaseSingleInstance();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!TryAcquireSingleInstance())
        {
            SignalExistingInstance();
            Environment.Exit(0);
            return;
        }

        // 上一次运行如果被强杀，语音热键可能留在按下状态；启动时先清一次。
        HeldKeyGuard.ReleaseModifiers("startup");

        StartActivationListener();

        Settings = SettingsStore.Load();
        if (SettingsStore.LastLoadWarning is { } warning)
        {
            LogCrash("Settings", new InvalidOperationException(warning));
        }

        // 程序被移动/重新发布后，修复开机自启项里的旧路径；未开启时不创建。
        StartupService.RepairPathIfEnabled();

        try
        {
            _realService = new RealPeripheralService(Settings);
            PeripheralService = _realService;
        }
        catch (Exception ex)
        {
            LogCrash("RealPeripheralService", ex);
            PeripheralService = new UnavailablePeripheralService($"设备服务初始化失败：{ex.Message}");
        }

        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();

        try
        {
            _taskbarPill = new PreviewTaskbarPill(PeripheralService, ShowMainWindow);
        }
        catch (Exception ex)
        {
            LogCrash("TaskbarPill", ex);
        }

        try
        {
            _tray = new TrayService(ShowMainWindow, ExitApplication);
        }
        catch (Exception ex)
        {
            LogCrash("Tray", ex);
        }
    }

    public static void HideMainWindow()
    {
        MainWindow?.AppWindow.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            Notify("薄荷外设仍在运行", "已最小化到系统托盘，双击托盘图标可重新打开。");
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        ShowWindow(_window);
    }

    private static void ShowWindow(Window window)
    {
        window.AppWindow.Show();
        if (window.AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        window.Activate();
    }

    public static void ExitApplication()
    {
        IsExiting = true;
        MainWindow = null;
        if (Application.Current is App app && app._window is not null)
        {
            app._window.Close();
            return;
        }

        Environment.Exit(0);
    }

    /// <summary>设置页/备份恢复的唯一入口：先落盘，再通知服务立即生效。</summary>
    public static void UpdateSettings(AppSettings settings)
    {
        var normalized = settings.Normalize();
        Settings = normalized;
        try
        {
            SettingsStore.Save(normalized);
        }
        catch (Exception ex)
        {
            Notify("设置保存失败", ex.Message, NotificationSeverity.Error);
            LogCrash("SettingsSave", ex);
        }

        if (MainWindow is MainWindow window)
        {
            window.ApplyTheme(normalized.ThemeMode);
            window.ApplyWallpaperTint();
        }

        try
        {
            PeripheralService.ApplySettings(normalized);
        }
        catch (Exception ex)
        {
            Notify("设置应用失败", ex.Message, NotificationSeverity.Error);
            LogCrash("SettingsApply", ex);
        }
    }

    public static void Notify(
        string title,
        string message,
        NotificationSeverity severity = NotificationSeverity.Informational)
    {
        var notification = new AppNotification(title, message, severity);
        var window = MainWindow;
        if (window is null)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => NotificationRaised?.Invoke(null, notification));
        if (Application.Current is App app)
        {
            app._tray?.ShowBalloon(title, message);
        }
    }

    /// <summary>导入备份后让 sidecar 重新读取遥控器配置；模块关闭时保持关闭。</summary>
    public static void RestartRemoteEngine()
    {
        if (_realService is null)
        {
            return;
        }

        _realService.RestartRemoteEngine();
    }

    // 旧实例退出时内核对象释放有延迟；直接退出会让用户以为重开也崩了。
    private static bool TryAcquireSingleInstance()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (createdNew)
            {
                _singleInstanceMutex = mutex;
                return true;
            }

            mutex.Dispose();
            if (attempt == 0)
            {
                SignalExistingInstance();
            }

            Thread.Sleep(250);
        }

        return false;
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch
        {
            // 旧实例已退出时事件不存在，属正常情况。
        }
    }

    private void StartActivationListener()
    {
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _activateSignal.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception)
                {
                    return;
                }

                var window = _window;
                if (window is null)
                {
                    continue;
                }

                window.DispatcherQueue.TryEnqueue(() =>
                {
                    ShowWindow(window);
                });
            }
        })
        {
            IsBackground = true,
            Name = "DeviceCenterActivate",
        };
        thread.Start();
    }

    private static void ReleaseSingleInstance()
    {
        var mutex = Interlocked.Exchange(ref _singleInstanceMutex, null);
        if (mutex is null)
        {
            return;
        }

        try { mutex.ReleaseMutex(); } catch { /* 非持有线程调用时忽略 */ }
        try { mutex.Dispose(); } catch { /* 忽略 */ }
    }

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeviceCenterPreview",
                "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {source}: {exception}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能反过来影响退出路径。
        }
    }
}
