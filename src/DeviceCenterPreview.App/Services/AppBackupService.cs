using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Settings;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 配置备份/恢复/诊断包。所有文件都走固定白名单；导入包先解压到临时目录并校验
/// manifest，避免把任意文件写进用户配置目录。
/// </summary>
internal static class AppBackupService
{
    private const string BackupKind = "device-center-preview-backup";
    private const string DiagnosticsKind = "device-center-preview-diagnostics";

    private static readonly HashSet<string> AllowedBackupEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "manifest.json",
        "settings.json",
        "remote/settings.json",
        "remote/button-mappings.json",
        "remote/voice-hold-hotkey.json",
        "remote/other-voice-hotkey.json",
    };

    private static readonly string[] RemoteBackupFiles =
    [
        "settings.json",
        "button-mappings.json",
        "voice-hold-hotkey.json",
        "other-voice-hotkey.json",
    ];

    public static string ConfigDirectory => App.SettingsStore.RootDirectory;

    public static string LogsDirectory => Path.Combine(ConfigDirectory, "logs");

    public static string BackupDirectory => Path.Combine(ConfigDirectory, "backups");

    public static string DiagnosticsDirectory => Path.Combine(ConfigDirectory, "diagnostics");

    public static IReadOnlyList<string> ListBackups()
    {
        Directory.CreateDirectory(BackupDirectory);
        return Directory
            .EnumerateFiles(BackupDirectory, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    public static string RemoteConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "app.getsayall.remote-mic.windows");

    public static void CreateBackup(string targetPath)
    {
        var temp = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "remote"));
            File.WriteAllText(
                Path.Combine(temp, "settings.json"),
                AppSettingsStore.Serialize(App.Settings));

            foreach (var name in RemoteBackupFiles)
            {
                var source = Path.Combine(RemoteConfigDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(temp, "remote", name), overwrite: true);
                }
            }

            var manifest = new
            {
                kind = BackupKind,
                schemaVersion = AppSettings.CurrentSchemaVersion,
                createdAt = DateTimeOffset.Now,
                appVersion = typeof(App).Assembly.GetName().Version?.ToString(),
            };
            File.WriteAllText(
                Path.Combine(temp, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            ZipFile.CreateFromDirectory(temp, targetPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    public static AppSettings RestoreBackup(string sourcePath)
    {
        var temp = CreateTempDirectory();
        try
        {
            ZipFile.ExtractToDirectory(sourcePath, temp, overwriteFiles: true);
            foreach (var entry in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(temp, entry).Replace('\\', '/');
                if (!AllowedBackupEntries.Contains(relative))
                {
                    throw new InvalidDataException($"备份包含未知文件：{relative}");
                }
            }

            var manifestPath = Path.Combine(temp, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException("备份缺少 manifest.json。");
            }

            using (var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
                if (!manifest.RootElement.TryGetProperty("kind", out var kind) ||
                    kind.GetString() != BackupKind)
                {
                    throw new InvalidDataException("这不是“薄荷外设”的配置备份。");
                }
            }

            var settingsPath = Path.Combine(temp, "settings.json");
            if (!File.Exists(settingsPath))
            {
                throw new InvalidDataException("备份缺少 settings.json。");
            }

            var settings = AppSettingsStore.Parse(File.ReadAllText(settingsPath));

            Directory.CreateDirectory(RemoteConfigDirectory);
            foreach (var name in RemoteBackupFiles)
            {
                var source = Path.Combine(temp, "remote", name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(RemoteConfigDirectory, name), overwrite: true);
                }
            }

            return settings;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    public static void CreateDiagnostics(string targetPath)
    {
        var temp = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "logs"));
            var summary = BuildSummary();
            File.WriteAllText(
                Path.Combine(temp, "summary.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(
                Path.Combine(temp, "settings.json"),
                AppSettingsStore.Serialize(App.Settings));

            if (Directory.Exists(LogsDirectory))
            {
                foreach (var log in Directory.EnumerateFiles(LogsDirectory, "*.log"))
                {
                    var text = File.ReadAllText(log);
                    File.WriteAllText(Path.Combine(temp, "logs", Path.GetFileName(log)), Redact(text));
                }
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            ZipFile.CreateFromDirectory(temp, targetPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    private static object BuildSummary()
    {
        var devices = new List<object>();
        foreach (var kind in Enum.GetValues<DeviceKind>())
        {
            var snapshot = App.PeripheralService.GetSnapshot(kind);
            devices.Add(new
            {
                kind = kind.ToString(),
                name = snapshot.DisplayName,
                connected = snapshot.IsConnected,
                battery = snapshot.BatteryPercent,
                charging = snapshot.Charging.ToString(),
                controlsAvailable = snapshot.ControlsAvailable,
                writeAllowed = snapshot.WriteAllowed,
                status = snapshot.StatusText,
                firmware = snapshot.Firmware,
            });
        }

        var remote = App.PeripheralService.GetRemoteControls();
        return new
        {
            kind = DiagnosticsKind,
            createdAt = DateTimeOffset.Now,
            appVersion = typeof(App).Assembly.GetName().Version?.ToString(),
            os = Environment.OSVersion.ToString(),
            settingsFile = "settings.json",
            settings = App.Settings,
            devices,
            remote = new
            {
                connected = remote.EngineConnected,
                voiceReady = remote.VoiceReady,
                rawInputPhase = remote.RawInputPhase,
                matchedDeviceCount = remote.MatchedDeviceCount,
                gateActive = remote.GateActive,
                listenerActive = remote.ListenerActive,
                mappingsEnabled = remote.MappingsEnabled,
            },
        };
    }

    private static string Redact(string text)
    {
        // 只保留可复现问题所需的最小信息：设备地址、用户名路径、HID 路径一律打码。
        text = Regex.Replace(
            text,
            @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b",
            "<mac>");
        text = Regex.Replace(text, @"[A-Za-z]:\\Users\\[^\\\r\n]+", "%USERPROFILE%");
        text = Regex.Replace(text, @"\\\\\?\\hid#[^\s""']+", "<hid-path>");
        return text;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DeviceCenterPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响主流程。
        }
    }
}
