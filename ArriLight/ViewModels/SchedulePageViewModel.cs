using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using ArriOpto.BLE.Agp;

namespace ArriLight.ViewModels;

public partial class SchedulePageViewModel : ViewModelBase
{
    private readonly AppDiagnosticService _diagnostics;
    private readonly AgpLightControlService _agpLight;
    private readonly DeviceRepository _repository;
    private readonly HashSet<int> _weeklySelections = [];
    private readonly HashSet<int> _monthlySelections = [];
    [ObservableProperty]
    private string _deviceName = AppResources.Device_DefaultName;
    private string _deviceAddress = string.Empty;
    [ObservableProperty]
    private string _repeatSelectionSummary = AppResources.Schedule_Daily;
    [ObservableProperty]
    private string _repeatSelectionDisplayText = AppResources.Schedule_Daily;
    private int _confirmedRepeatIndex = 1;
    private bool _isInitializing = true;
    private bool _isActionOn = true;
    private CancellationTokenSource? _scheduleLoadCancellation;
    private bool _isLoadingSchedules;
    private bool _hasLoadedSchedules;
    private bool _supportsSchedules;
    private int _maximumScheduleSlots = AgpModelCodec.MaximumScheduleSlots;
    private string? _scheduleStatusText;

    public string ScheduleCountText => _scheduleStatusText ?? Localization.Format(nameof(AppResources.Schedule_Count), Schedules.Count);

    public IReadOnlyList<string> RepeatModes { get; } =
        [AppResources.Schedule_Once, AppResources.Schedule_Daily, AppResources.Schedule_Weekly, AppResources.Schedule_Monthly];

    public ObservableCollection<RepeatOptionItem> RepeatOptions { get; } = [];

    public ObservableCollection<LightScheduleItem> Schedules { get; } = [];

    private readonly IUserInteraction _interaction;
    [ObservableProperty] private int _repeatModeIndex = 1;
    [ObservableProperty] private TimeSpan? _scheduleTime = new TimeSpan(7, 30, 0);
    [ObservableProperty] private bool _isAddScheduleVisible;
    [ObservableProperty] private bool _isRepeatPopupVisible;
    [ObservableProperty] private string _repeatPopupTitle = string.Empty;
    public bool CanEditRepeatDetail => _confirmedRepeatIndex is 2 or 3;
    public bool IsActionOn => _isActionOn;
    public event Action? ActionVisualChanged;
    public SchedulePageViewModel(AgpLightControlService agpLight, DeviceRepository repository,
        AppDiagnosticService diagnostics, IUserInteraction interaction)
    {
        _agpLight = agpLight; _repository = repository; _diagnostics = diagnostics; _interaction = interaction;
        Schedules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ScheduleCountText));
        UpdateRepeatSummary(); _isInitializing = false;
    }

    public override async Task OnAppearingAsync()
    {
        _scheduleLoadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scheduleLoadCancellation = cancellation;
        _isLoadingSchedules = true;
        _hasLoadedSchedules = false;
        _scheduleStatusText = AppResources.Schedule_Loading;
        OnPropertyChanged(nameof(ScheduleCountText));

        try
        {
            var device = _repository.GetByAddress(_deviceAddress) ??
                throw new InvalidOperationException(AppResources.Device_RecordMissing);
            var schedules = device.IsDemo
                ? Array.Empty<AgpSchedule>()
                : await _agpLight.GetSchedulesAsync(_deviceAddress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_scheduleLoadCancellation, cancellation))
                return;

            _supportsSchedules = !device.IsDemo &&
                _repository.GetCapabilities(_deviceAddress) is { } capabilities &&
                capabilities.HasFlag(AgpCapabilities.Scheduler);
            _maximumScheduleSlots = _agpLight.GetCachedScheduleSlotCount(_deviceAddress);
            var items = schedules.Select(CreateScheduleItem).ToArray();
            Schedules.Clear();
            foreach (var item in items)
                Schedules.Add(item);
            _hasLoadedSchedules = true;
            _scheduleStatusText = device.IsDemo ? AppResources.Schedule_DemoUnavailable :
                _supportsSchedules ? null : AppResources.Schedule_Unsupported;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A page that has been left must not apply an unfinished read.
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            if (!ReferenceEquals(_scheduleLoadCancellation, cancellation) ||
                cancellation.IsCancellationRequested)
                return;
            _scheduleStatusText = AppResources.Schedule_NotLoaded;
            _diagnostics.Error("Light/Schedule", $"读取设备日程失败：{DeviceName}。", exception);
            await _interaction.AlertAsync(AppResources.Schedule_LoadFailed, exception.Message, AppResources.Common_Ok);
        }
        finally
        {
            if (ReferenceEquals(_scheduleLoadCancellation, cancellation))
            {
                _scheduleLoadCancellation = null;
                _isLoadingSchedules = false;
                OnPropertyChanged(nameof(ScheduleCountText));
            }
            cancellation.Dispose();
        }
    }

    public override void OnDisappearing()
    {
        _scheduleLoadCancellation?.Cancel();
        IsAddScheduleVisible = false;
        IsRepeatPopupVisible = false;

    }

    public override void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("name", out var value) && value is not null)
            DeviceName = Uri.UnescapeDataString(value.ToString()!);
        if (query.TryGetValue("address", out var address) && address is not null)
            _deviceAddress = Uri.UnescapeDataString(address.ToString()!);
    }

    partial void OnRepeatModeIndexChanged(int value)
    {
        if (_isInitializing || RepeatModeIndex < 0)
            return;

        var selectedIndex = RepeatModeIndex;
        if (selectedIndex is 2 or 3)
        {
            ShowRepeatPopup(selectedIndex);
            return;
        }

        _confirmedRepeatIndex = selectedIndex;
        UpdateRepeatSummary();
    }

    [RelayCommand]
    private void RepeatDetail()
    {
        if (_confirmedRepeatIndex is 2 or 3)
            ShowRepeatPopup(_confirmedRepeatIndex);
    }

    private void ShowRepeatPopup(int repeatIndex)
    {
        RepeatOptions.Clear();

        if (repeatIndex == 2)
        {
            RepeatPopupTitle = AppResources.Schedule_SelectWeekdays;
            var labels = new[] { AppResources.Schedule_Monday, AppResources.Schedule_Tuesday, AppResources.Schedule_Wednesday, AppResources.Schedule_Thursday, AppResources.Schedule_Friday, AppResources.Schedule_Saturday, AppResources.Schedule_Sunday };
            if (_weeklySelections.Count == 0)
                _weeklySelections.Add(ToMondayBasedDay(DateTime.Today.DayOfWeek));

            for (var index = 1; index <= labels.Length; index++)
                RepeatOptions.Add(new(labels[index - 1], index, _weeklySelections.Contains(index)));
        }
        else
        {
            RepeatPopupTitle = AppResources.Schedule_SelectMonthDays;
            if (_monthlySelections.Count == 0)
                _monthlySelections.Add(DateTime.Today.Day);

            for (var day = 1; day <= 31; day++)
                RepeatOptions.Add(new(Localization.Format(nameof(AppResources.Schedule_DayNumber), day), day, _monthlySelections.Contains(day)));
        }

        IsRepeatPopupVisible = true;
    }

    [RelayCommand]
    private void RepeatPopupCancel()
    {
        IsRepeatPopupVisible = false;
        RestoreConfirmedRepeatMode();
    }

    [RelayCommand]
    private async Task RepeatPopupConfirmAsync()
    {
        var selectedValues = RepeatOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Value)
            .ToArray();

        if (selectedValues.Length == 0)
        {
            await _interaction.AlertAsync(AppResources.Schedule_SelectDate, AppResources.Schedule_DateRequired, AppResources.Common_Ok);
            return;
        }

        var selectedMode = RepeatModeIndex;
        var target = selectedMode == 2 ? _weeklySelections : _monthlySelections;
        target.Clear();
        target.UnionWith(selectedValues);

        _confirmedRepeatIndex = selectedMode;
        IsRepeatPopupVisible = false;
        UpdateRepeatSummary();
    }

    private void RestoreConfirmedRepeatMode()
    {
        _isInitializing = true;
        RepeatModeIndex = _confirmedRepeatIndex;
        _isInitializing = false;
        UpdateRepeatSummary();
    }

    private void UpdateRepeatSummary()
    {
        OnPropertyChanged(nameof(CanEditRepeatDetail));
        RepeatSelectionSummary = _confirmedRepeatIndex switch
        {
            0 => AppResources.Schedule_Once,
            1 => AppResources.Schedule_Daily,
            2 when _weeklySelections.Count > 0 =>
                string.Join(AppResources.Common_ListSeparator, _weeklySelections.Order().Select(WeeklyLabel)),
            2 => AppResources.Schedule_ChooseWeekday,
            3 when _monthlySelections.Count > 0 =>
                string.Join(AppResources.Common_ListSeparator, _monthlySelections.Order().Select(day => Localization.Format(nameof(AppResources.Schedule_DayNumber), day))),
            3 => AppResources.Schedule_ChooseDate,
            _ => AppResources.Schedule_Daily
        };
        RepeatSelectionDisplayText = CompactSummary(RepeatSelectionSummary);
    }

    [RelayCommand]
    private void ActionOn()
    {
        _isActionOn = true;
        UpdateActionButtons();
    }

    [RelayCommand]
    private void ActionOff()
    {
        _isActionOn = false;
        UpdateActionButtons();
    }

    private void UpdateActionButtons() { OnPropertyChanged(nameof(IsActionOn)); ActionVisualChanged?.Invoke(); }

    /// <summary>标题栏“+ 添加”：打开添加定时弹层。</summary>
    [RelayCommand]
    private void AddSchedule()
    {
        if (!CanModifySchedules())
            return;
        _isInitializing = true;
        RepeatModeIndex = _confirmedRepeatIndex;
        ScheduleTime = new TimeSpan(7, 30, 0);
        UpdateActionButtons();
        _isInitializing = false;
        IsAddScheduleVisible = true;
    }

    [RelayCommand]
    private void AddScheduleCancel() =>
        IsAddScheduleVisible = false;

    [RelayCommand]
    private async Task AddScheduleConfirmAsync()
    {
        if (!CanModifySchedules())
            return;
        var occupiedSlots = Schedules.Select(schedule => schedule.Register.Slot).ToHashSet();
        var index = Enumerable.Range(0, _maximumScheduleSlots)
            .FirstOrDefault(slot => !occupiedSlots.Contains((byte)slot), -1);
        if (index < 0)
        {
            await _interaction.AlertAsync(
                AppResources.Schedule_Full,
                Localization.Format(nameof(AppResources.Schedule_AvailableSlots), _maximumScheduleSlots),
                AppResources.Common_Ok);
            return;
        }

        var time = ScheduleTime ?? DateTime.Now.TimeOfDay;
        var register = BuildRegister((byte)index, time, _isActionOn);
        if (!await TrySendScheduleAsync(register))
        {
            await _interaction.AlertAsync(AppResources.Common_SendFailed, AppResources.Schedule_SendFailed, AppResources.Common_Ok);
            return;
        }

        Schedules.Add(CreateScheduleItem(register));
        IsAddScheduleVisible = false;
    }

    [RelayCommand]
    private async Task DeleteScheduleAsync(LightScheduleItem schedule)
    {
        if (!CanModifySchedules())
            return;

        var confirmed = await _interaction.ConfirmAsync(
            AppResources.Schedule_Delete,
            Localization.Format(nameof(AppResources.Schedule_DeleteConfirmation), schedule.TimeText, schedule.ActionText),
            AppResources.Common_Delete,
            AppResources.Common_Cancel);

        if (!confirmed)
            return;

        var slot = schedule.Register.Slot;
        try
        {
            await _agpLight.DeleteScheduleAsync(_deviceAddress, slot);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            _diagnostics.Error(
                "Light/Schedule",
                $"删除 AGP 日程失败：{DeviceName}。",
                exception);
            await _interaction.AlertAsync(AppResources.Common_DeleteFailed, AppResources.Schedule_DeleteRejected, AppResources.Common_Ok);
            return;
        }
        Schedules.Remove(schedule);
    }

    public async Task<bool> SetScheduleEnabledAsync(LightScheduleItem schedule, bool enabled)
    {
        if (schedule.IsEnabled == enabled)
            return true;
        if (!CanModifySchedules())
            return false;
        if (!await TrySendScheduleAsync(schedule.Register with { IsEnabled = enabled }))
            return false;
        schedule.IsEnabled = enabled;
        return true;
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
            ErrorMessage = exception.Message;
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

    private bool CanModifySchedules()
    {
        if (_isLoadingSchedules)
            _interaction.ShowToast(AppResources.Schedule_WaitForLoad);
        else if (!_hasLoadedSchedules)
            _interaction.ShowToast(AppResources.Schedule_ReloadRequired);
        else if (!_supportsSchedules)
            _interaction.ShowToast(AppResources.Schedule_DeviceUnsupported);
        else
            return true;
        return false;
    }

    private static LightScheduleItem CreateScheduleItem(AgpSchedule schedule)
    {
        var utcDate = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Utc);
        var utc = schedule.Type == AgpScheduleType.Once
            ? DateTimeOffset.FromUnixTimeSeconds(schedule.TimeValue)
            : new DateTimeOffset(utcDate.AddSeconds(schedule.TimeValue));
        var local = utc.ToLocalTime();
        var dayShift = (local.Date - utc.Date).Days;
        var repeatText = schedule.Type switch
        {
            AgpScheduleType.Once => Localization.Format(nameof(AppResources.Schedule_OnceDate), local),
            AgpScheduleType.Daily => AppResources.Schedule_Daily,
            AgpScheduleType.Weekly => string.Join(AppResources.Common_ListSeparator, Enumerable.Range(0, 7)
                .Where(day => (schedule.Selector & (1 << day)) != 0)
                .Select(day => ((day + dayShift + 7) % 7) + 1)
                .Order()
                .Select(WeeklyLabel)),
            AgpScheduleType.Monthly => FormatMonthlyRepeat(schedule.Selector, dayShift),
            _ => throw new InvalidOperationException(AppResources.Schedule_InvalidRepeatType)
        };
        return new LightScheduleItem(
            local.ToString("HH:mm"),
            schedule.Action == AgpScheduleAction.On ? AppResources.Schedule_ActionOn : AppResources.Schedule_ActionOff,
            repeatText,
            schedule.IsEnabled,
            schedule);
    }

    private static string FormatMonthlyRepeat(byte utcDay, int localDayShift)
    {
        if (localDayShift < 0 && utcDay == 1)
            return AppResources.Schedule_LastDay;
        // A UTC date near the end of a month can become either the following
        // local date or the first of the next month. Retain the UTC rule rather
        // than misrepresenting it as a single fixed local day of the month.
        if (localDayShift > 0 && utcDay >= 28)
            return Localization.Format(nameof(AppResources.Schedule_UtcMonthlyDay), utcDay);
        return Localization.Format(nameof(AppResources.Schedule_MonthlyDay), utcDay + localDayShift);
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
            1 => AppResources.Schedule_Monday,
            2 => AppResources.Schedule_Tuesday,
            3 => AppResources.Schedule_Wednesday,
            4 => AppResources.Schedule_Thursday,
            5 => AppResources.Schedule_Friday,
            6 => AppResources.Schedule_Saturday,
            7 => AppResources.Schedule_Sunday,
            _ => string.Empty
        };

    private static string CompactSummary(string summary)
    {
        const int maximumCharacters = 9;
        return summary.Length <= maximumCharacters
            ? summary
            : $"{summary[..maximumCharacters]}…";
    }

}

public sealed partial class RepeatOptionItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public RepeatOptionItem(string label, int value, bool isSelected)
    {
        Label = label;
        Value = value;
        _isSelected = isSelected;
    }

    public string Label { get; }

    public int Value { get; }

}

public sealed partial class LightScheduleItem : ObservableObject
{
    [ObservableProperty] private bool _isEnabled;

    public LightScheduleItem(
        string timeText,
        string actionText,
        string repeatText,
        bool isEnabled,
        AgpSchedule register)
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

    public AgpSchedule Register { get; }

}
