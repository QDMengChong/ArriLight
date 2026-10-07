using ArriOpto.BLE.Agp;

namespace ArriLight;

public sealed partial class AgpLightControlService
{
    public Task<AgpModeConfig> GetLocalModeAsync(
        string address, CancellationToken cancellationToken = default) =>
        WithClientAsync(address, async handle =>
        {
            RequireCapability(handle.Capabilities, AgpCapabilities.LocalMode);
            var response = await handle.Client.GetModeAsync(cancellationToken);
            EnsureSuccess(response.Result);
            return response.Config!;
        }, cancellationToken);

    public Task<AgpModeConfig> SetLocalModeAsync(
        string address, AgpModeConfig configuration, CancellationToken cancellationToken = default)
    {
        // Validate before preparing a BLE session. MODE changes only hardware input behavior.
        _ = AgpModelCodec.EncodeModeSet(configuration);
        return WithClientAsync(address, async handle =>
        {
            RequireCapability(handle.Capabilities, AgpCapabilities.LocalMode);
            var response = await handle.Client.SetModeAsync(configuration, cancellationToken);
            EnsureSuccess(response.Result);
            return response.Config!;
        }, cancellationToken);
    }
}
