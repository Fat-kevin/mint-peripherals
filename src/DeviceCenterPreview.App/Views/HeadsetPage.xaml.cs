using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceCenterPreview.App.Views;

public sealed partial class HeadsetPage : Page
{
    private bool _loading;
    private bool _controlsAvailable;
    private CancellationTokenSource? _findCts;
    private bool _findRinging;

    public HeadsetPage()
    {
        InitializeComponent();
        AncCombo.ItemsSource = new[] { "关闭", "通透", "普通", "深度", "实验性降噪", "风噪模式" };
        EqCombo.ItemsSource = new[] { "悔恨之泪", "均衡中正", "欧美澎湃", "真律还原", "游戏优化", "细腻佳音", "温婉人声" };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PageScroll.SizeChanged += (_, _) => PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed += OnServiceChanged;
        PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
        Refresh();
        ResetScrollToTop();
    }

    private void ResetScrollToTop()
    {
        PageScroll.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(() => PageScroll.ChangeView(null, 0, null, true));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed -= OnServiceChanged;
        _findCts?.Cancel();
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        var snapshot = App.PeripheralService.GetSnapshot(DeviceKind.Headset);
        TitleText.Text = $"耳机 · {snapshot.DisplayName}";
        SubtitleText.Text = snapshot.Firmware is null
            ? snapshot.StatusText
            : $"{snapshot.StatusText} · 固件 {snapshot.Firmware}";
        HeadsetStateText.Text = snapshot.IsConnected ? "已连接" : "未连接";
        HeadsetFirmwareText.Text = snapshot.Firmware is null
            ? "固件 —"
            : $"固件 {snapshot.Firmware}";
        ControlBannerText.Text = snapshot.ControlHint ?? "";
        ControlBanner.Visibility = snapshot.ControlsAvailable && snapshot.WriteAllowed
            ? Visibility.Collapsed
            : Visibility.Visible;

        SetComponent(LeftRing, snapshot, "left");
        SetComponent(RightRing, snapshot, "right");
        SetComponent(CaseRing, snapshot, "case");

        _controlsAvailable = snapshot.ControlsAvailable && snapshot.WriteAllowed;
        AncCombo.IsEnabled = _controlsAvailable;
        EqCombo.IsEnabled = _controlsAvailable;
        GameModeSwitch.IsEnabled = _controlsAvailable;
        LowLatencySwitch.IsEnabled = _controlsAvailable;
        DualDeviceSwitch.IsEnabled = _controlsAvailable;
        InEarSwitch.IsEnabled = _controlsAvailable;
        WindNoiseSwitch.IsEnabled = _controlsAvailable;
        FindButton.IsEnabled = _controlsAvailable;
        if (!_controlsAvailable)
        {
            ResetFind(clearStatus: true);
        }

        var controls = App.PeripheralService.GetHeadsetControls();
        _loading = true;
        AncCombo.SelectedIndex = controls.IsKnown ? (int)controls.Anc : -1;
        EqCombo.SelectedIndex = controls.IsKnown ? (int)controls.Eq : -1;
        GameModeSwitch.IsOn = controls.IsKnown && controls.GameMode;
        LowLatencySwitch.IsOn = controls.IsKnown && controls.LowLatency;
        DualDeviceSwitch.IsOn = controls.IsKnown && controls.DualDevice;
        InEarSwitch.IsOn = controls.IsKnown && controls.InEarDetection;
        WindNoiseSwitch.IsOn = controls.IsKnown && controls.WindNoise;
        _loading = false;
    }

    private void SetComponent(
        Controls.BatteryRing ring,
        PeripheralSnapshot snapshot,
        string key)
    {
        var component = snapshot.Components.FirstOrDefault(c => c.Key == key);
        var percent = component?.Percent;
        ring.BatteryValue = percent ?? 0;
        ring.Charging = component?.Charging == ChargingState.Charging;
        ring.RingBrush = (percent ?? 0) switch
        {
            <= 15 => ResourceBrush("AppDangerBrush"),
            <= 35 => ResourceBrush("AppWarningBrush"),
            _ => ResourceBrush("AppHeadsetBrush"),
        };

        var chargingText = component?.Charging switch
        {
            ChargingState.Charging => "充电中",
            ChargingState.Full => "已充满",
            ChargingState.Discharging => "使用中",
            _ => "未知",
        };
        var caption = key switch
        {
            "left" => LeftCharging,
            "right" => RightCharging,
            _ => CaseCharging,
        };
        caption.Text = percent is null ? "—" : chargingText;
    }

    private Microsoft.UI.Xaml.Media.Brush ResourceBrush(string key) =>
        Services.AppTheme.GetBrush(this, key);

    private void Anc_SelectionChanged(object sender, SelectionChangedEventArgs e) => Save();

    private void Eq_SelectionChanged(object sender, SelectionChangedEventArgs e) => Save();

    private void Toggle_Changed(object sender, RoutedEventArgs e) => Save();

    private async void Save()
    {
        if (_loading || !_controlsAvailable)
        {
            return;
        }

        var controls = new HeadsetControls(
            AncCombo.SelectedIndex >= 0 ? (AncMode)AncCombo.SelectedIndex : AncMode.Off,
            EqCombo.SelectedIndex >= 0 ? (EqPreset)EqCombo.SelectedIndex : EqPreset.Balanced,
            GameModeSwitch.IsOn,
            LowLatencySwitch.IsOn,
            DualDeviceSwitch.IsOn,
            InEarSwitch.IsOn,
            WindNoiseSwitch.IsOn);

        var result = await App.PeripheralService.UpdateHeadsetControlsAsync(controls);
        SaveStatus.Text = result.Ok ? "已发送" : result.Message ?? "写入失败";
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (!_controlsAvailable)
        {
            return;
        }

        if (_findCts is not null)
        {
            _findCts.Cancel();
            return;
        }

        if (_findRinging)
        {
            await StopFindAsync();
            return;
        }

        // 防呆：首次点击只开始 5 秒倒计时，不立即鸣响；倒计时内再点一次即可取消。
        var cts = new CancellationTokenSource();
        _findCts = cts;
        try
        {
            for (var n = 5; n >= 1; n--)
            {
                SaveStatus.Text = $"{n} 秒后开始鸣响，点“取消查找”可立即停止";
                FindButton.Content = $"取消查找（{n}）";
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }

            await App.PeripheralService.FindHeadsetAsync(true);
            _findRinging = true;
            SaveStatus.Text = "耳机正在鸣响";
            FindButton.Content = "停止鸣响";
        }
        catch (OperationCanceledException)
        {
            SaveStatus.Text = "已取消查找";
        }
        catch (Exception ex)
        {
            SaveStatus.Text = $"查找失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_findCts, cts))
            {
                _findCts = null;
                cts.Dispose();
                if (!_findRinging)
                {
                    FindButton.Content = "查找耳机";
                }
            }
        }
    }

    private async Task StopFindAsync()
    {
        try
        {
            await App.PeripheralService.FindHeadsetAsync(false);
            SaveStatus.Text = "已发送停止鸣响";
        }
        finally
        {
            _findRinging = false;
            FindButton.Content = "查找耳机";
        }
    }

    private void ResetFind(bool clearStatus)
    {
        _findCts?.Cancel();
        _findCts = null;
        _findRinging = false;
        FindButton.Content = "查找耳机";
        if (clearStatus)
        {
            SaveStatus.Text = "";
        }
    }
}

