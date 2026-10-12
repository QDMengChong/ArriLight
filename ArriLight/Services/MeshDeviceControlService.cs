using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using ArriOpto.BLE.Mesh;
using ArriOpto.BLE.Mesh.Models;

namespace ArriLight;

/// <summary>
/// 已配网 Mesh 设备的日常控制（开关与亮度）。
/// </summary>
/// <remarks>
/// Mesh 与 AGP 共用应用级设备调度器，但保留各自的协议适配层。
/// Mesh Proxy 会话按地址复用：第一次操作建立 GATT、MTU、通知和 Proxy Filter，
/// 后续命令直接复用同一会话；应用进入后台或关闭时由生命周期显式释放。
///
/// 协议事实（来自 PW01-MESH 固件）：Generic OnOff Server 注册并自动绑定 AppKey；
/// Light Lightness Server 虽被 HSL Server 内部注册，但没有被绑定 AppKey，直接下发
/// 会被节点静默丢弃。因此亮度经由已绑定的 <c>LightHslModel</c> 只改写 Lightness
/// 分量，色相与饱和度沿用设备当前值。
/// </remarks>
public class MeshDeviceControlService : IAsyncDisposable
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(20);
    private const int UserPriority = 1;
    private const int StatusPriority = 3;

    private static readonly IReadOnlyDictionary<DeviceRuntimeStep, DeviceRuntimeStepResult> MeshSteps =
        new ReadOnlyDictionary<DeviceRuntimeStep, DeviceRuntimeStepResult>(
            Enum.GetValues<DeviceRuntimeStep>().ToDictionary(
                step => step,
                step => new DeviceRuntimeStepResult(
                    DeviceRuntimeStepStatus.NotApplicable,
                    DateTimeOffset.UtcNow)));

    private readonly BluetoothConnectionService _connectionService = null!;
    private readonly MeshNetworkManager _networkManager;
    private readonly AppDiagnosticService _diagnostics;
    private readonly DeviceWorkScheduler _work;
    private readonly ConcurrentDictionary<string, MeshProxySession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MeshDeviceState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ushort, (ushort Hue, ushort Saturation)> _hslMemory = [];
    private readonly ConcurrentDictionary<string, byte> _transactionIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MeshRuntimeEntry> _runtimeEntries =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public MeshDeviceControlService(
        BluetoothConnectionService connectionService,
        MeshNetworkManager networkManager,
        AppDiagnosticService diagnostics,
        DeviceWorkScheduler? work = null)
    {
        _connectionService = connectionService;
        _networkManager = networkManager;
        _diagnostics = diagnostics;
        _work = work ?? new DeviceWorkScheduler();
        if (_connectionService is not null)
            _connectionService.StateChanged += OnConnectionStateChanged;
    }

    /// <summary>最近一次成功取样到的设备状态，供 UI 首帧展示。</summary>
    public event EventHandler<MeshDeviceState>? StateChanged;

    /// <summary>与 AGP Runtime 相同的统一运行状态事件。</summary>
    public event EventHandler<DeviceRuntimeChange>? RuntimeStateChanged;

    public MeshDeviceState? GetCachedState(string address) =>
        _states.GetValueOrDefault(address);

    public DeviceRuntimeSnapshot? GetRuntimeSnapshot(string address)
    {
        if (!_runtimeEntries.TryGetValue(address, out var entry))
            return null;
        lock (entry.Sync)
            return entry.GetSnapshot(address);
    }

    /// <summary>
    /// 读取设备当前开关状态。状态查询复用现有 Proxy 会话。
    /// </summary>
    public async Task<bool?> GetOnOffStateAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunProxySessionAsync<bool?>(
                address,
                "Mesh.OnOff.Get",
                StatusPriority,
                async (client, node, token) =>
                {
                    var response = await SendAndWaitAsync(
                        client,
                        node,
                        MeshOpcode.GenericOnOffStatus,
                        GenericOnOffModel.Get(),
                        "OnOff.Get",
                        token);
                    if (response is null ||
                        !GenericOnOffModel.TryParseStatus(response.AccessPdu, out var status))
                    {
                        MarkRuntimeUnknown(address, "OnOff 状态未知");
                        return null;
                    }

                    PublishState(address, node, isOn: status.PresentOnOff);
                    return status.PresentOnOff;
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Warning(
                "Mesh/Control",
                $"读取 Mesh 开关状态失败：device={address}，{exception.Message}");
            return null;
        }
    }

    /// <summary>下发绝对开关状态并等待设备确认。</summary>
    public async Task<bool> SetOnOffAsync(
        string address,
        bool isOn,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunProxySessionAsync(
                address,
                $"Mesh.OnOff.Set({(isOn ? "On" : "Off")})",
                UserPriority,
                async (client, node, token) =>
                {
                    var response = await SendAndWaitAsync(
                        client,
                        node,
                        MeshOpcode.GenericOnOffStatus,
                        GenericOnOffModel.Set(isOn, NextTid(address)),
                        $"OnOff.Set({(isOn ? "On" : "Off")})",
                        token);
                    if (response is not null &&
                        GenericOnOffModel.TryParseStatus(response.AccessPdu, out var status))
                    {
                        PublishState(address, node, isOn: status.PresentOnOff);
                        return status.PresentOnOff;
                    }

                    // 命令可能已经到达设备，但缺少 Status 时只能记录未知结果。
                    PublishState(address, node, isOn: isOn);
                    MarkRuntimeUnknown(address, "OnOff.Set 未收到 Status");
                    return isOn;
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Warning(
                "Mesh/Control",
                $"设置 Mesh 开关失败：device={address}，{exception.Message}");
            throw;
        }
    }

    /// <summary>
    /// 用户按下开关按钮的语义：优先使用最近确认状态；没有状态时在同一
    /// Proxy 会话内先查询，再发送绝对 OnOff Set，避免两次完整连接。
    /// </summary>
    public async Task<bool> ToggleOrTurnOnAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunProxySessionAsync(
                address,
                "Mesh.OnOff.Toggle",
                UserPriority,
                async (client, node, token) =>
                {
                    var current = GetCachedState(address)?.IsOn;
                    if (current is null)
                    {
                        var response = await SendAndWaitAsync(
                            client,
                            node,
                            MeshOpcode.GenericOnOffStatus,
                            GenericOnOffModel.Get(),
                            "OnOff.Get",
                            token);
                        if (response is not null &&
                            GenericOnOffModel.TryParseStatus(response.AccessPdu, out var status))
                        {
                            current = status.PresentOnOff;
                            PublishState(address, node, isOn: current);
                        }
                    }

                    var desired = current is { } known ? !known : true;
                    _diagnostics.Information(
                        "Mesh/Control",
                        $"开关决策：device={address}，current={(current is { } value ? (value ? "On" : "Off") : "未知")}，desired={(desired ? "On" : "Off")}。");

                    var setResponse = await SendAndWaitAsync(
                        client,
                        node,
                        MeshOpcode.GenericOnOffStatus,
                        GenericOnOffModel.Set(desired, NextTid(address)),
                        $"OnOff.Set({(desired ? "On" : "Off")})",
                        token);
                    if (setResponse is not null &&
                        GenericOnOffModel.TryParseStatus(setResponse.AccessPdu, out var setStatus))
                    {
                        PublishState(address, node, isOn: setStatus.PresentOnOff);
                        return setStatus.PresentOnOff;
                    }

                    PublishState(address, node, isOn: desired);
                    MarkRuntimeUnknown(address, "OnOff.Toggle 未收到 Status");
                    return desired;
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Warning(
                "Mesh/Control",
                $"Mesh 开关操作失败：device={address}，{exception.Message}");
            throw;
        }
    }

    /// <summary>读取当前亮度百分比；不可用时返回 null。</summary>
    public async Task<byte?> GetLightnessPercentAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunProxySessionAsync<byte?>(
                address,
                "Mesh.Hsl.Get",
                StatusPriority,
                async (client, node, token) =>
                {
                    var response = await SendAndWaitAsync(
                        client,
                        node,
                        MeshOpcode.LightHslStatus,
                        LightHslModel.Get(),
                        "HSL.Get",
                        token);
                    if (response is null ||
                        !LightHslModel.TryParseStatus(response.AccessPdu, out var status))
                    {
                        MarkRuntimeUnknown(address, "HSL 状态未知");
                        return null;
                    }

                    RememberHsl(node.UnicastAddress, status.Hue, status.Saturation);
                    var percent = ToPercent(status.Lightness);
                    PublishState(address, node, lightnessPercent: percent);
                    return percent;
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Warning(
                "Mesh/Control",
                $"读取 Mesh 亮度失败：device={address}，{exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 设置亮度百分比。经由 Light HSL Set 只改写 Lightness 分量，
    /// 色相/饱和度沿用设备当前值。
    /// </summary>
    public async Task<bool> SetLightnessAsync(
        string address,
        byte brightnessPercent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunProxySessionAsync(
                address,
                $"Mesh.Hsl.Set({brightnessPercent}%)",
                UserPriority,
                async (client, node, token) =>
                {
                    var (hue, saturation) = await ResolveHslAsync(client, node, token);
                    var lightness = ToLightness(brightnessPercent);
                    var response = await SendAndWaitAsync(
                        client,
                        node,
                        MeshOpcode.LightHslStatus,
                        LightHslModel.Set(lightness, hue, saturation, NextTid(address)),
                        $"HSL.Set({brightnessPercent}%)",
                        token);
                    if (response is not null &&
                        LightHslModel.TryParseStatus(response.AccessPdu, out var status))
                    {
                        RememberHsl(node.UnicastAddress, status.Hue, status.Saturation);
                        PublishState(address, node, lightnessPercent: ToPercent(status.Lightness));
                        return true;
                    }

                    PublishState(address, node, lightnessPercent: brightnessPercent);
                    MarkRuntimeUnknown(address, "HSL.Set 未收到 Status");
                    return true;
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Warning(
                "Mesh/Control",
                $"设置 Mesh 亮度失败：device={address}，{exception.Message}");
            throw;
        }
    }

    /// <summary>主动把 Mesh 设备标记为离线，并通知统一状态观察者。</summary>
    public void RecordDeviceOffline(string address, string? error = null) =>
        MutateRuntime(address, entry =>
        {
            entry.Availability = DeviceRuntimeAvailability.Offline;
            entry.Stage = error is null ? "Mesh 未连接" : "Mesh 连接失败";
            entry.IsBusy = false;
            entry.HasPendingUserOperation = false;
            entry.Error = error;
        });

    /// <summary>
    /// 应用进入后台时释放 Mesh Proxy 会话；前台恢复后下一次查询或控制会自动重连。
    /// </summary>
    public Task SuspendForBackgroundAsync(CancellationToken cancellationToken = default) =>
        DisconnectAllAsync(cancellationToken, "应用进入后台");

    public Task DisconnectForApplicationCloseAsync(CancellationToken cancellationToken = default) =>
        DisconnectAllAsync(cancellationToken, "应用关闭");

    private async Task DisconnectAllAsync(CancellationToken cancellationToken, string reason)
    {
        foreach (var pair in _sessions.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DisconnectSessionAsync(pair.Key, pair.Value, cancellationToken, reason);
        }
    }

    private async Task<T> RunProxySessionAsync<T>(
        string address,
        string operationName,
        int priority,
        Func<MeshClient, MeshNode, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var operationId = BeginRuntimeOperation(
            address,
            operationName,
            userOperation: priority <= UserPriority);
        try
        {
            return await _work.Enqueue(
                address,
                priority,
                token => ExecuteProxyOperationAsync(address, operationId, operation, token),
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            MarkRuntimeCanceled(address, operationId, exception.Message);
            throw;
        }
        catch (Exception exception)
        {
            MarkRuntimeFailed(address, operationId, exception.Message);
            throw;
        }
    }

    private async Task<T> ExecuteProxyOperationAsync<T>(
        string address,
        Guid operationId,
        Func<MeshClient, MeshNode, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var network = _networkManager.FindNetworkForNode(address)
            ?? throw new InvalidOperationException("设备不在任何 Mesh 网络中。");
        var node = network.FindNode(address)
            ?? throw new InvalidOperationException("设备不在任何 Mesh 网络中。");
        var session = _sessions.GetOrAdd(address, _ => new MeshProxySession());

        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            MutateRuntime(address, entry =>
            {
                entry.Stage = "连接 Mesh Proxy";
                entry.IsBusy = true;
            });

            using var timeout = new CancellationTokenSource(SessionTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            var client = await EnsureProxySessionAsync(address, network, session, linked.Token);
            MutateRuntime(address, entry =>
            {
                entry.Availability = DeviceRuntimeAvailability.Ready;
                entry.Stage = "Mesh Proxy 已就绪";
                entry.IsBusy = true;
                entry.Error = null;
            });

            var result = await operation(client, node, linked.Token);
            CompleteRuntimeOperation(address, operationId);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!_connectionService.IsConnected(address))
                await InvalidateSessionAsync(address, session, exception.Message);
            throw;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<MeshClient> EnsureProxySessionAsync(
        string address,
        MeshNetwork network,
        MeshProxySession session,
        CancellationToken cancellationToken)
    {
        session.Transport ??= new MeshGattTransport(
            _connectionService,
            _diagnostics,
            address,
            provisioningMode: false);
        session.Client ??= new MeshClient(session.Transport)
        {
            DiagnosticLog = message => _diagnostics.Debug("Mesh/RX", message),
            SequenceNumberAllocated = sequenceNumber =>
                _networkManager.SyncSequenceFrom(network, sequenceNumber)
        };

        var generation = _connectionService.GetConnectionGeneration(address);
        if (!session.Transport.IsConnected || session.Generation != generation)
        {
            await session.Transport.StartReceivingAsync(cancellationToken);
            session.Generation = session.Transport.ConnectionGeneration;
            session.Client.Restore(_networkManager.RestoreConfigFor(address));
            await session.Client.ConfigureProxyFilterAsync(cancellationToken);
            // Mesh 会话由本服务持有，不能被普通短租约提前释放。
            _connectionService.PromoteConnectionToLongLived(address);
            _diagnostics.Information(
                "Mesh/GATT",
                $"Mesh Proxy 会话已建立并复用：device={address}，generation={session.Generation}。");
        }

        return session.Client;
    }

    private async Task DisconnectSessionAsync(
        string address,
        MeshProxySession session,
        CancellationToken cancellationToken,
        string reason)
    {
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            session.Transport?.Dispose();
            session.Transport = null;
            session.Client = null;
            session.Generation = -1;
            if (_connectionService.IsConnected(address))
                await _connectionService.DisconnectAsync(address, cancellationToken);
            RecordDeviceOffline(address, reason);
        }
        catch (Exception exception)
        {
            _diagnostics.Warning(
                "Mesh/Lifecycle",
                $"释放 Mesh 会话失败：device={address}，{exception.Message}");
            RecordDeviceOffline(address, exception.Message);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task InvalidateSessionAsync(
        string address,
        MeshProxySession session,
        string error)
    {
        try
        {
            session.Transport?.Dispose();
            session.Transport = null;
            session.Client = null;
            session.Generation = -1;
            if (_connectionService.IsConnected(address))
                await _connectionService.DisconnectAsync(address, CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            _diagnostics.Warning(
                "Mesh/Lifecycle",
                $"Mesh 异常会话清理失败：device={address}，{cleanupException.Message}");
        }

        RecordDeviceOffline(address, error);
    }

    /// <summary>向目标节点发送访问层消息并等待同名 Status；超时返回 null。</summary>
    private async Task<MeshAccessMessage?> SendAndWaitAsync(
        MeshClient client,
        MeshNode node,
        ushort statusOpcode,
        byte[] accessPdu,
        string label,
        CancellationToken cancellationToken)
    {
        var statusTask = client.WaitForAccessMessageFromAsync(
            node.UnicastAddress,
            MeshOpcode.Encode(statusOpcode),
            StatusTimeout,
            cancellationToken);
        await client.SendAccessMessageAsync(
            node.UnicastAddress,
            accessPdu,
            cancellationToken);
        try
        {
            _diagnostics.Debug("Mesh/Control", $"已发送 {label}：node=0x{node.UnicastAddress:X4}。");
            return await statusTask;
        }
        catch (TimeoutException)
        {
            _diagnostics.Warning(
                "Mesh/Control",
                $"等待 {label} 的 Status 超时：node=0x{node.UnicastAddress:X4}。");
            return null;
        }
    }

    private async Task<(ushort Hue, ushort Saturation)> ResolveHslAsync(
        MeshClient client,
        MeshNode node,
        CancellationToken cancellationToken)
    {
        if (_hslMemory.TryGetValue(node.UnicastAddress, out var known))
            return known;

        var response = await SendAndWaitAsync(
            client,
            node,
            MeshOpcode.LightHslStatus,
            LightHslModel.Get(),
            "HSL.Get(前置)",
            cancellationToken);
        if (response is not null &&
            LightHslModel.TryParseStatus(response.AccessPdu, out var status))
        {
            RememberHsl(node.UnicastAddress, status.Hue, status.Saturation);
            return (status.Hue, status.Saturation);
        }

        return (0, 0);
    }

    private void RememberHsl(ushort unicastAddress, ushort hue, ushort saturation) =>
        _hslMemory[unicastAddress] = (hue, saturation);

    private byte NextTid(string address)
    {
        var next = _transactionIds.AddOrUpdate(
            address,
            _ => (byte)0,
            (_, previous) => unchecked((byte)(previous + 1)));
        return next;
    }

    private void PublishState(
        string address,
        MeshNode node,
        bool? isOn = null,
        byte? lightnessPercent = null)
    {
        if (isOn is null && lightnessPercent is null)
            return;

        var state = _states.AddOrUpdate(
            address,
            _ => new MeshDeviceState(address, node.UnicastAddress, isOn, lightnessPercent),
            (_, previous) => previous with
            {
                IsOn = isOn ?? previous.IsOn,
                LightnessPercent = lightnessPercent ?? previous.LightnessPercent
            });
        MutateRuntime(address, entry =>
        {
            entry.Availability = DeviceRuntimeAvailability.Ready;
            entry.LastSeen = DateTimeOffset.UtcNow;
            entry.Error = null;
        });
        StateChanged?.Invoke(this, state);
    }

    private Guid BeginRuntimeOperation(string address, string name, bool userOperation)
    {
        var operation = new DeviceRuntimeOperation(
            Guid.NewGuid(),
            name,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(30),
            DeviceRuntimeOperationStage.Queued);
        MutateRuntime(address, entry =>
        {
            entry.IsBusy = true;
            entry.HasPendingUserOperation = userOperation;
            entry.Stage = "排队";
            entry.Error = null;
            entry.Operation = operation;
        });
        return operation.Id;
    }

    private void CompleteRuntimeOperation(string address, Guid operationId)
    {
        MutateRuntime(address, entry =>
        {
            if (entry.Operation?.Id != operationId)
                return;
            if (entry.Operation.Stage == DeviceRuntimeOperationStage.Unknown)
            {
                entry.IsBusy = false;
                entry.HasPendingUserOperation = false;
                return;
            }
            entry.Operation = entry.Operation with { Stage = DeviceRuntimeOperationStage.Confirmed };
            entry.IsBusy = false;
            entry.HasPendingUserOperation = false;
            entry.Stage = "Mesh 操作已确认";
            entry.Error = null;
        });
    }

    private void MarkRuntimeUnknown(string address, string error) =>
        MutateRuntime(address, entry =>
        {
            if (entry.Operation is { } operation)
                entry.Operation = operation with { Stage = DeviceRuntimeOperationStage.Unknown, Error = error };
            entry.IsBusy = false;
            entry.HasPendingUserOperation = false;
            entry.Stage = "结果未知";
            entry.Error = error;
        });

    private void MarkRuntimeCanceled(string address, Guid operationId, string? error) =>
        MutateRuntime(address, entry =>
        {
            if (entry.Operation?.Id != operationId)
                return;
            entry.Operation = entry.Operation with { Stage = DeviceRuntimeOperationStage.Canceled, Error = error };
            entry.IsBusy = false;
            entry.HasPendingUserOperation = false;
            entry.Stage = "已取消";
            entry.Error = error;
        });

    private void MarkRuntimeFailed(string address, Guid operationId, string error) =>
        MutateRuntime(address, entry =>
        {
            if (entry.Operation?.Id != operationId)
                return;
            entry.Operation = entry.Operation with { Stage = DeviceRuntimeOperationStage.Failed, Error = error };
            entry.IsBusy = false;
            entry.HasPendingUserOperation = false;
            entry.Stage = "Mesh 操作失败";
            entry.Error = error;
            if (!_connectionService.IsConnected(address))
                entry.Availability = DeviceRuntimeAvailability.Offline;
        });

    private void OnConnectionStateChanged(object? sender, BluetoothSessionSnapshot snapshot)
    {
        if (!_sessions.ContainsKey(snapshot.Address) && !_runtimeEntries.ContainsKey(snapshot.Address))
            return;
        if (snapshot.IsConnected)
            return;

        if (snapshot.State is BluetoothSessionState.Scanning or BluetoothSessionState.Connecting)
        {
            MutateRuntime(snapshot.Address, entry =>
            {
                entry.IsBusy = true;
                entry.Stage = snapshot.Message;
                entry.Error = null;
            });
            return;
        }

        MutateRuntime(snapshot.Address, entry =>
        {
            entry.Availability = DeviceRuntimeAvailability.Offline;
            entry.IsBusy = false;
            entry.HasPendingUserOperation = false;
            entry.Stage = snapshot.Message;
            entry.Error = snapshot.Message;
        });
    }

    private DeviceRuntimeSnapshot MutateRuntime(
        string address,
        Action<MeshRuntimeEntry> mutate)
    {
        var entry = _runtimeEntries.GetOrAdd(address, _ => new MeshRuntimeEntry());
        DeviceRuntimeSnapshot snapshot;
        lock (entry.Sync)
        {
            mutate(entry);
            entry.Version++;
            snapshot = entry.GetSnapshot(address);
        }
        RuntimeStateChanged?.Invoke(this, new(address, snapshot));
        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_connectionService is not null)
            _connectionService.StateChanged -= OnConnectionStateChanged;
        try
        {
            await DisconnectAllAsync(CancellationToken.None, "服务释放");
        }
        catch (Exception exception)
        {
            _diagnostics.Warning("Mesh/Lifecycle", $"释放 Mesh 控制服务失败：{exception.Message}");
        }
    }

    private sealed class MeshProxySession
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public MeshGattTransport? Transport { get; set; }
        public MeshClient? Client { get; set; }
        public long Generation { get; set; } = -1;
    }

    private sealed class MeshRuntimeEntry
    {
        public readonly object Sync = new();
        public DeviceRuntimeAvailability Availability { get; set; } = DeviceRuntimeAvailability.Offline;
        public string? Stage { get; set; }
        public bool IsBusy { get; set; }
        public bool HasPendingUserOperation { get; set; }
        public string? Error { get; set; }
        public DeviceRuntimeOperation? Operation { get; set; }
        public DateTimeOffset? LastSeen { get; set; }
        public long Version { get; set; }

        public DeviceRuntimeSnapshot GetSnapshot(string address) => new(
            address,
            Availability,
            Stage,
            Version,
            MeshSteps,
            IsBusy,
            HasPendingUserOperation,
            Error,
            Operation,
            Protocol: BluetoothDeviceProtocol.Mesh,
            LastSeen: LastSeen);
    }

    private static ushort ToLightness(byte percent) =>
        MeshLightness.ToLightness(percent);

    private static byte ToPercent(ushort lightness) =>
        MeshLightness.ToPercent(lightness);
}

/// <summary>
/// Mesh Light HSL 的 Lightness 分量（0-65535）与 UI 百分比（0-100）之间的换算。
/// </summary>
public static class MeshLightness
{
    public static ushort ToLightness(byte percent) =>
        (ushort)Math.Clamp(Math.Round(percent / 100d * ushort.MaxValue), 0, ushort.MaxValue);

    public static byte ToPercent(ushort lightness) =>
        (byte)Math.Clamp(Math.Round(lightness / (double)ushort.MaxValue * 100), 0, 100);
}

/// <summary>一次 Mesh 命令/查询后观察到的设备状态。</summary>
public sealed record MeshDeviceState(
    string Address,
    ushort UnicastAddress,
    bool? IsOn = null,
    byte? LightnessPercent = null);
