using ArriOpto.BLE.Ota.Protocol;
using Plugin.BLE.Abstractions.Contracts;

namespace ArriOpto.BLE.Ota;

/// <summary>
/// Plugin.BLE 传输层和 APP→OTA 重新扫描的配置。
/// </summary>
public sealed record PluginBleOtaOptions
{
    /// <summary>与 BLE 库无关的协议参数。</summary>
    public OtaOptions Protocol { get; init; } = new();

    /// <summary>APP 重启后查找 OTA bootloader 的最长扫描时间。</summary>
    public TimeSpan BootloaderScanTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 未提供 <see cref="BootloaderMatcher"/> 时使用的默认设备名集合。
    /// </summary>
    /// <remarks>附近存在多个同名设备时不安全，量产环境应使用自定义匹配器。</remarks>
    public IReadOnlySet<string> BootloaderNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PPlusOTA"
        };

    /// <summary>
    /// APP→OTA 重启后用于识别同一物理设备的跨平台匹配器。
    /// </summary>
    /// <remarks>
    /// 推荐校验广播制造商数据或 service data。不要把 Android MAC 作为唯一依据，
    /// 因为 iOS 不公开真实外设 MAC。
    /// </remarks>
    public Func<IDevice, SingleBankAppTransition, bool>? BootloaderMatcher { get; init; }

    /// <summary>
    /// 可选的 MAUI 主线程调度器。
    /// </summary>
    /// <example>
    /// <c>operation =&gt; MainThread.InvokeOnMainThreadAsync(operation)</c>
    /// </example>
    public Func<Func<Task>, Task>? OperationDispatcher { get; init; }
    public PluginBleOtaTransport? Transport { get; init; }

    /// <summary>恢复升级要求设备已经处于 Single Bank OTA，禁止自动从应用切换。</summary>
    public bool RequireSingleBankOtaMode { get; init; }

    internal void Validate()
    {
        if (BootloaderScanTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BootloaderScanTimeout));
        }

        if (BootloaderNames is null)
        {
            throw new ArgumentNullException(nameof(BootloaderNames));
        }
    }
}
