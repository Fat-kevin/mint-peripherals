using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceCenterPreview.App.Views;

public sealed partial class HeadsetPage : Page
{
    private readonly RadioButton[] _ancModes;
    private readonly RadioButton[] _eqModes;
    private bool _loading;
    private bool _controlsAvailable;
    private CancellationTokenSource? _findCts;
    private bool _findRinging;

    public HeadsetPage()
    {
        InitializeComponent();
        _ancModes = BuildModes(AncModes, "NoiseMode", ["关闭", "通透", "普通", "深度", "实验性降噪", "风噪模式"], 3);
        _eqModes = BuildModes(EqModes, "SoundPreset", ["悔恨之泪", "均衡中正", "欧美澎湃", "真律还原", "游戏优化", "细腻佳音", "温婉人声"], 3);
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
        TitleText.Text = snapshot.IsConnected ? snapshot.DisplayName : "耳机";
        SubtitleText.Text = snapshot.StatusText;
        HeadsetFirmwareText.Text = snapshot.Firmware is null
            ? "固件 —"
            : $"固件 {snapshot.Firmware}";
        ControlBannerText.Text = snapshot.ControlHint ?? "";
        ControlBanner.Visibility = string.IsNullOrWhiteSpace(snapshot.ControlHint) ||
                                   snapshot.ControlHint == snapshot.StatusText ||
                                   (snapshot.ControlsAvailable && snapshot.WriteAllowed)
            ? Visibility.Collapsed
            : Visibility.Visible;

        SetComponent(snapshot, "left", LeftReadout, LeftCharging);
        SetComponent(snapshot, "right", RightReadout, RightCharging);
        SetComponent(snapshot, "case", CaseReadout, CaseCharging);

        _controlsAvailable = snapshot.ControlsAvailable && snapshot.WriteAllowed;
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
        UpdateModes(_ancModes, controls.IsKnown ? (int)controls.Anc : -1);
        UpdateModes(_eqModes, controls.IsKnown ? (int)controls.Eq : -1);
        GameModeSwitch.IsOn = controls.IsKnown && controls.GameMode;
        LowLatencySwitch.IsOn = controls.IsKnown && controls.LowLatency;
        DualDeviceSwitch.IsOn = controls.IsKnown && controls.DualDevice;
        InEarSwitch.IsOn = controls.IsKnown && controls.InEarDetection;
        WindNoiseSwitch.IsOn = controls.IsKnown && controls.WindNoise;
        _loading = false;
    }

    private RadioButton[] BuildModes(Grid grid, string groupName, string[] labels, int columns)
    {
        for (var column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (var row = 0; row * columns < labels.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var buttons = new RadioButton[labels.Length];
        for (var index = 0; index < labels.Length; index++)
        {
            var button = new RadioButton
            {
                Content = labels[index],
                GroupName = groupName,
                Style = (Style)Application.Current.Resources["AppModeButtonStyle"],
            };
            button.Checked += Toggle_Changed;
            Grid.SetRow(button, index / columns);
            Grid.SetColumn(button, index % columns);
            grid.Children.Add(button);
            buttons[index] = button;
        }

        return buttons;
    }

    private void UpdateModes(RadioButton[] buttons, int selectedIndex)
    {
        for (var index = 0; index < buttons.Length; index++)
        {
            buttons[index].IsEnabled = _controlsAvailable;
            buttons[index].IsChecked = index == selectedIndex;
        }
    }

    private static int SelectedMode(RadioButton[] buttons, int fallback)
    {
        var selection = Array.FindIndex(buttons, button => button.IsChecked == true);
        return selection >= 0 ? selection : fallback;
    }

    private static void SetComponent(PeripheralSnapshot snapshot, string key, TextBlock readout, TextBlock caption)
    {
        var component = snapshot.Components.FirstOrDefault(item => item.Key == key);
        readout.Text = component?.Percent is int percent ? $"{percent}%" : "—";
        caption.Text = component?.Charging switch
        {
            ChargingState.Charging => "充电中",
            ChargingState.Full => "已充满",
            ChargingState.Discharging => "使用中",
            _ => "",
        };
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e) => Save();

    private async void Save()
    {
        if (_loading || !_controlsAvailable)
        {
            return;
        }

        var controls = new HeadsetControls(
            (AncMode)SelectedMode(_ancModes, (int)AncMode.Off),
            (EqPreset)SelectedMode(_eqModes, (int)EqPreset.Balanced),
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

