using System.Collections.ObjectModel;
using ArriOpto.BLE.Agp;

namespace ArriLight;

public enum DeviceRuntimeAvailability { Offline, Online, Ready }
public enum DeviceRuntimeStep
{
    Connection, ResponseChannel, Authentication, StateSubscription, LightState,
    Effects, LocalMode, RelativeDelayOn, RelativeDelayOff, ColorSubscription,
    ColorState, CtlRange, Clock, ScheduleList, Schedules, DeviceInformation,
    BatterySubscription, BatteryState
}
public enum DeviceRuntimeStepStatus { Pending, Running, Succeeded, NotApplicable, Failed, Invalidated }
public sealed record DeviceRuntimeStepResult(DeviceRuntimeStepStatus Status, DateTimeOffset UpdatedAt, string? Error = null);
public enum DeviceRuntimeOperationStage { WaitingPrerequisites, Queued, Sent, Accepted, Confirmed, Failed, Unknown, Canceled }
public sealed record DeviceRuntimeOperation(Guid Id, string Name, DateTimeOffset CreatedAt,
    DateTimeOffset Deadline, DeviceRuntimeOperationStage Stage, string? Error = null);
public sealed record DeviceRuntimeSnapshot(
    string Address, DeviceRuntimeAvailability Availability, string? StageText, long Version,
    IReadOnlyDictionary<DeviceRuntimeStep, DeviceRuntimeStepResult> Steps,
    bool IsBusy, bool HasPendingUserOperation, string? Error = null, DeviceRuntimeOperation? LastOperation = null,
    AgpDelayOnResponse? RelativeDelayOn = null, AgpDelayOffResponse? RelativeDelayOff = null, AgpTimeResponse? Clock = null,
    BluetoothDeviceProtocol Protocol = BluetoothDeviceProtocol.Agp,
    DateTimeOffset? LastSeen = null);
public sealed record DeviceRuntimeChange(string Address, DeviceRuntimeSnapshot Snapshot);

internal sealed class DeviceRuntimeEntry(string address)
{
    internal readonly object Sync = new();
    internal string Address { get; } = address;
    internal long Version;
    internal long Epoch;
    internal long Generation;
    internal long WorkGeneration;
    internal bool Online;
    internal bool Busy;
    internal int UserOperations;
    internal string? Stage;
    internal string? Error;
    internal DeviceRuntimeOperation? Operation;
    internal bool Exclusive;
    internal bool RecoveryPending;
    internal bool AuthorizationBlocked;
    internal string? BlockedToken;
    internal int Attention;
    internal AgpDelayOnResponse? RelativeDelayOn;
    internal AgpDelayOffResponse? RelativeDelayOff;
    internal AgpTimeResponse? Clock;
    internal Task? Preparation;
    internal CancellationTokenSource? Lifetime;
    internal readonly Dictionary<DeviceRuntimeStep, DeviceRuntimeStepResult> Steps = [];
    internal DeviceRuntimeSnapshot Snapshot()
    {
        var complete = Steps.Count == Enum.GetValues<DeviceRuntimeStep>().Length &&
            Steps.Values.All(step => step.Status is DeviceRuntimeStepStatus.Succeeded or DeviceRuntimeStepStatus.NotApplicable);
        return new(Address, !Online ? DeviceRuntimeAvailability.Offline : complete
                ? DeviceRuntimeAvailability.Ready : DeviceRuntimeAvailability.Online,
            Stage, Version, new ReadOnlyDictionary<DeviceRuntimeStep, DeviceRuntimeStepResult>(new Dictionary<DeviceRuntimeStep, DeviceRuntimeStepResult>(Steps)),
            Busy, UserOperations > 0, Online && complete ? null : Error, Operation, RelativeDelayOn, RelativeDelayOff, Clock);
    }
}
