using System.Text;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 功 能： 轻量文件日志，按天滚动，用于定位设备发现/SPP 连接/写入问题。
/// 2026/10/19 15:31:08 xliu
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeviceCenterPreview",
        "logs");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var file = Path.Combine(DirectoryPath, $"app-{DateTimeOffset.Now:yyyyMMdd}.log");
                var builder = new StringBuilder()
                    .Append('[').Append(DateTimeOffset.Now.ToString("HH:mm:ss.fff")).Append("] ")
                    .Append(level).Append(' ').Append(message);
                if (exception is not null)
                {
                    builder.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
                }

                builder.AppendLine();
                File.AppendAllText(file, builder.ToString());
            }
        }
        catch
        {
            // 日志失败不能影响设备功能。
        }
    }
}
