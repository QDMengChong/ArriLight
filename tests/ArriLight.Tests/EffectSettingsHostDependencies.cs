using ArriOpto.BLE.Agp;

namespace ArriLight;

// Exercise the production dialog controller with in-memory UI and BLE boundaries.
// Android's native Dialog/SeekBar are compiled by the Android build, not rendered here.
public partial class DeviceControlPage : ContentPage
{
    private readonly AppDiagnosticService _diagnostics = new();
    private readonly DeviceRepository _repository = new();
    private AgpLightMode _effectMode = AgpLightMode.Steady;
    private bool _effectsSupported = true;
    private readonly TestOverlay EffectSettingsOverlay = new();
    private readonly TestLabel EffectSettingsTitle = new();
    private readonly TestLabel EffectSettingsPeriodLabel = new();
    private readonly Slider EffectSettingsPeriodSlider = new();

    public DeviceControlPageViewModel ViewModel { get; }
    public DeviceControlPage(string address = "AA:BB:CC:DD:EE:FF")
    {
        DeviceAddress = address;
        _repository.TryAdd("PW01", address);
        ViewModel = new TestControlViewModel(_repository, this);
        ViewModel.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = address });
        LoadEffectParameters();
        EffectSettingsPeriodSlider.ValueChanged += OnEffectSettingsPeriodChanged;
    }

    public string DeviceAddress { get; }
    public bool IsDeviceOnline { get; set; } = true;
    internal bool EffectsSupported { set => _effectsSupported = value; }
    internal bool DialogIsVisible => EffectSettingsOverlay.IsVisible;
    internal bool DialogIsOpen => _isEffectSettingsOpen;
    internal string DialogTitle => EffectSettingsTitle.Text;
    internal ushort PeriodForTest(AgpLightMode mode) => GetEffectPeriod(mode);
    internal double DialogPeriod => EffectSettingsPeriodSlider.Value;
    internal bool RejectEffect { get; set; }
    internal AgpLightMode ActiveEffect { get => _effectMode; set { _effectMode = value; ViewModel.SetEffectForTest(value); } }
    internal List<(AgpLightMode Mode, ushort Period)> SentEffects { get; } = [];
    internal Task OpenForTestAsync(AgpLightMode mode) => ShowEffectSettingsAsync(mode);
    internal Task SelectForTestAsync(AgpLightMode mode) => SetEffectAsync(mode, GetEffectPeriod(mode));
    internal void SynchronizeForTest(AgpLightMode mode, ushort period) => RememberEffectPeriod(mode, period);
    internal void ChangePeriodForTest(double value) => EffectSettingsPeriodSlider.Value = value;
    internal void ApplyForTest() => OnEffectSettingsApplyClicked(this, EventArgs.Empty);
    internal void CancelForTest() => OnEffectSettingsCancelClicked(this, EventArgs.Empty);
    internal void LeaveForTest() => CloseEffectSettings();
    internal bool BackForTest() => OnBackButtonPressed();
    internal void DisconnectForTest()
    {
        IsDeviceOnline = false;
        CloseEffectSettings();
    }

    internal Task SetEffectAsync(AgpLightMode mode, ushort period)
    {
        SentEffects.Add((mode, period));
        if (!RejectEffect)
        {
            _effectMode = mode;
            ViewModel.SetEffectForTest(mode);
            RememberEffectPeriod(mode, period);
        }
        return Task.CompletedTask;
    }

    private bool HasEffectSettingsOverlay => true;

    private Task EnsureEffectSettingsOverlayLoadedAsync(string title, ushort period)
    {
        EffectSettingsTitle.Text = title;
        EffectSettingsPeriodSlider.Value = period;
        EffectSettingsPeriodLabel.Text = $"周期长度：{period} ms";
        return Task.CompletedTask;
    }

    private void SetEffectSettingsOverlayVisible(bool visible) =>
        EffectSettingsOverlay.IsVisible = visible;
}

public sealed class ValueChangedEventArgs(double oldValue, double newValue) : EventArgs
{
    public double OldValue { get; } = oldValue;
    public double NewValue { get; } = newValue;
}

public sealed class Slider
{
    private double _value;
    public event EventHandler<ValueChangedEventArgs>? ValueChanged;
    public double Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;
            var oldValue = _value;
            _value = value;
            ValueChanged?.Invoke(this, new ValueChangedEventArgs(oldValue, value));
        }
    }
}

internal sealed class TestControlViewModel(DeviceRepository repository, DeviceControlPage page) :
    DeviceControlPageViewModel(repository, null!, null!, null!, new AppDiagnosticService(),
        new TestUiDispatcher(), new TestAppNavigator(), new TestUserInteraction())
{
    public override Task SetEffectAsync(AgpLightMode mode, ushort period) => page.SetEffectAsync(mode, period);
}
