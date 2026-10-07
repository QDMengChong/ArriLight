using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using ArriOpto.BLE.Agp;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LocalModeLabel)), NotifyPropertyChangedFor(nameof(CanEditDelaySeconds))]
    private bool _isAlwaysOnMode = true;
    private bool _isLocalModeSupported;
    private bool _isLocalModeBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLocalModeNotice))]
    private string _localModeNotice = string.Empty;
    private CancellationTokenSource? _localModeCancellation;
    private Task _localModeLoadTask = Task.CompletedTask;
    private int _delaySecondsEditVersion;

    public bool SupportsTurnOnDelay => SupportedCapabilities.HasFlag(AgpCapabilities.RelativeTimer);
    public bool SupportsLocalMode => !IsDemo && SupportedCapabilities.HasFlag(AgpCapabilities.LocalMode);
    public bool HasLocalModeNotice => !string.IsNullOrEmpty(LocalModeNotice);
    public string LocalModeLabel => IsAlwaysOnMode
        ? AppResources.DeviceMode_AlwaysOn : AppResources.DeviceMode_Delayed;

    public bool IsLocalModeSupported
    {
        get => _isLocalModeSupported;
        private set
        {
            _isLocalModeSupported = value;
            OnPropertyChanged();
            NotifyLocalModeAvailability();
        }
    }

    public bool IsLocalModeBusy
    {
        get => _isLocalModeBusy;
        private set
        {
            _isLocalModeBusy = value;
            OnPropertyChanged();
            NotifyLocalModeAvailability();
        }
    }

    public bool CanSetLocalMode => SupportsLocalMode && IsLocalModeSupported && IsDeviceOnline &&
        !IsLocalModeBusy && _localModeCancellation is { IsCancellationRequested: false };
    public bool CanEditDelaySeconds => IsDeviceOnline && !IsLocalModeBusy &&
        ((SupportsTurnOnDelay && IsTurnOnDelayEnabled) || (CanSetLocalMode && !IsAlwaysOnMode));

    private void NotifyLocalModeAvailability()
    {
        OnPropertyChanged(nameof(CanSetLocalMode));
        OnPropertyChanged(nameof(CanEditDelaySeconds));
    }

    private void ResetLocalModeState()
    {
        CancelLocalModeOperations();
        IsLocalModeSupported = false;
        IsAlwaysOnMode = true;
        LocalModeNotice = string.Empty;
    }

    private async Task LoadLocalModeAsync(CancellationToken appearanceToken, int secondsVersion)
    {
        CancelLocalModeOperations();
        IsLocalModeSupported = false;
        if (!SupportsLocalMode || _repository.GetByAddress(DeviceAddress) is not { IsDemo: false })
            return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(appearanceToken);
        _localModeCancellation = cancellation;
        var token = cancellation.Token;
        IsLocalModeBusy = true;
        try
        {
            var configuration = await _agpLight.GetLocalModeAsync(DeviceAddress, token);
            token.ThrowIfCancellationRequested();
            // Preserve an edit made while connection/profile/MODE_GET was pending.
            ApplyLocalModeConfiguration(configuration, secondsVersion == _delaySecondsEditVersion);
            IsLocalModeSupported = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (NotSupportedException) { /* Older devices do not expose this configuration. */ }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                ErrorMessage = exception.Message;
                _interaction.ShowToast(Localization.Format(nameof(AppResources.DeviceMode_LoadFailed), exception.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_localModeCancellation, cancellation))
                IsLocalModeBusy = false;
        }
    }

    [RelayCommand]
    private async Task SetLocalModeAsync()
    {
        if (!CanSetLocalMode || _localModeCancellation is not { } cancellation)
            return;

        uint seconds = 0;
        if (!IsAlwaysOnMode &&
            (!uint.TryParse(TurnOnDelaySecondsText, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
             seconds is < 1 or > AgpModelCodec.MaximumDelaySeconds))
        {
            _interaction.ShowToast(AppResources.DeviceMode_InvalidSeconds);
            return;
        }

        var configuration = IsAlwaysOnMode
            ? new AgpModeConfig(AgpLocalMode.DelayNone)
            : new AgpModeConfig(AgpLocalMode.OnAutoOff, OffSeconds: seconds);
        var secondsVersion = _delaySecondsEditVersion;
        var token = cancellation.Token;
        IsLocalModeBusy = true;
        try
        {
            var saved = await _agpLight.SetLocalModeAsync(DeviceAddress, configuration, token);
            token.ThrowIfCancellationRequested();
            ApplyLocalModeConfiguration(saved, secondsVersion == _delaySecondsEditVersion);
            _interaction.ShowToast(AppResources.DeviceMode_SetSucceeded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                ErrorMessage = exception.Message;
                _interaction.ShowToast(Localization.Format(nameof(AppResources.DeviceMode_SetFailed), exception.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_localModeCancellation, cancellation))
                IsLocalModeBusy = false;
        }
    }

    private void ApplyLocalModeConfiguration(AgpModeConfig configuration, bool updateSeconds)
    {
        IsAlwaysOnMode = configuration.Mode != AgpLocalMode.OnAutoOff;
        if (updateSeconds && configuration.Mode == AgpLocalMode.OnAutoOff)
            TurnOnDelaySecondsText = configuration.OffSeconds.ToString(CultureInfo.InvariantCulture);
        LocalModeNotice = configuration.Mode is AgpLocalMode.DelayNone or AgpLocalMode.OnAutoOff
            ? string.Empty : AppResources.DeviceMode_OtherMode;
    }

    private void CancelLocalModeOperations()
    {
        var cancellation = _localModeCancellation;
        _localModeCancellation = null;
        IsLocalModeBusy = false;
        _ = CancellationDispatch.Request(cancellation, _diagnostics, "Control.LocalMode", dispose: true, address: DeviceAddress);
    }
}
