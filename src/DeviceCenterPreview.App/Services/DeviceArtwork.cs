using System.Text.RegularExpressions;

namespace DeviceCenterPreview.App.Services;

/// <summary>功 能： 按设备名匹配 OpenMouse 的罗技产品图；未知型号返回 null 由 UI 回退图标。</summary>
internal static class DeviceArtwork
{
    private static readonly (Regex Pattern, string File)[] LogitechRules =
    [
        (new Regex(@"g502\s*x\s*plus", RegexOptions.IgnoreCase), "logitech-g502-x-plus.png"),
        (new Regex(@"g502\s*x", RegexOptions.IgnoreCase), "logitech-g502-x.png"),
        (new Regex(@"\bg502\b", RegexOptions.IgnoreCase), "logitech-g502.png"),
        (new Regex(@"\bg703\b", RegexOptions.IgnoreCase), "logitech-g703.png"),
        (new Regex(@"\bpro\s*x\s*wireless\b", RegexOptions.IgnoreCase), "logitech-pro-x-superlight-2c.png"),
        (new Regex(@"superstrike.*x\s*3|x\s*3.*superstrike", RegexOptions.IgnoreCase), "logitech-pro-x3-superstrike.png"),
        (new Regex(@"superstrike", RegexOptions.IgnoreCase), "logitech-pro-x2-superstrike.png"),
        (new Regex(@"superlight", RegexOptions.IgnoreCase), "logitech-pro-x-superlight-2c.png"),
        (new Regex(@"\bmx\s*master\s*3s\b", RegexOptions.IgnoreCase), "logitech-mx-master-3s.png"),
        (new Regex(@"\bg(?:102|203)\b", RegexOptions.IgnoreCase), "logitech-g203.png"),
        (new Regex(@"\bg303\b", RegexOptions.IgnoreCase), "logitech-g303.png"),
        (new Regex(@"\bg402\b", RegexOptions.IgnoreCase), "logitech-g402.png"),
        (new Regex(@"\bg403\b", RegexOptions.IgnoreCase), "logitech-g403.png"),
        (new Regex(@"\bg903\b", RegexOptions.IgnoreCase), "logitech-g903.png"),
        (new Regex(@"\bg30[45]\b", RegexOptions.IgnoreCase), "logitech-g305.png"),
        (new Regex(@"\bg309\b", RegexOptions.IgnoreCase), "logitech-g309.png"),
        (new Regex(@"\bg\s*pro\s*2\b", RegexOptions.IgnoreCase), "logitech-g-pro-2.png"),
        (new Regex(@"\bg\s*pro\s*wireless\b", RegexOptions.IgnoreCase), "logitech-g-pro.png"),
        (new Regex(@"\bg\s*pro\b", RegexOptions.IgnoreCase), "logitech-g-pro.png"),
        (new Regex(@"\bmx\s*anywhere\s*3\b", RegexOptions.IgnoreCase), "logitech-mx-anywhere-3.png"),
        (new Regex(@"\bmx\s*ergo\b", RegexOptions.IgnoreCase), "logitech-mx-ergo-s.png"),
    ];

    public static string? ForMouse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var (pattern, file) in LogitechRules)
        {
            if (pattern.IsMatch(name))
            {
                return "Assets/Devices/Logitech/" + file;
            }
        }

        return null;
    }
}
