using ArriOpto.BLE.Agp;

namespace ArriLight;

public sealed partial class AgpLightControlService
{
    // The saved checkbox now schedules automatic off after immediate power-on.
    // Countdown runs on the device; the APP does not keep a delayed task.
    public Task SetControlPageOnOffAsync(
        string address, bool isOn, int delaySeconds,
        CancellationToken cancellationToken = default)
    {
        ValidateControlDelay(delaySeconds);
        if (!isOn || delaySeconds == 0)
            return SetOnOffAsync(address, isOn, cancellationToken);

        AgpCommand[] commands = [AgpCommands.SetOnOff(true), AgpCommands.SetDelayOff((uint)delaySeconds)];
        return WithClientAsync(address, async handle =>
        {
            RequireCapability(handle.Capabilities, AgpCapabilities.OnOff);
            RequireCapability(handle.Capabilities, AgpCapabilities.RelativeTimer);
            await ExecuteControlBatchAsync(handle, commands, isOn: true, cancellationToken);
        }, cancellationToken);
    }

    public Task<AgpEffectResponse> SetControlPageEffectAsync(
        string address, AgpLightMode mode, ushort periodMilliseconds, int delaySeconds,
        CancellationToken cancellationToken = default)
    {
        ValidateControlDelay(delaySeconds);
        var effect = AgpCommands.SetEffect(mode, periodMilliseconds);
        var power = AgpCommands.SetOnOff(true);
        AgpCommand[] commands = delaySeconds > 0
            ? [effect, power, AgpCommands.SetDelayOff((uint)delaySeconds)]
            : [effect, power];
        return WithClientAsync(address, async handle =>
        {
            RequireCapability(handle.Capabilities, AgpCapabilities.Effects);
            RequireCapability(handle.Capabilities, AgpCapabilities.OnOff);
            if (delaySeconds > 0)
                RequireCapability(handle.Capabilities, AgpCapabilities.RelativeTimer);
            var result = await ExecuteControlBatchAsync(handle, commands, isOn: true, cancellationToken);
            return result.Steps[0].GetResponse(effect);
        }, cancellationToken);
    }

    private async Task<AgpBatchResult> ExecuteControlBatchAsync(
        AgpClientHandle handle, IReadOnlyList<AgpCommand> commands, bool isOn,
        CancellationToken cancellationToken)
    {
        using var applicationCancellation = LinkApplicationCancellation(cancellationToken);
        cancellationToken = applicationCancellation.Token;
        AgpBatchResult result;
        try
        {
            result = await handle.Client.ExecuteBatchAsync(commands, cancellationToken: cancellationToken);
        }
        catch (AgpBatchException exception)
        {
            ApplyControlBatchProgress(handle, exception.Progress, isOn);
            throw;
        }
        catch (AgpBatchCanceledException exception)
        {
            ApplyControlBatchProgress(handle, exception.Progress, isOn);
            throw;
        }

        ApplyControlBatchProgress(handle, result, isOn);
        if (!result.IsSuccess)
            EnsureSuccess(result.Steps[^1].Result!.Value);
        return result;
    }

    private void ApplyControlBatchProgress(AgpClientHandle handle, AgpBatchResult progress, bool isOn)
    {
        // A later failure cannot undo a mode or power change confirmed by the device.
        foreach (var step in progress.Steps.Where(step => step.IsSuccess))
        {
            if (step.Opcode == AgpOpcode.EffectSet)
            {
                var effect = step.GetResponse<AgpEffectResponse>();
                handle.LastState = handle.LastState with { Mode = effect.Mode };
                CacheControlState(handle, effect.PeriodMilliseconds);
            }
            else if (step.Opcode == AgpOpcode.OnOffSet)
            {
                handle.LastState = handle.LastState with { IsOn = isOn };
                CacheControlState(handle);
            }
        }
    }

    private static void ValidateControlDelay(int seconds)
    {
        if (seconds is < 0 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(seconds));
    }
}
