using ArriOpto.BLE.Agp;
using ArriOpto.BLE.Mesh;
using ArriOpto.BLE.Mesh.Models;

namespace ArriLight;

public partial class MeshDeviceControlService
{
    public long? GetGroupConnectionGeneration(string address)
    {
        lock (_lifecycleSync)
        {
            if (_applicationSuspended || _applicationClosing || _disposed || !IsSavedMeshDevice(address)) return null;
            return _sessions.TryGetValue(address, out var session) && session.Client is not null &&
                session.Transport?.IsConnected == true && _connectionService.IsConnected(address) &&
                session.Generation == _connectionService.GetConnectionGeneration(address) ? session.Generation : null;
        }
    }

    private MeshClient RequireGroupSession(string address, MeshProxySession session, long generation) =>
        GetGroupConnectionGeneration(address) == generation && session.Generation == generation && session.Client is { } client
            ? client : throw new OperationCanceledException(AppResources.Group_SessionExpired);

    public Task<bool> SetConnectedGroupCommandAsync(string address, long generation,
        DeviceGroupCommand command, Func<bool> isStillMember, CancellationToken cancellationToken = default)
    {
        command.Validate();
        return RunProxySessionAsync(address, "Mesh.Group.Set", UserPriority, async (client, node, token) =>
        {
            if (!isStillMember() || GetGroupConnectionGeneration(address) != generation)
                throw new OperationCanceledException(AppResources.Group_SessionExpired);
            var profile = GetCachedControlProfile(address) ?? throw new NotSupportedException(AppResources.Group_CapabilitiesUnknown);
            if (!command.IsSupportedBy(profile.Capabilities)) throw new NotSupportedException(AppResources.Group_Incompatible);
            if (command.Kind == DeviceGroupCommandKind.Power)
            {
                var response = await SendAndWaitAsync(client, profile.OnOffAddress!.Value, MeshOpcode.GenericOnOffStatus,
                    GenericOnOffModel.Set(command.IsOn, NextTid(address)), "Group.OnOff.Set", token);
                if (response is null || !GenericOnOffModel.TryParseStatus(response.AccessPdu, out var status))
                    throw UnknownLightingResult(address);
                PublishState(address, node, isOn: status.PresentOnOff);
                return true;
            }
            if (command.Kind != DeviceGroupCommandKind.Lighting) throw new NotSupportedException(AppResources.Group_Incompatible);
            if (command.ColorModel == AgpColorModel.Hsl)
                return await SetHslCoreAsync(client, node, address, command.Brightness, ToComponent(command.Hue), ToComponent(command.Saturation), token);
            if (command.ColorModel == AgpColorModel.Ctl)
            {
                if (command.TemperatureKelvin < profile.CtlRange.TemperatureMinimumKelvin || command.TemperatureKelvin > profile.CtlRange.TemperatureMaximumKelvin)
                    throw new ArgumentException(AppResources.Group_TemperatureRangeMismatch);
                return await SetCtlCoreAsync(client, node, address, command.Brightness, command.TemperatureKelvin, token);
            }
            return await SetBrightnessCoreAsync(client, node, address, command.Brightness, token);
        }, cancellationToken, requiredConnectionGeneration: generation);
    }
}
