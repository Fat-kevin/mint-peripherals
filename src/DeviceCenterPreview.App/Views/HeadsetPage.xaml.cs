using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeviceCenterPreview.App.Views;

public sealed partial class HeadsetPage : Page, IThemeAware
{
    private static readonly string[] AncNames =
        ["关闭", "通透", "普通", "深度", "实验性降噪", "风噪模式"];

    private static readonly string[] AncDescriptions =
        ["不处理环境音", "听见周围声音", "日常均衡降噪", "强力隔绝环境", "实验室算法", "抑制户外风噪"];

    private static readonly string[] EqNames =
        ["悔恨之泪", "均衡中正", "欧美澎湃", "真律还原", "游戏优化", "细腻佳音", "温婉人声"];

    private readonly List<Button> _ancButtons = [];
    private readonly List<TextBlock> _ancLabels = [];
    private readonly List<TextBlock> _ancNotes = [];
    private readonly List<Button> _eqButtons = [];
    private readonly List<TextBlock> _eqLabels = [];

    private bool _loading;
    private bool _controlsAvailable;
    private int _ancIndex = -1;
    private int _eqIndex = -1;
    private CancellationTokenSource? _findCts;
    private bool _findRinging;

    private bool _entrancePlayed;

    public HeadsetPage()
    {
        InitializeComponent();
        BuildTiles();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PageScroll.SizeChanged += (_, _) => PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
    }

    private void BuildTiles()
    {
        var modeStyle = (Style)Application.Current.Resources["BenchModeButtonStyle"];

        for (var i = 0; i < AncNames.Length; i++)
        {
            var name = new TextBlock
            {
                Text = AncNames[i],
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var note = new TextBlock
            {
                Text = AncDescriptions[i],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(name);
            stack.Children.Add(note);

            var button = new Button
            {
                Content = stack,
                Style = modeStyle,
                Height = 50,
                Tag = i,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            button.Click += Anc_Click;
            Grid.SetRow(button, i / 3);
            Grid.SetColumn(button, i % 3);
            AncGrid.Children.Add(button);
            _ancButtons.Add(button);
            _ancLabels.Add(name);
            _ancNotes.Add(note);
        }

        for (var i = 0; i < EqNames.Length; i++)
        {
            var label = new TextBlock
            {
                Text = EqNames[i],
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var button = new Button
            {
                Content = label,
                Style = modeStyle,
                Height = 32,
                Tag = i,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            button.Click += Eq_Click;
            Grid.SetRow(button, i / 4);
            Grid.SetColumn(button, i % 4);
            EqGrid.Children.Add(button);
            _eqButtons.Add(button);
            _eqLabels.Add(label);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed += OnServiceChanged;
        PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
        Refresh();
        PageScroll.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => PageScroll.ChangeView(null, 0, null, true));
        if (!_entrancePlayed)
        {
            _entrancePlayed = true;
            UiMotion.PlayEntrance(PageContent);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _entrancePlayed = false;
        App.PeripheralService.Changed -= OnServiceChanged;
        _findCts?.Cancel();
    }

    public void OnAppThemeChanged() => Refresh();

    private void OnServiceChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var snapshot = App.PeripheralService.GetSnapshot(DeviceKind.Headset);
        PageTitleText.Text = snapshot.DisplayName;
        PageMetaText.Text = snapshot.Firmware is { Length: > 0 } firmware
            ? $"SPP 已接入 · 固件 {firmware}"
            : snapshot.StatusText;

        ChipConn.Text = snapshot.IsConnected ? "已连接" : "未连接";
        ConnDot.Fill = AppTheme.GetBrush(this,
            snapshot.IsConnected ? "AppSuccessBrush" : "AppOfflineBrush");
        ChipFirmware.Text = snapshot.Firmware ?? "—";
        ChipWrite.Text = snapshot.WriteAllowed ? "允许" : "已关闭";

        SetComponent(snapshot, "left", LeftBarFill, LeftBarRest, LeftPercent, LeftCharging);
        SetComponent(snapshot, "right", RightBarFill, RightBarRest, RightPercent, RightCharging);
        SetComponent(snapshot, "case", CaseBarFill, CaseBarRest, CasePercent, CaseCharging);

        ControlBannerText.Text = snapshot.ControlHint ?? "";
        ControlBanner.Visibility = snapshot.ControlsAvailable && snapshot.WriteAllowed
            ? Visibility.Collapsed
            : Visibility.Visible;

        _controlsAvailable = snapshot.ControlsAvailable && snapshot.WriteAllowed;
        var enabled = _controlsAvailable;
        foreach (var button in _ancButtons)
        {
            button.IsEnabled = enabled;
        }

        foreach (var button in _eqButtons)
        {
            button.IsEnabled = enabled;
        }

        GameModeSwitch.IsEnabled = enabled;
        LowLatencySwitch.IsEnabled = enabled;
        DualDeviceSwitch.IsEnabled = enabled;
        InEarSwitch.IsEnabled = enabled;
        WindNoiseSwitch.IsEnabled = enabled;
        FindButton.IsEnabled = enabled;
        if (!_controlsAvailable)
        {
            ResetFind(clearStatus: true);
        }

        var controls = App.PeripheralService.GetHeadsetControls();
        _loading = true;
        _ancIndex = controls.IsKnown ? (int)controls.Anc : -1;
        _eqIndex = controls.IsKnown ? (int)controls.Eq : -1;
        GameModeSwitch.IsOn = controls.IsKnown && controls.GameMode;
        LowLatencySwitch.IsOn = controls.IsKnown && controls.LowLatency;
        DualDeviceSwitch.IsOn = controls.IsKnown && controls.DualDevice;
        InEarSwitch.IsOn = controls.IsKnown && controls.InEarDetection;
        WindNoiseSwitch.IsOn = controls.IsKnown && controls.WindNoise;
        _loading = false;

        ChipAnc.Text = _ancIndex >= 0 ? AncNames[_ancIndex] : "—";
        ChipEq.Text = _eqIndex >= 0 ? EqNames[_eqIndex] : "—";
        ApplySelectionColors();
    }

    private void ApplySelectionColors()
    {
        var ink = AppTheme.GetBrush(this, "AppInkBrush");
        var onInk = AppTheme.GetBrush(this, "AppOnInkBrush");
        var primary = AppTheme.GetBrush(this, "AppTextPrimaryBrush");
        var tertiary = AppTheme.GetBrush(this, "AppTextTertiaryBrush");
        var border = AppTheme.GetBrush(this, "AppBorderBrush");
        var surface = AppTheme.GetBrush(this, "AppCardBrush");

        for (var i = 0; i < _ancButtons.Count; i++)
        {
            var selected = i == _ancIndex;
            var button = _ancButtons[i];
            button.Background = selected ? ink : surface;
            button.BorderBrush = selected ? ink : border;
            _ancLabels[i].Foreground = selected ? onInk : primary;
            _ancNotes[i].Foreground = selected ? onInk : tertiary;
            _ancNotes[i].Opacity = selected ? 0.8 : 1;
        }

        for (var i = 0; i < _eqButtons.Count; i++)
        {
            var selected = i == _eqIndex;
            var button = _eqButtons[i];
            button.Background = selected ? ink : surface;
            button.BorderBrush = selected ? ink : border;
            _eqLabels[i].Foreground = selected ? onInk : primary;
        }
    }

    private void SetComponent(
        PeripheralSnapshot snapshot,
        string key,
        ColumnDefinition fill,
        ColumnDefinition rest,
        TextBlock percentText,
        TextBlock chargingText)
    {
        var component = snapshot.Components.FirstOrDefault(item => item.Key == key);
        var value = component?.Percent;
        if (value is int percent)
        {
            fill.Width = new GridLength(Math.Clamp(percent, 0, 100), GridUnitType.Star);
            rest.Width = new GridLength(Math.Clamp(100 - percent, 0, 100), GridUnitType.Star);
            percentText.Text = $"{percent}%";
        }
        else
        {
            fill.Width = new GridLength(0, GridUnitType.Star);
            rest.Width = new GridLength(1, GridUnitType.Star);
            percentText.Text = "—";
        }

        var charging = component?.Charging ?? ChargingState.Unknown;
        chargingText.Text = charging switch
        {
            ChargingState.Charging => "充电中",
            ChargingState.Full => "已充满",
            ChargingState.Discharging => "使用中",
            _ => "",
        };
        chargingText.Foreground = AppTheme.GetBrush(this,
            charging == ChargingState.Charging ? "AppWarningBrush" : "AppTextTertiaryBrush");
    }

    private void Anc_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || !_controlsAvailable || sender is not Button { Tag: int index })
        {
            return;
        }

        _ancIndex = index;
        ChipAnc.Text = AncNames[index];
        ApplySelectionColors();
        Save();
    }

    private void Eq_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || !_controlsAvailable || sender is not Button { Tag: int index })
        {
            return;
        }

        _eqIndex = index;
        ChipEq.Text = EqNames[index];
        ApplySelectionColors();
        Save();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e) => Save();

    private async void Save()
    {
        if (_loading || !_controlsAvailable)
        {
            return;
        }

        var controls = new HeadsetControls(
            _ancIndex >= 0 ? (AncMode)_ancIndex : AncMode.Off,
            _eqIndex >= 0 ? (EqPreset)_eqIndex : EqPreset.Balanced,
            GameModeSwitch.IsOn,
            LowLatencySwitch.IsOn,
            DualDeviceSwitch.IsOn,
            InEarSwitch.IsOn,
            WindNoiseSwitch.IsOn);

        var result = await App.PeripheralService.UpdateHeadsetControlsAsync(controls);
        SaveStatus.Text = result.Ok ? "已写入耳机并回读校验" : result.Message ?? "写入失败";
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

        var cts = new CancellationTokenSource();
        _findCts = cts;
        try
        {
            for (var n = 5; n >= 1; n--)
            {
                SaveStatus.Text = $"{n} 秒后开始鸣响，再点一次可取消";
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


