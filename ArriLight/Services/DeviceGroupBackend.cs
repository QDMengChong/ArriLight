using ArriOpto.BLE.Agp;
using System.Collections.Concurrent;

namespace ArriLight;

public sealed class DeviceGroupBackend : IDeviceGroupBackend
{
    private readonly AgpLightControlService _agp;
    private readonly MeshDeviceControlService _mesh;
    private readonly ConcurrentDictionary<string, DeviceGroupMemberSnapshot> _demoStates = new(StringComparer.OrdinalIgnoreCase);
    public event EventHandler? Changed;

    public DeviceGroupBackend(AgpLightControlService agp, MeshDeviceControlService mesh)
    {
        _agp = agp; _mesh = mesh;
        agp.DeviceStatusChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        agp.RuntimeStateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        mesh.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        mesh.RuntimeStateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public DeviceGroupMemberSnapshot Read(SavedDevice device)
    {
        if (device.IsDemo)
            return _demoStates.GetOrAdd(device.Address, _ => new(device, AgpCapabilities.OnOff | AgpCapabilities.Lightness | AgpCapabilities.Effects |
                AgpCapabilities.RelativeTimer | AgpCapabilities.LocalMode, null, 0,
                IsOn: device.Address.EndsWith("01", StringComparison.Ordinal), Brightness: 100,
                Detail: AppResources.Group_Demo)) with { Device = device };
        if (device.EffectiveProtocol == BluetoothDeviceProtocol.Mesh)
        {
            var profile = _mesh.GetCachedControlProfile(device.Address);
            var state = _mesh.GetCachedState(device.Address);
            return new(device, profile?.Capabilities ?? device.Capabilities, profile?.CtlRange,
                _mesh.GetGroupConnectionGeneration(device.Address), state?.IsOn, state?.LightnessPercent,
                state?.Hue / (double)ushort.MaxValue, state?.Saturation / (double)ushort.MaxValue,
                state?.TemperatureKelvin, state?.ActiveColorModel ?? AgpColorModel.None,
                Detail: _mesh.GetRuntimeSnapshot(device.Address)?.Error);
        }
        var status = _agp.GetCachedDeviceStatus(device.Address);
        var agpProfile = status?.Profile;
        double? hue = null, saturation = null; ushort? temperature = null;
        var color = AgpColorModel.None;
        byte? brightness = status?.LightState is { } light ? light.IsOn && light.PresentBrightness > 0
            ? light.PresentBrightness : light.LastNonzeroBrightness : null;
        switch (agpProfile?.ColorState)
        {
            case AgpHslState hsl:
                hue = hsl.Present.Hue / (double)ushort.MaxValue; saturation = hsl.Present.Saturation / (double)ushort.MaxValue;
                color = hsl.ActiveModel; brightness = (byte)AgpColorModelCodec.Lightness16ToBrightnessPercent(hsl.Present.Lightness); break;
            case AgpCtlState ctl:
                temperature = ctl.Present.TemperatureKelvin; color = ctl.ActiveModel;
                brightness = (byte)AgpColorModelCodec.Lightness16ToBrightnessPercent(ctl.Present.Lightness); break;
            case AgpLightness16State l:
                brightness = (byte)AgpColorModelCodec.Lightness16ToBrightnessPercent(l.Present); break;
        }
        return new(device, agpProfile?.Capabilities ?? device.Capabilities, agpProfile?.CtlRange,
            _agp.GetGroupConnectionGeneration(device.Address), status?.LightState?.IsOn, brightness,
            hue, saturation, temperature, color, agpProfile?.EffectMode ?? AgpLightMode.Steady,
            agpProfile?.EffectPeriodMilliseconds, _agp.GetCachedLocalMode(device.Address), status?.LightState?.IsLocked == true,
            _agp.GetRuntimeSnapshot(device.Address)?.Error);
    }

    public Task ExecuteAsync(DeviceGroupMemberSnapshot member, DeviceGroupCommand command,
        Func<bool> isStillMember, CancellationToken cancellationToken)
    {
        if (member.Device.IsDemo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isStillMember()) throw new OperationCanceledException();
            _demoStates.AddOrUpdate(member.Device.Address, member, (_, previous) => command.Kind switch
            {
                DeviceGroupCommandKind.Power => previous with { IsOn = command.IsOn },
                DeviceGroupCommandKind.Lighting => previous with { Brightness = command.Brightness },
                DeviceGroupCommandKind.Effect => previous with { IsOn = true, Effect = command.Effect, EffectPeriodMilliseconds = command.PeriodMilliseconds },
                DeviceGroupCommandKind.LocalMode => previous with { LocalMode = command.LocalMode },
                _ => previous
            });
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        var generation = member.ConnectionGeneration ?? throw new OperationCanceledException(AppResources.Group_SessionExpired);
        return member.Device.EffectiveProtocol == BluetoothDeviceProtocol.Mesh
            ? _mesh.SetConnectedGroupCommandAsync(member.Device.Address, generation, command, isStillMember, cancellationToken)
            : _agp.SetConnectedGroupCommandAsync(member.Device.Address, generation, command, isStillMember, cancellationToken);
    }
}
