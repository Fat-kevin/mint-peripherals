using VigilWatt.Core.Devices;
using VigilWatt.Core.Settings;
using VigilWatt.Core.Storage;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var root = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DeviceCenterPreview");
Directory.CreateDirectory(root);

var settings = new AppSettings();
var history = new HistoryStore(Path.Combine(root, "probe.db"));
using var manager = new DeviceManager(settings, history);

manager.DeviceUpdated += status => Console.WriteLine($"UPDATED: {status.Kind} | {status.Name} | {status.BatteryPercent?.ToString() ?? "—"}%");
manager.Start();
Console.WriteLine("Scanning for 20 seconds...");
await Task.Delay(TimeSpan.FromSeconds(20));

Console.WriteLine();
Console.WriteLine("KIND       | SOURCE           | NAME                           | BATTERY | CHARGING     | PROTOCOL | ID");
Console.WriteLine("-----------|------------------|--------------------------------|---------|--------------|----------|-------------------");
foreach (var d in manager.Devices.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(
        $"{d.Kind,-10} | {d.Source,-16} | {d.Name,-30} | {(d.BatteryPercent?.ToString() ?? "—"),7} | {d.Charging,-12} | {d.Protocol,-8} | {d.Id}");
}

Console.WriteLine();
Console.WriteLine("=== Paired classic Bluetooth ===");
try
{
    var paired = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector());
    foreach (var info in paired)
    {
        var bt = await BluetoothDevice.FromIdAsync(info.Id);
        Console.WriteLine($"{info.Name,-32} | {bt?.ConnectionStatus,-10} | {bt?.BluetoothAddress:X12} | {info.Id}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"classic enum failed: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== BLE devices ===");
try
{
    var bleDevices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector());
    foreach (var info in bleDevices)
    {
        Console.WriteLine($"{info.Name,-32} | {info.Id}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"ble enum failed: {ex.Message}");
}
