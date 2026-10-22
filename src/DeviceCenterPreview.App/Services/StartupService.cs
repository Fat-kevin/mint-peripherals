using Microsoft.Win32;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 当前用户的开机自启项（HKCU\...\Run）。不依赖安装包身份，也不需要管理员权限。
/// 注册表是实际状态的唯一来源；settings.json 不重复保存，避免“界面开着、系统没开”。
/// </summary>
internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeviceCenterPreview";

    public readonly record struct ApplyResult(bool Ok, string Message);

    public static bool IsEnabled() => !string.IsNullOrWhiteSpace(ReadCommand());

    public static string? ReadCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"startup read failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static ApplyResult SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return new ApplyResult(false, "无法打开当前用户的启动项注册表。");
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Info("startup disabled");
                return new ApplyResult(true, "已关闭开机自启。");
            }

            var executable = ResolveExecutable();
            if (string.IsNullOrWhiteSpace(executable))
            {
                return new ApplyResult(false, "无法确定当前程序路径，开机自启未开启。");
            }

            key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            AppLog.Info($"startup enabled: {executable}");
            return new ApplyResult(true, "已开启开机自启。");
        }
        catch (Exception ex)
        {
            AppLog.Error("startup write failed", ex);
            return new ApplyResult(false, $"开机自启设置失败：{ex.Message}");
        }
    }

    /// <summary>程序被移动/重新发布后，把旧路径修到当前 exe；未开启时不做任何事。</summary>
    public static void RepairPathIfEnabled()
    {
        var command = ReadCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var executable = ResolveExecutable();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var expected = $"\"{executable}\"";
        if (string.Equals(command.Trim(), expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var result = SetEnabled(true);
        if (!result.Ok)
        {
            AppLog.Warn($"startup path repair failed: {result.Message}");
        }
    }

    /// <summary>
    /// 通过 `dotnet run` 启动时 ProcessPath 可能是 dotnet.exe；优先使用同目录下的
    /// apphost，保证写入 Run 的是可双击运行的本应用 exe。
    /// </summary>
    private static string? ResolveExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            File.Exists(processPath) &&
            !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var appHost = Path.Combine(AppContext.BaseDirectory, "DeviceCenterPreview.App.exe");
        if (File.Exists(appHost))
        {
            return appHost;
        }

        return string.IsNullOrWhiteSpace(processPath) ? null : processPath;
    }
}
