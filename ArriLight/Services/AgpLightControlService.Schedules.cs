using System.Collections.Concurrent;
using ArriOpto.BLE.Agp;

namespace ArriLight;

public sealed partial class AgpLightControlService
{
    private readonly ConcurrentDictionary<string, byte> _scheduleSlotCounts =
        new(StringComparer.OrdinalIgnoreCase);

    internal int GetCachedScheduleSlotCount(string address) =>
        _scheduleSlotCounts.TryGetValue(address, out var count)
            ? count : AgpModelCodec.MaximumScheduleSlots;

    public Task<IReadOnlyList<AgpSchedule>> GetSchedulesAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        cancellationToken.ThrowIfCancellationRequested();
        if (_repository.GetCapabilities(address) is { } capabilities &&
            !capabilities.HasFlag(AgpCapabilities.Scheduler))
        {
            return Task.FromResult<IReadOnlyList<AgpSchedule>>([]);
        }

        return WithClientAsync<IReadOnlyList<AgpSchedule>>(
            address,
            async handle =>
            {
                if (!handle.Capabilities.HasFlag(AgpCapabilities.Scheduler))
                    return [];

                var list = await handle.Client.GetScheduleListAsync(cancellationToken);
                EnsureSuccess(list.Result);
                if (list.MaximumSlots is 0 or > AgpModelCodec.MaximumScheduleSlots)
                    throw new InvalidOperationException("设备返回的日程槽数量无效。");

                var schedules = new List<AgpSchedule>();
                // The list mask identifies enabled slots. Read every slot so
                // configured schedules that are disabled remain visible.
                for (byte slot = 0; slot < list.MaximumSlots; slot++)
                {
                    var response = await handle.Client.GetScheduleAsync(slot, cancellationToken);
                    EnsureSuccess(response.Result);
                    var schedule = response.Schedule ??
                        throw new InvalidOperationException("设备未返回日程内容。");
                    if (schedule.Slot != slot)
                        throw new InvalidOperationException("设备返回的日程槽与请求不一致。");

                    if (schedule is
                        {
                            Type: AgpScheduleType.Once,
                            IsEnabled: false,
                            Action: AgpScheduleAction.Off,
                            BrightnessPercent: 0,
                            Selector: 0,
                            TimeValue: 0
                        })
                    {
                        continue;
                    }

                    schedules.Add(schedule);
                }

                cancellationToken.ThrowIfCancellationRequested();
                _scheduleSlotCounts[address] = list.MaximumSlots;
                return schedules;
            },
            cancellationToken);
    }
}
