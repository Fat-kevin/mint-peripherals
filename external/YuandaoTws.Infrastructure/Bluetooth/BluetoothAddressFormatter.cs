namespace YuandaoTws.Infrastructure.Bluetooth;

/// <summary>蓝牙地址 ulong MAC 与 "XX:XX:XX:XX:XX:XX" 字符串互转（供多类复用）。</summary>
public static class BluetoothAddressFormatter
{
    public static string Format(ulong address)
    {
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = (byte)(address >> (8 * (5 - i)));
        }

        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    public static bool TryParse(string? mac, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(mac))
        {
            return false;
        }

        var hex = mac.Replace(":", string.Empty).Replace("-", string.Empty);
        return hex.Length <= 16
            && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out address);
    }
}
