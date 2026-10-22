using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Services;
using Xunit;

namespace DeviceCenterPreview.Core.Tests;

public sealed class MockPeripheralServiceTests
{
    [Fact]
    public void Headset_snapshot_has_three_components_and_min_of_ears()
    {
        var service = new MockPeripheralService();

        var snapshot = service.GetSnapshot(DeviceKind.Headset);

        Assert.True(snapshot.IsConnected);
        Assert.Equal(3, snapshot.Components.Count);
        Assert.Equal(80, snapshot.BatteryPercent);
        Assert.Equal(ChargingState.Charging, snapshot.Charging);
    }

    [Fact]
    public void Selecting_mouse_changes_snapshot()
    {
        var service = new MockPeripheralService();

        service.SelectDevice(DeviceKind.Mouse, "mouse:rapoo:vt3");
        var snapshot = service.GetSnapshot(DeviceKind.Mouse);

        Assert.Equal("mouse:rapoo:vt3", snapshot.DeviceId);
        Assert.Equal(45, snapshot.BatteryPercent);
    }

    [Fact]
    public async Task Updating_headset_controls_raises_changed_and_updates_status()
    {
        var service = new MockPeripheralService();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await service.UpdateHeadsetControlsAsync(HeadsetControls.Default with { Anc = AncMode.Deep });

        Assert.Equal(1, raised);
        Assert.Contains(PeripheralDisplayNames.Anc(AncMode.Deep), service.GetSnapshot(DeviceKind.Headset).StatusText);
    }

    [Fact]
    public void Selecting_unknown_device_throws()
    {
        var service = new MockPeripheralService();

        Assert.Throws<ArgumentException>(() => service.SelectDevice(DeviceKind.Mouse, "nope"));
    }

    [Fact]
    public void Remote_controls_are_per_device()
    {
        var service = new MockPeripheralService();

        var rc003 = service.GetRemoteControls();
        service.SelectDevice(DeviceKind.Remote, "remote:rc001");
        var rc001 = service.GetRemoteControls();

        Assert.Equal("remote:rc003", rc003.DeviceId);
        Assert.Equal("remote:rc001", rc001.DeviceId);
        Assert.NotSame(rc003, rc001);
    }
}
