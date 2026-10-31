using ArriOpto.BLE.Agp;

namespace ArriLight;

public sealed class AgpFactoryResetUnconfirmedException(Exception innerException)
    : Exception(AppResources.Device_ResetUnconfirmedMessage, innerException);

public sealed partial class AgpLightControlService
{
    /// <summary>独占目标设备发送一次恢复出厂请求；返回仅表示设备已持久化受理。</summary>
    public async Task FactoryResetAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var device = _repository.GetByAddress(address);
        if (device is null || device.IsDemo || device.EffectiveProtocol != BluetoothDeviceProtocol.Agp ||
            device.Capabilities?.HasFlag(AgpCapabilities.FactoryReset) != true)
            throw new NotSupportedException(AppResources.Device_ResetUnsupported);

        var accepted = false;
        var unconfirmed = false;
        async Task<bool> ResetAsync(CancellationToken token)
        {
            try
            {
                var handle = await GetOrCreateClientAsync(address, token, skipInitialLightStateRead: true);
                RequireCapability(handle.Capabilities, AgpCapabilities.FactoryReset);
                // 确认最新锁定状态，不能用页面的历史快照绕过运输锁。
                var state = await handle.Client.ReadLightStateAsync(token);
                if (state.IsLocked)
                    throw new InvalidOperationException(AppResources.Device_ResetLocked);

                try
                {
                    var response = await handle.Client.FactoryResetAsync(token);
                    EnsureSuccess(response.Result);
                    accepted = true;
                }
                catch (Exception exception) when (exception.Data["AgpResult"] is null)
                {
                    // 写入或响应异常不能证明设备未受理，禁止后台重连重放此操作。
                    unconfirmed = true;
                    throw new AgpFactoryResetUnconfirmedException(exception);
                }
                return true;
            }
            finally
            {
                if (accepted || unconfirmed)
                    await FinishFactoryResetSessionAsync(address, accepted);
            }
        }

        try
        {
            if (_runtimeStarted)
            {
                await RunRuntimeExclusiveAsync(address, AppResources.Device_Resetting,
                    ResetAsync, cancellationToken, resumePreparation: true);
            }
            else
            {
                EnsureApplicationForeground();
                using var lifetime = LinkApplicationCancellation(cancellationToken);
                await _operationLock.WaitAsync(lifetime.Token);
                try { await ResetAsync(lifetime.Token); }
                finally { _operationLock.Release(); }
            }
        }
        finally
        {
            if ((accepted || unconfirmed) && _runtimeEntries.TryGetValue(address, out var entry))
            {
                lock (entry.Sync)
                {
                    entry.Stage = accepted ? AppResources.Device_ResetAccepted : AppResources.Device_ResetUnconfirmed;
                    entry.Error = unconfirmed ? AppResources.Device_ResetUnconfirmedMessage : null;
                }
                PublishRuntime(entry);
            }
        }
    }

    private async Task FinishFactoryResetSessionAsync(string address, bool accepted)
    {
        var entry = _runtimeEntries.GetOrAdd(address, key => new(key));
        lock (entry.Sync)
        {
            entry.Online = false;
            entry.AuthorizationBlocked = true;
            entry.BlockedToken = _repository.GetToken(address);
            entry.RelativeDelayOn = null;
            entry.RelativeDelayOff = null;
            entry.Clock = null;
            foreach (var step in entry.Steps.Keys.ToArray())
                entry.Steps[step] = new(DeviceRuntimeStepStatus.Invalidated, DateTimeOffset.UtcNow);
        }
        if (accepted)
        {
            try { _repository.ClearToken(address); }
            catch (Exception exception)
            {
                // 设备已经受理，不能因本地凭据清理失败而提示设备重置失败。
                _diagnostics.Warning("AGP/FactoryReset", $"清理失效凭据失败：device={address}，{exception.Message}");
            }
        }
        lock (entry.Sync) entry.BlockedToken = _repository.GetToken(address);
        _connectionService.ReconnectQueue.Remove(address);
        _runtimeSchedules.TryRemove(address, out _);
        _scheduleSlotCounts.TryRemove(address, out _);
        RecordDeviceOffline(address);
        _deviceStatuses.TryRemove(address, out _);
        try
        {
            await DisposeHandleAsync(address);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _connectionService.DisconnectDeviceAsync(address, cleanup.Token);
        }
        catch (Exception exception)
        {
            _diagnostics.Warning("AGP/FactoryReset", $"释放重置会话失败：device={address}，{exception.Message}");
        }
    }
}
