using ArriOpto.BLE.Agp;

namespace ArriLight;

public sealed partial class AgpLightControlService
{
    public long? GetGroupConnectionGeneration(string address)
    {
        if (_applicationSuspended || _applicationClosing || !_repository.Contains(address) ||
            !_handles.TryGetValue(address, out var handle) || !handle.IsAuthorized || !IsCurrentHandle(handle)) return null;
        if (_runtimeEntries.TryGetValue(address, out var entry))
            lock (entry.Sync) if (entry.Exclusive || entry.AuthorizationBlocked) return null;
        return handle.ConnectionGeneration;
    }

    private AgpClientHandle RequireGroupHandle(string address, long generation)
    {
        if (GetGroupConnectionGeneration(address) != generation || !_handles.TryGetValue(address, out var handle))
            throw new OperationCanceledException(AppResources.Group_SessionExpired);
        return handle;
    }

    public Task<bool> SetConnectedGroupCommandAsync(string address, long generation,
        DeviceGroupCommand command, Func<bool> isStillMember, CancellationToken cancellationToken = default)
    {
        command.Validate();
        return WithClientAsync(address, async (handle, token) =>
        {
            if (!isStillMember() || GetGroupConnectionGeneration(address) != generation)
                throw new OperationCanceledException(AppResources.Group_SessionExpired);
            if (!command.IsSupportedBy(handle.Capabilities)) throw new NotSupportedException(AppResources.Group_Incompatible);
            if (handle.HasLightState && handle.LastState.IsLocked) throw new InvalidOperationException(AppResources.Device_Locked);
            switch (command.Kind)
            {
                case DeviceGroupCommandKind.Power:
                case DeviceGroupCommandKind.Effect:
                    var commands = new List<AgpCommand>();
                    if (command.Kind == DeviceGroupCommandKind.Effect)
                        commands.Add(AgpCommands.SetEffect(command.Effect,
                            command.Effect == AgpLightMode.Steady ? (ushort)0 : command.PeriodMilliseconds));
                    var isOn = command.Kind == DeviceGroupCommandKind.Effect || command.IsOn;
                    commands.Add(AgpCommands.SetOnOff(isOn));
                    if (isOn && command.Seconds > 0) commands.Add(AgpCommands.SetDelayOff((uint)command.Seconds));
                    await ExecuteControlBatchAsync(handle, commands, isOn, token);
                    break;
                case DeviceGroupCommandKind.Lighting:
                    var lightness = AgpColorModelCodec.BrightnessPercentToLightness16(command.Brightness);
                    if (command.ColorModel == AgpColorModel.Hsl)
                        EnsureSuccess(await handle.Client.SetHslAsync(new(lightness, RatioToUInt16(command.Hue), RatioToUInt16(command.Saturation)), cancellationToken: token));
                    else if (command.ColorModel == AgpColorModel.Ctl)
                    {
                        if (handle.CtlRange is not { } range || command.TemperatureKelvin < range.TemperatureMinimumKelvin || command.TemperatureKelvin > range.TemperatureMaximumKelvin)
                            throw new ArgumentException(AppResources.Group_TemperatureRangeMismatch);
                        EnsureSuccess(await handle.Client.SetCtlAsync(new(lightness, command.TemperatureKelvin, 0), cancellationToken: token));
                    }
                    else if (handle.Capabilities.HasFlag(AgpCapabilities.Lightness16))
                        EnsureSuccess(await handle.Client.SetLightness16Async(lightness, cancellationToken: token));
                    else EnsureSuccess(await handle.Client.SetLightnessAsync(command.Brightness, cancellationToken: token));
                    break;
                case DeviceGroupCommandKind.Timer:
                    if (command.IsOn)
                    {
                        var delay = await handle.Client.SetDelayOnAsync((uint)command.Seconds, cancellationToken: token);
                        EnsureSuccess(delay.Result); CacheRuntimeDelay(address, delay);
                    }
                    else
                    {
                        var delay = await handle.Client.SetDelayOffAsync((uint)command.Seconds, cancellationToken: token);
                        EnsureSuccess(delay.Result); CacheRuntimeDelay(address, delay);
                    }
                    break;
                case DeviceGroupCommandKind.LocalMode:
                    var response = await handle.Client.SetModeAsync(command.LocalMode!, token);
                    EnsureSuccess(response.Result); handle.LocalMode = response.Config;
                    break;
            }
            CacheControlState(handle);
            return true;
        }, cancellationToken, skipInitialLightStateRead: true, requiredConnectionGeneration: generation);
    }
}
