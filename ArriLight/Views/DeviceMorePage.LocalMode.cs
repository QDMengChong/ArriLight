using System.Globalization;
using ArriOpto.BLE.Agp;

namespace ArriLight;

public partial class DeviceMorePage
{
    private bool _isAlwaysOnMode = true;
    private string _localModeSecondsText = "60";
    private bool _isLocalModeSupported;
    private bool _isLocalModeBusy;
    private string _localModeNotice = string.Empty;
    private CancellationTokenSource? _localModeCancellation;

    public bool IsAlwaysOnMode
    {
        get => _isAlwaysOnMode;
        set
        {
            if (_isAlwaysOnMode == value)
                return;
            _isAlwaysOnMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LocalModeLabel));
            OnPropertyChanged(nameof(CanEditLocalModeSeconds));
        }
    }

    public string LocalModeLabel => IsAlwaysOnMode
        ? AppResources.DeviceMode_AlwaysOn : AppResources.DeviceMode_Delayed;

    public string LocalModeSecondsText
    {
        get => _localModeSecondsText;
        set
        {
            _localModeSecondsText = value;
            OnPropertyChanged();
        }
    }

    public string LocalModeNotice
    {
        get => _localModeNotice;
        private set
        {
            _localModeNotice = value;
            OnPropertyChanged();
        }
    }

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

    public bool CanSetLocalMode => IsLocalModeSupported && !IsLocalModeBusy;
    public bool CanEditLocalModeSeconds => CanSetLocalMode && !IsAlwaysOnMode;

    private void NotifyLocalModeAvailability()
    {
        OnPropertyChanged(nameof(CanSetLocalMode));
        OnPropertyChanged(nameof(CanEditLocalModeSeconds));
    }

    private async Task LoadLocalModeAsync()
    {
        CancelLocalModeOperations();
        IsLocalModeSupported = false;
        if (_repository.GetByAddress(DeviceId) is not { IsDemo: false })
            return;

        var cancellation = new CancellationTokenSource();
        _localModeCancellation = cancellation;
        var token = cancellation.Token;
        IsLocalModeBusy = true;
        try
        {
            var configuration = await _agpLight.GetLocalModeAsync(DeviceId, token);
            token.ThrowIfCancellationRequested();
            ApplyLocalModeConfiguration(configuration);
            IsLocalModeSupported = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (NotSupportedException) { /* Older devices do not expose this configuration. */ }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                ToastService.Show(Localization.Format(nameof(AppResources.DeviceMode_LoadFailed), exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_localModeCancellation, cancellation))
                IsLocalModeBusy = false;
        }
    }

    private async void OnSetLocalModeClicked(object? sender, EventArgs e) => await SetLocalModeAsync();

    private async Task SetLocalModeAsync()
    {
        if (!CanSetLocalMode || _localModeCancellation is not { } cancellation)
            return;

        uint seconds = 0;
        if (!IsAlwaysOnMode &&
            (!uint.TryParse(LocalModeSecondsText, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
             seconds is < 1 or > AgpModelCodec.MaximumDelaySeconds))
        {
            ToastService.Show(AppResources.DeviceMode_InvalidSeconds);
            return;
        }

        var configuration = IsAlwaysOnMode
            ? new AgpModeConfig(AgpLocalMode.DelayNone)
            : new AgpModeConfig(AgpLocalMode.OnAutoOff, OffSeconds: seconds);
        var token = cancellation.Token;
        IsLocalModeBusy = true;
        try
        {
            var saved = await _agpLight.SetLocalModeAsync(DeviceId, configuration, token);
            token.ThrowIfCancellationRequested();
            ApplyLocalModeConfiguration(saved);
            ToastService.Show(AppResources.DeviceMode_SetSucceeded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                ToastService.Show(Localization.Format(nameof(AppResources.DeviceMode_SetFailed), exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_localModeCancellation, cancellation))
                IsLocalModeBusy = false;
        }
    }

    private void ApplyLocalModeConfiguration(AgpModeConfig configuration)
    {
        IsAlwaysOnMode = configuration.Mode != AgpLocalMode.OnAutoOff;
        if (configuration.Mode == AgpLocalMode.OnAutoOff)
            LocalModeSecondsText = configuration.OffSeconds.ToString(CultureInfo.InvariantCulture);
        LocalModeNotice = configuration.Mode is AgpLocalMode.DelayNone or AgpLocalMode.OnAutoOff
            ? string.Empty : AppResources.DeviceMode_OtherMode;
    }

    private void CancelLocalModeOperations()
    {
        _localModeCancellation?.Cancel();
        _localModeCancellation?.Dispose();
        _localModeCancellation = null;
        IsLocalModeBusy = false;
    }
}
