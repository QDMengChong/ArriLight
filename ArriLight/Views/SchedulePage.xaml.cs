using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ArriOpto.BLE.Agp;

namespace ArriLight;

public partial class SchedulePage : ContentPage, IQueryAttributable
{
    private readonly AppDiagnosticService _diagnostics;
    private readonly AgpLightControlService _agpLight;
    private readonly HashSet<int> _weeklySelections = [];
    private readonly HashSet<int> _monthlySelections = [];
    private string _deviceName = "灯光设备";
    private string _deviceAddress = string.Empty;
    private string _repeatSelectionSummary = "每天";
    private string _repeatSelectionDisplayText = "每天";
    private int _confirmedRepeatIndex = 1;
    private bool _isInitializing = true;
    private bool _isActionOn = true;

    public string DeviceName
    {
        get => _deviceName;
        private set => SetField(ref _deviceName, value);
    }

    public string RepeatSelectionSummary
    {
        get => _repeatSelectionSummary;
        private set => SetField(ref _repeatSelectionSummary, value);
    }

    public string RepeatSelectionDisplayText
    {
        get => _repeatSelectionDisplayText;
        private set => SetField(ref _repeatSelectionDisplayText, value);
    }

    public string ScheduleCountText => $"{Schedules.Count} 个计划";

    public IReadOnlyList<string> RepeatModes { get; } =
        ["仅一次", "每天", "每周", "每月"];

    public ObservableCollection<RepeatOptionItem> RepeatOptions { get; } = [];

    public ObservableCollection<LightScheduleItem> Schedules { get; } =
    [
        new("07:30", "定时开", "工作日", true),
        new("23:00", "定时关", "每天", true)
    ];

    public SchedulePage()
    {
        InitializeComponent();
        _agpLight = ServiceHelper.GetRequiredService<AgpLightControlService>();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        BindingContext = this;
        Schedules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ScheduleCountText));

        RepeatModePicker.SelectedIndex = _confirmedRepeatIndex;
        ScheduleTimePicker.Time = new TimeSpan(7, 30, 0);
        UpdateActionButtons();
        UpdateRepeatSummary();
        _isInitializing = false;
    }
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("name", out var value) && value is not null)
            DeviceName = Uri.UnescapeDataString(value.ToString()!);
        if (query.TryGetValue("address", out var address) && address is not null)
            _deviceAddress = Uri.UnescapeDataString(address.ToString()!);
    }

    private void OnRepeatModeChanged(object? sender, EventArgs e)
    {
        if (_isInitializing || RepeatModePicker.SelectedIndex < 0)
            return;

        var selectedIndex = RepeatModePicker.SelectedIndex;
        if (selectedIndex is 2 or 3)
        {
            ShowRepeatPopup(selectedIndex);
            return;
        }

        _confirmedRepeatIndex = selectedIndex;
        UpdateRepeatSummary();
    }

    private void OnRepeatDetailClicked(object? sender, EventArgs e)
    {
        if (_confirmedRepeatIndex is 2 or 3)
            ShowRepeatPopup(_confirmedRepeatIndex);
    }

    private void ShowRepeatPopup(int repeatIndex)
    {
        RepeatOptions.Clear();

        if (repeatIndex == 2)
        {
            RepeatPopupTitle.Text = "选择每周执行日";
            var labels = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            if (_weeklySelections.Count == 0)
                _weeklySelections.Add(ToMondayBasedDay(DateTime.Today.DayOfWeek));

            for (var index = 1; index <= labels.Length; index++)
                RepeatOptions.Add(new(labels[index - 1], index, _weeklySelections.Contains(index)));
        }
        else
        {
            RepeatPopupTitle.Text = "选择每月执行日";
            if (_monthlySelections.Count == 0)
                _monthlySelections.Add(DateTime.Today.Day);

            for (var day = 1; day <= 31; day++)
                RepeatOptions.Add(new($"{day}日", day, _monthlySelections.Contains(day)));
        }

        RepeatPopupOverlay.IsVisible = true;
    }

    private void OnRepeatPopupCancelClicked(object? sender, EventArgs e)
    {
        RepeatPopupOverlay.IsVisible = false;
        RestoreConfirmedRepeatMode();
    }

    private async void OnRepeatPopupConfirmClicked(object? sender, EventArgs e)
    {
        var selectedValues = RepeatOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Value)
            .ToArray();

        if (selectedValues.Length == 0)
        {
            await DisplayAlertAsync("请选择日期", "至少需要选择一个执行日期。", "知道了");
            return;
        }

        var selectedMode = RepeatModePicker.SelectedIndex;
        var target = selectedMode == 2 ? _weeklySelections : _monthlySelections;
        target.Clear();
        target.UnionWith(selectedValues);

        _confirmedRepeatIndex = selectedMode;
        RepeatPopupOverlay.IsVisible = false;
        UpdateRepeatSummary();
    }

    private void RestoreConfirmedRepeatMode()
    {
        _isInitializing = true;
        RepeatModePicker.SelectedIndex = _confirmedRepeatIndex;
        _isInitializing = false;
        UpdateRepeatSummary();
    }

    private void UpdateRepeatSummary()
    {
        RepeatDetailButton.IsEnabled = _confirmedRepeatIndex is 2 or 3;
        RepeatSelectionSummary = _confirmedRepeatIndex switch
        {
            0 => "仅一次",
            1 => "每天",
            2 when _weeklySelections.Count > 0 =>
                string.Join("、", _weeklySelections.Order().Select(WeeklyLabel)),
            2 => "选择星期",
            3 when _monthlySelections.Count > 0 =>
                string.Join("、", _monthlySelections.Order().Select(day => $"{day}日")),
            3 => "选择日期",
            _ => "每天"
        };
        RepeatSelectionDisplayText = CompactSummary(RepeatSelectionSummary);
    }

    private void OnActionOnClicked(object? sender, EventArgs e)
    {
        _isActionOn = true;
        UpdateActionButtons();
    }

    private void OnActionOffClicked(object? sender, EventArgs e)
    {
        _isActionOn = false;
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var inactiveBackground = isDark
            ? Color.FromArgb("#263247")
            : Colors.Transparent;
        var inactiveText = isDark
            ? Color.FromArgb("#A6B0C2")
            : Color.FromArgb("#6F7B90");

        ActionOnButton.BackgroundColor = _isActionOn
            ? Color.FromArgb("#3157E8")
            : inactiveBackground;
        ActionOnButton.TextColor = _isActionOn ? Colors.White : inactiveText;
        ActionOffButton.BackgroundColor = _isActionOn
            ? inactiveBackground
            : Color.FromArgb("#3157E8");
        ActionOffButton.TextColor = _isActionOn ? inactiveText : Colors.White;
    }

    /// <summary>标题栏“+ 添加”：打开添加定时弹层。</summary>
    private void OnAddScheduleClicked(object? sender, EventArgs e)
    {
        _isInitializing = true;
        RepeatModePicker.SelectedIndex = _confirmedRepeatIndex;
        ScheduleTimePicker.Time = new TimeSpan(7, 30, 0);
        UpdateActionButtons();
        _isInitializing = false;
        AddScheduleOverlay.IsVisible = true;
    }

    private void OnAddScheduleCancelClicked(object? sender, EventArgs e) =>
        AddScheduleOverlay.IsVisible = false;

    private async void OnAddScheduleConfirmClicked(object? sender, EventArgs e)
    {
        if (Schedules.Count >= AgpModelCodec.MaximumScheduleSlots - 1)
        {
            await DisplayAlertAsync(
                "日程已满",
                "当前可用 7 个 AGP 日程槽，最后一个槽保留给延时开灯。",
                "知道了");
            return;
        }

        var time = ScheduleTimePicker.Time ?? DateTime.Now.TimeOfDay;
        var index = (byte)(Schedules.Count % (AgpModelCodec.MaximumScheduleSlots - 1));
        var register = BuildRegister(index, time, _isActionOn);
        if (!await TrySendScheduleAsync(register))
        {
            await DisplayAlertAsync("发送失败", "无法发送定时指令，请确认设备已连接。", "知道了");
            return;
        }

        Schedules.Add(new(
            $"{time.Hours:00}:{time.Minutes:00}",
            _isActionOn ? "定时开" : "定时关",
            RepeatSelectionSummary,
            true,
            register));
        AddScheduleOverlay.IsVisible = false;
    }

    private async void OnDeleteScheduleClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightScheduleItem schedule })
            return;

        var confirmed = await DisplayAlertAsync(
            "删除定时",
            $"确定删除 {schedule.TimeText} 的“{schedule.ActionText}”计划吗？",
            "删除",
            "取消");

        if (!confirmed)
            return;

        var slot = schedule.Register?.Slot ??
            (byte)Math.Max(0, Schedules.IndexOf(schedule));
        try
        {
            await _agpLight.DeleteScheduleAsync(_deviceAddress, slot);
        }
        catch (Exception exception)
        {
            _diagnostics.Error(
                "Light/Schedule",
                $"删除 AGP 日程失败：{DeviceName}。",
                exception);
            await DisplayAlertAsync("删除失败", "设备未接受删除日程指令。", "知道了");
            return;
        }
        Schedules.Remove(schedule);
    }

    private async void OnScheduleEnabledToggled(object? sender, ToggledEventArgs e)
    {
        if (sender is not Switch { BindingContext: LightScheduleItem schedule } ||
            schedule.IsEnabled == e.Value)
        {
            return;
        }

        var index = schedule.Register?.Slot ??
            (byte)Math.Max(0, Schedules.IndexOf(schedule));
        var register = schedule.Register ?? BuildRegister(
            index,
            TimeSpan.Parse(schedule.TimeText),
            schedule.ActionText == "定时开");
        register = register with { IsEnabled = e.Value };
        if (await TrySendScheduleAsync(register))
            schedule.IsEnabled = e.Value;
    }

    private async Task<bool> TrySendScheduleAsync(AgpSchedule register)
    {
        if (string.IsNullOrWhiteSpace(_deviceAddress))
            return false;

        try
        {
            await _agpLight.SetScheduleAsync(_deviceAddress, register);
            return true;
        }
        catch (Exception exception)
        {
            _diagnostics.Error(
                "Light/Schedule",
                $"发送定时指令失败：{DeviceName}。",
                exception);
            return false;
        }
    }

    private AgpSchedule BuildRegister(byte index, TimeSpan time, bool turnOn)
    {
        var action = turnOn ? AgpScheduleAction.On : AgpScheduleAction.Off;
        var brightness = turnOn ? (byte)100 : (byte)0;
        var utcTime = ToUtcScheduleTime(time);
        return _confirmedRepeatIndex switch
        {
            0 => AgpSchedule.Once(
                index,
                true,
                action,
                brightness,
                ToNextExecutionUtc(time)),
            2 => AgpSchedule.Weekly(
                index,
                true,
                action,
                brightness,
                ToUtcWeekdayMask(_weeklySelections, utcTime.DayShift),
                utcTime.SecondsOfDay),
            3 => AgpSchedule.Monthly(
                index,
                true,
                action,
                brightness,
                ToUtcMonthlyDay(
                    _monthlySelections.Count > 0
                        ? _monthlySelections.Min()
                        : DateTime.Today.Day,
                    time),
                utcTime.SecondsOfDay),
            _ => AgpSchedule.Daily(
                index,
                true,
                action,
                brightness,
                utcTime.SecondsOfDay)
        };
    }

    private static uint ToNextExecutionUtc(TimeSpan time)
    {
        var local = DateTime.Today.Add(time);
        if (local <= DateTime.Now)
            local = local.AddDays(1);
        return checked((uint)new DateTimeOffset(local).ToUniversalTime().ToUnixTimeSeconds());
    }

    private static (uint SecondsOfDay, int DayShift) ToUtcScheduleTime(
        TimeSpan localTime)
    {
        var local = DateTime.Today.Add(localTime);
        var utc = new DateTimeOffset(local).ToUniversalTime();
        return (
            checked((uint)utc.TimeOfDay.TotalSeconds),
            (utc.Date - local.Date).Days);
    }

    private static byte ToUtcWeekdayMask(
        IEnumerable<int> localDays,
        int dayShift) =>
        (byte)localDays.Aggregate(
            0,
            (mask, dayNumber) =>
                mask | (1 << ((dayNumber - 1 + dayShift + 7) % 7)));

    private static byte ToUtcMonthlyDay(int localDay, TimeSpan localTime)
    {
        var today = DateTime.Today;
        var day = Math.Min(localDay, DateTime.DaysInMonth(today.Year, today.Month));
        var local = new DateTime(today.Year, today.Month, day).Add(localTime);
        return (byte)new DateTimeOffset(local).ToUniversalTime().Day;
    }

    private static int ToMondayBasedDay(DayOfWeek dayOfWeek) =>
        dayOfWeek == DayOfWeek.Sunday ? 7 : (int)dayOfWeek;

    private static string WeeklyLabel(int day) =>
        day switch
        {
            1 => "周一",
            2 => "周二",
            3 => "周三",
            4 => "周四",
            5 => "周五",
            6 => "周六",
            7 => "周日",
            _ => string.Empty
        };

    private static string CompactSummary(string summary)
    {
        const int maximumCharacters = 9;
        return summary.Length <= maximumCharacters
            ? summary
            : $"{summary[..maximumCharacters]}…";
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }
}

public sealed class RepeatOptionItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public RepeatOptionItem(string label, int value, bool isSelected)
    {
        Label = label;
        Value = value;
        _isSelected = isSelected;
    }

    public string Label { get; }
    public int Value { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LightScheduleItem : INotifyPropertyChanged
{
    private bool _isEnabled;

    public LightScheduleItem(
        string timeText,
        string actionText,
        string repeatText,
        bool isEnabled,
        AgpSchedule? register = null)
    {
        TimeText = timeText;
        ActionText = actionText;
        RepeatText = repeatText;
        _isEnabled = isEnabled;
        Register = register;
    }

    public string TimeText { get; }
    public string ActionText { get; }
    public string RepeatText { get; }
    public AgpSchedule? Register { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;

            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
