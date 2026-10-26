using DeviceCenterPreview.App.Services;
using DeviceCenterPreview.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeviceCenterPreview.App.Views;

public sealed partial class MousePage : Page
{
    private static readonly (string Id, string Label)[] ActionOptions =
    [
        ("disabled", "禁用"),
        ("left", "左键"),
        ("right", "右键"),
        ("middle", "中键"),
        ("back", "后退"),
        ("forward", "前进"),
        ("enter", "Enter"),
        ("escape", "Esc"),
        ("space", "空格"),
        ("copy", "Ctrl+C"),
        ("paste", "Ctrl+V"),
        ("volume_up", "音量+"),
        ("volume_down", "音量-"),
        ("mute", "静音"),
        ("play_pause", "播放/暂停"),
        ("media_next", "下一曲"),
        ("media_prev", "上一曲"),
        ("dpi", "DPI 切换"),
    ];

    private readonly TextBox[] _stageBoxes;
    private readonly ComboBox[] _buttonCombos;
    private readonly TextBlock[] _buttonLabels;
    private readonly Grid[] _buttonRows;
    private readonly List<ActionOption> _actionOptions;
    private bool _loading;
    private bool _syncingDpi;
    private bool _dirty;
    private bool _controlsAvailable;
    private bool _canWrite;
    private List<int> _rates = [];
    private string _lastDeviceSignature = "";
    private string _lastPollingSignature = "";
    private string _lastButtonSignature = "";
    private string _lastStageSignature = "";
    private string? _lastArtworkPath;

    public MousePage()
    {
        InitializeComponent();
        _stageBoxes = [StageBox1, StageBox2, StageBox3, StageBox4, StageBox5];
        _buttonCombos =
        [
            ButtonCombo1, ButtonCombo2, ButtonCombo3, ButtonCombo4,
            ButtonCombo5, ButtonCombo6, ButtonCombo7, ButtonCombo8,
            ButtonCombo9, ButtonCombo10, ButtonCombo11, ButtonCombo12,
            ButtonCombo13, ButtonCombo14, ButtonCombo15, ButtonCombo16,
        ];
        _buttonLabels =
        [
            ButtonLabel1, ButtonLabel2, ButtonLabel3, ButtonLabel4,
            ButtonLabel5, ButtonLabel6, ButtonLabel7, ButtonLabel8,
            ButtonLabel9, ButtonLabel10, ButtonLabel11, ButtonLabel12,
            ButtonLabel13, ButtonLabel14, ButtonLabel15, ButtonLabel16,
        ];
        _buttonRows =
        [
            ButtonRow1, ButtonRow2, ButtonRow3, ButtonRow4,
            ButtonRow5, ButtonRow6, ButtonRow7, ButtonRow8,
            ButtonRow9, ButtonRow10, ButtonRow11, ButtonRow12,
            ButtonRow13, ButtonRow14, ButtonRow15, ButtonRow16,
        ];
        _actionOptions = ActionOptions
            .Select(option => new ActionOption(option.Id, option.Label))
            .ToList();
        foreach (var combo in _buttonCombos)
        {
            combo.ItemsSource = _actionOptions;
            combo.PlaceholderText = "未识别（保留原值）";
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PageScroll.SizeChanged += (_, _) => PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.PeripheralService.Changed += OnServiceChanged;
        PageContent.Width = Math.Max(0, PageScroll.ViewportWidth);
        // 每次进入页面都以设备当前值为准，避免缓存的旧编辑状态。
        _dirty = false;
        Refresh();
        _ = RefreshMouseOnLoadAsync();
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
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        var snapshot = App.PeripheralService.GetSnapshot(DeviceKind.Mouse);
        var controls = App.PeripheralService.GetMouseControls();

        _loading = true;
        var devices = App.PeripheralService.GetDevices(DeviceKind.Mouse);
        var deviceSignature = string.Join("|", devices.Select(device => $"{device.Id}:{device.Name}:{device.IsConnected}"));
        if (deviceSignature != _lastDeviceSignature)
        {
            _lastDeviceSignature = deviceSignature;
            DeviceCombo.ItemsSource = devices;
        }

        var selected = App.PeripheralService.GetSelectedDeviceId(DeviceKind.Mouse);
        if (!string.IsNullOrEmpty(selected))
        {
            DeviceCombo.SelectedValue = selected;
        }

        TitleText.Text = $"鼠标 · {snapshot.DisplayName}";
        var artwork = DeviceArtwork.ForMouse(snapshot.DisplayName);
        if (artwork != _lastArtworkPath)
        {
            _lastArtworkPath = artwork;
            if (artwork is not null)
            {
                MouseHeroImage.Source = new BitmapImage(new Uri($"ms-appx:///{artwork}"));
            }
        }

        MouseHeroImage.Visibility = artwork is not null ? Visibility.Visible : Visibility.Collapsed;
        MouseHeroIcon.Visibility = artwork is not null ? Visibility.Collapsed : Visibility.Visible;
        SubtitleText.Text = snapshot.StatusText;
        MouseRing.BatteryValue = snapshot.BatteryPercent ?? 0;
        MouseRing.Charging = snapshot.Charging == ChargingState.Charging;
        MouseRing.RingBrush = (snapshot.BatteryPercent ?? 0) switch
        {
            <= 15 => ResourceBrush("AppDangerBrush"),
            <= 35 => ResourceBrush("AppWarningBrush"),
            _ => ResourceBrush("AppMouseBrush"),
        };
        ControlBannerText.Text = snapshot.ControlHint ?? "";
        ControlBanner.Visibility = snapshot.ControlsAvailable && snapshot.WriteAllowed
            ? Visibility.Collapsed
            : Visibility.Visible;

        _controlsAvailable = snapshot.ControlsAvailable;
        _canWrite = snapshot.ControlsAvailable && snapshot.WriteAllowed && controls.WriteSupported;
        // 板载模式下滑条编辑的是“当前档位”的板载值（不再禁用），
        // 主机模式下才直写 DPI 寄存器；语义见 Dpi_ValueChanged 的档位同步。
        var onboardActive = _controlsAvailable && controls.OnboardMode && controls.OnboardProfilesAvailable;
        DpiSlider.IsEnabled = _canWrite;
        PollingCombo.IsEnabled = _canWrite;
        ApplyButton.IsEnabled = _canWrite;
        OnboardToggle.IsEnabled = _canWrite && controls.OnboardProfilesAvailable;
        StageIndexCombo.IsEnabled = _canWrite && controls.OnboardProfilesAvailable;
        if (DpiLabel is not null)
        {
            DpiLabel.Text = onboardActive
                ? $"当前档位 DPI（档位 {(controls.DpiStageIndex ?? 0) + 1}）"
                : "当前 DPI";
        }

        if (DpiHintText is not null)
        {
            DpiHintText.Text = "板载模式执行中：这里调节的是当前档位的板载配置，" +
                               "鼠标上的 DPI 键用于在档位间切换；关闭「板载配置」后可直接调节主机 DPI。";
            DpiHintText.Visibility = onboardActive ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_controlsAvailable)
        {
            HeroDpiText.Text = controls.Dpi is int heroDpi ? $"{heroDpi}" : "—";
            HeroPollingText.Text = controls.PollingRateHz is int heroRate ? $"{heroRate}" : "—";
            HeroModeText.Text = !controls.OnboardProfilesAvailable
                ? "不支持"
                : (controls.OnboardMode ? "已开启" : "已关闭");

            _rates = controls.SupportedPollingRates is { Count: > 0 }
                ? controls.SupportedPollingRates.ToList()
                : [125, 250, 500, 1000];
            var pollingSignature = string.Join(",", _rates);
            if (pollingSignature != _lastPollingSignature)
            {
                _lastPollingSignature = pollingSignature;
                PollingCombo.ItemsSource = _rates.Select(rate => $"{rate} Hz").ToList();
            }

            var stages = controls.DpiStages;
            if (StageIndexCombo.Items.Count != 5)
            {
                StageIndexCombo.ItemsSource = Enumerable.Range(1, 5).Select(index => $"档位 {index}").ToList();
            }

            // 15 秒刷新只回填设备值；用户有未应用的修改时保留表单，
            // 否则正在拖的滑条、正在输入的档位会被刷新打回原值。
            if (!_dirty)
            {
                _syncingDpi = true;
                try
                {
                    DpiSlider.Minimum = controls.DpiMin ?? 400;
                    DpiSlider.Maximum = controls.DpiMax ?? 8000;
                    DpiSlider.StepFrequency = controls.DpiStep ?? 50;
                    DpiSlider.Value = Math.Clamp(
                        controls.Dpi ?? (int)DpiSlider.Minimum,
                        (int)DpiSlider.Minimum,
                        (int)DpiSlider.Maximum);
                    PollingCombo.SelectedIndex = controls.PollingRateHz is int rate
                        ? _rates.IndexOf(rate)
                        : -1;

                    var stageSignature = string.Join(",", stages ?? []) +
                                         $"|{controls.DpiStageIndex}|{controls.OnboardProfilesAvailable}";
                    if (stageSignature != _lastStageSignature)
                    {
                        _lastStageSignature = stageSignature;
                        for (var index = 0; index < _stageBoxes.Length; index++)
                        {
                            var hasStage = stages is not null && stages.Count > index;
                            _stageBoxes[index].IsEnabled = _canWrite && hasStage;
                            _stageBoxes[index].Text = hasStage ? stages![index].ToString() : "";
                        }
                    }

                    StageIndexCombo.SelectedIndex = controls.DpiStageIndex is int stageIndex
                        ? Math.Clamp(stageIndex, 0, 4)
                        : -1;
                    OnboardToggle.IsOn = controls.OnboardMode;
                }
                finally
                {
                    _syncingDpi = false;
                }
            }

            DpiValueText.Text = _dirty
                ? $"{(int)DpiSlider.Value} DPI"
                : (controls.Dpi is int currentDpi ? $"{currentDpi} DPI" : "未读取");

            ProfileInfoText.Text = controls.OnboardProfilesAvailable
                ? $"板载配置：{controls.ProfileName ?? "未命名"} · 配置 {controls.ProfileFormat ?? 1} · " +
                  $"{controls.ProfileSize ?? 0} 字节 · {(controls.OnboardMode ? "鼠标固件执行中" : "主机模式")} · " +
                  $"{(controls.WriteSupported ? "可写" : controls.WriteUnsupportedReason ?? "只读")}"
                : "该鼠标未暴露可读写的板载配置；DPI 档位不可编辑。";
            ProfileInfoText.Text += stages is null ? "" : $" · 当前档位 {stages[Math.Clamp(controls.DpiStageIndex ?? 0, 0, 4)]} DPI";
            if (ProfileNameBox.FocusState == FocusState.Unfocused &&
                ProfileNameBox.Text != (controls.ProfileName ?? ""))
            {
                ProfileNameBox.Text = controls.ProfileName ?? "";
            }

            DeviceInfoText.Text =
                $"名称：{snapshot.DisplayName}\n" +
                $"连接：{snapshot.StatusText}\n" +
                $"电量：{FormatBattery(snapshot.BatteryPercent, snapshot.Charging)}\n" +
                $"固件：{snapshot.Firmware ?? "—"}\n" +
                $"DPI 范围：{controls.DpiMin?.ToString() ?? "—"}–{controls.DpiMax?.ToString() ?? "—"}，" +
                $"步进 {controls.DpiStep?.ToString() ?? "—"}\n" +
                $"轮询率：{string.Join(" / ", _rates.Select(item => $"{item} Hz"))}\n" +
                $"板载配置：{(controls.OnboardProfilesAvailable ? (controls.WriteSupported ? "可读写" : "只读") : "不可用")}\n" +
                $"控制通道：{(controls.WriteSupported ? "可写" : "只读")}";

            UpdateButtonRows(controls);
        }
        else
        {
            DpiValueText.Text = "未接入";
            HeroDpiText.Text = "—";
            HeroPollingText.Text = "—";
            HeroModeText.Text = "未接入";
            PollingCombo.SelectedIndex = -1;
            ProfileInfoText.Text = "控制通道尚未接入。";
            DeviceInfoText.Text = snapshot.ControlHint ?? snapshot.StatusText;
            foreach (var box in _stageBoxes)
            {
                box.IsEnabled = false;
                box.Text = "";
            }

            foreach (var row in _buttonRows)
            {
                row.Visibility = Visibility.Collapsed;
            }

            _lastButtonSignature = "";
            _dirty = false;
            ButtonCountText.Text = "控制通道未接入";
            ButtonMappingScroll.Visibility = Visibility.Collapsed;
            ButtonMappingEmpty.Visibility = Visibility.Visible;
            ButtonMappingEmpty.Text = "控制通道尚未接入；接入后按设备返回的按键显示。";
            ButtonMappingHint.Text = "不显示空白按键行。";
            foreach (var combo in _buttonCombos)
            {
                combo.IsEnabled = false;
            }
        }

        _loading = false;
    }

    private void UpdateButtonRows(MouseControls controls)
    {
        var signature = string.Join("|", controls.Buttons.Select(button =>
            $"{button.Id}:{button.Key}:{button.Action}")) +
                        $"|{controls.OnboardProfilesAvailable}|{controls.OnboardProfileWritable}|{_canWrite}";
        if (signature == _lastButtonSignature)
        {
            return;
        }

        _lastButtonSignature = signature;
        var count = controls.Buttons.Count;
        var mappingAvailable = controls.OnboardProfilesAvailable;
        var writable = mappingAvailable && _canWrite && controls.OnboardProfileWritable;
        var visibleRows = mappingAvailable ? (count + 1) / 2 : 0;

        for (var rowIndex = 0; rowIndex < ButtonMappingPanel.RowDefinitions.Count; rowIndex++)
        {
            ButtonMappingPanel.RowDefinitions[rowIndex].Height =
                rowIndex < visibleRows ? new GridLength(44) : new GridLength(0);
        }

        if (!mappingAvailable)
        {
            ButtonCountText.Text = "不可用";
            ButtonMappingScroll.Visibility = Visibility.Collapsed;
            ButtonMappingEmpty.Visibility = Visibility.Visible;
            ButtonMappingEmpty.Text = "当前设备未提供板载按键配置；性能设置仍可用。";
            ButtonMappingHint.Text = "不猜测设备能力，也不显示空白按键行。";
        }
        else if (count == 0)
        {
            ButtonCountText.Text = "设备未返回按键";
            ButtonMappingScroll.Visibility = Visibility.Collapsed;
            ButtonMappingEmpty.Visibility = Visibility.Visible;
            ButtonMappingEmpty.Text = "此鼠标没有返回可配置按键；性能与设备信息仍可用。";
            ButtonMappingHint.Text = "按键数量和名称由鼠标板载配置提供。";
        }
        else
        {
            ButtonCountText.Text = $"设备返回 {count} 个";
            ButtonMappingScroll.Visibility = Visibility.Visible;
            ButtonMappingEmpty.Visibility = Visibility.Collapsed;
            ButtonMappingHint.Text = writable
                ? "按键数量与顺序由鼠标板载配置决定；非标准按键以设备槽位显示。"
                : "当前配置只读：可以查看按键，不能写入。";
        }

        for (var index = 0; index < _buttonRows.Length; index++)
        {
            var hasButton = index < count;
            _buttonRows[index].Visibility = hasButton ? Visibility.Visible : Visibility.Collapsed;
            if (!hasButton)
            {
                continue;
            }

            var button = controls.Buttons[index];
            _buttonLabels[index].Text = button.Key;

            var combo = _buttonCombos[index];
            if (_actionOptions.Any(option => option.Id == button.Action))
            {
                combo.SelectedValue = button.Action;
            }
            else
            {
                // 未知动作不清空原值：显示占位提示，应用时保留设备返回的原动作。
                combo.SelectedIndex = -1;
            }

            combo.IsEnabled = writable;
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DeviceCombo.SelectedValue is not string id)
        {
            return;
        }

        App.PeripheralService.SelectDevice(DeviceKind.Mouse, id);
        Refresh();
    }

    private void Dpi_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DpiValueText is not null)
        {
            DpiValueText.Text = $"{(int)DpiSlider.Value} DPI";
        }

        // 程序回填设备值（15s 刷新/档位切换）时不能把表单标记成“用户修改”。
        if (_loading || _syncingDpi)
        {
            return;
        }

        MarkDirty();

        // 板载模式：滑条即当前档位编辑器——把值同步进对应档位输入框，
        // 「应用设置」走板载配置写入路径（WriteProfileControls）。
        // 注意：InitializeComponent 解析 Minimum/Value 时本回调就会触发，
        // 此时 OnboardToggle/StageIndexCombo/_stageBoxes 还没建好，
        // 必须全部判空，否则空引用会被 XAML 解析器包装成页面解析失败。
        if (OnboardToggle is not { IsOn: true } ||
            StageIndexCombo is not { SelectedIndex: >= 0 and < 5 } ||
            _stageBoxes is null)
        {
            return;
        }

        var stage = _stageBoxes[StageIndexCombo.SelectedIndex];
        if (stage is { IsEnabled: true } && stage.Text != ((int)DpiSlider.Value).ToString())
        {
            _syncingDpi = true;
            try
            {
                stage.Text = ((int)DpiSlider.Value).ToString();
            }
            finally
            {
                _syncingDpi = false;
            }
        }
    }

    /// <summary>「当前档位」下拉：选中哪档，滑条和 DPI 读数就跟到该档位的值。</summary>
    private void StageIndex_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingDpi || StageIndexCombo is null || _stageBoxes is null)
        {
            return;
        }

        MarkDirty();
        var index = StageIndexCombo.SelectedIndex;
        if (index < 0 || index >= _stageBoxes.Length)
        {
            return;
        }

        if (DpiLabel is not null)
        {
            DpiLabel.Text = $"当前档位 DPI（档位 {index + 1}）";
        }

        if (int.TryParse(_stageBoxes[index].Text.Trim(), out var value) && value > 0)
        {
            SetSliderValue(value);
        }
    }

    /// <summary>档位输入框：改的是当前档位时，滑条与读数同步。</summary>
    private void StageBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _syncingDpi || _stageBoxes is null ||
            StageIndexCombo is null || DpiSlider is null)
        {
            return;
        }

        if (sender is not TextBox box)
        {
            return;
        }

        var index = Array.IndexOf(_stageBoxes, box);
        if (index < 0)
        {
            return;
        }

        MarkDirty();
        if (index != StageIndexCombo.SelectedIndex)
        {
            return;
        }

        if (int.TryParse(box.Text.Trim(), out var value) && value > 0)
        {
            SetSliderValue(value);
        }
    }

    private void Polling_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingDpi)
        {
            return;
        }

        MarkDirty();
    }

    private void SetSliderValue(int value)
    {
        if (DpiSlider is null)
        {
            return;
        }

        var clamped = Math.Clamp(value, (int)DpiSlider.Minimum, (int)DpiSlider.Maximum);
        if (Math.Abs(DpiSlider.Value - clamped) < 0.5)
        {
            return;
        }

        _syncingDpi = true;
        try
        {
            DpiSlider.Value = clamped;
        }
        finally
        {
            _syncingDpi = false;
        }

        if (DpiValueText is not null)
        {
            DpiValueText.Text = $"{clamped} DPI";
        }
    }

    private void MarkDirty()
    {
        if (_dirty)
        {
            return;
        }

        _dirty = true;
        if (SaveStatus is not null)
        {
            SaveStatus.Text = "有未应用的修改，点「应用设置」写入";
        }
    }

    private void Onboard_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            MarkDirty();
            SaveStatus.Text = "板载模式将在“应用设置”后切换。";
        }
    }

    private async Task RefreshMouseOnLoadAsync()
    {
        await App.PeripheralService.RefreshMouseAsync();
        Refresh();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        SaveStatus.Text = "正在探测鼠标…";
        await App.PeripheralService.RefreshMouseAsync();
        _lastButtonSignature = "";
        _lastStageSignature = "";
        // 手动刷新即放弃未应用的编辑，以设备当前值为准。
        _dirty = false;
        Refresh();
        RefreshButton.IsEnabled = true;
        SaveStatus.Text = _controlsAvailable ? "已发现鼠标" : "未发现鼠标；请移动鼠标后重试";
    }

    private List<int>? ReadStages()
    {
        var stages = new List<int>(_stageBoxes.Length);
        foreach (var box in _stageBoxes)
        {
            var text = box.Text.Trim();
            if (text.Length == 0)
            {
                stages.Add(0);
                continue;
            }

            if (!int.TryParse(text, out var value) || value is < 0 or > 12000)
            {
                SaveStatus.Text = "DPI 档位必须是 0–12000 的整数。";
                return null;
            }

            stages.Add(value);
        }

        return stages;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_controlsAvailable || !_canWrite)
        {
            return;
        }

        var current = App.PeripheralService.GetMouseControls();
        var polling = PollingCombo.SelectedIndex >= 0 && PollingCombo.SelectedIndex < _rates.Count
            ? _rates[PollingCombo.SelectedIndex]
            : current.PollingRateHz ?? 0;

        var stages = current.OnboardProfilesAvailable
            ? ReadStages()
            : null;
        if (current.OnboardProfilesAvailable && stages is null)
        {
            return;
        }
        var buttons = current.OnboardProfilesAvailable
            ? current.Buttons.Select((button, index) =>
                index < _buttonCombos.Length && _buttonCombos[index].SelectedValue is string action
                    ? button with { Action = action }
                    : button).ToList()
            : current.Buttons;

        var updated = current with
        {
            Dpi = (int)DpiSlider.Value,
            PollingRateHz = polling > 0 ? polling : null,
            DpiStages = stages,
            DpiStageIndex = StageIndexCombo.SelectedIndex >= 0 ? StageIndexCombo.SelectedIndex : current.DpiStageIndex,
            OnboardMode = OnboardToggle.IsOn,
            ProfileName = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? current.ProfileName : ProfileNameBox.Text.Trim(),
            Buttons = buttons,
        };

        ApplyButton.IsEnabled = false;
        SaveStatus.Text = "正在写入…";
        var result = await App.PeripheralService.UpdateMouseControlsAsync(updated);
        ApplyButton.IsEnabled = _canWrite;
        _lastButtonSignature = "";
        if (result.Ok)
        {
            _dirty = false;
        }

        Refresh();
        SaveStatus.Text = result.Ok
            ? $"已应用：{updated.Dpi} DPI · {updated.PollingRateHz?.ToString() ?? "-"} Hz" +
              (updated.OnboardMode ? " · 板载开" : " · 板载关")
            : result.Message ?? "写入失败";
    }

    private static string FormatBattery(int? percent, ChargingState charging)
    {
        var text = percent is null ? "—" : $"{percent}%";
        return charging == ChargingState.Charging ? $"{text}  ⚡" : text;
    }

    private Microsoft.UI.Xaml.Media.Brush ResourceBrush(string key) =>
        Services.AppTheme.GetBrush(this, key);

    private sealed record ActionOption(string Id, string Label);
}
