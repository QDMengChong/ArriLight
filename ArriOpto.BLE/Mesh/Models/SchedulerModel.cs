using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Scheduler model messages (Mesh Model Specification 1.1, Section 5.2.3).
/// </summary>
public static class SchedulerModel
{
    /// <summary>构建 Scheduler Get 消息。</summary>
    public static byte[] Get() =>
        MeshMessageWriter.Create(MeshOpcode.SchedulerGet).ToArray();

    /// <summary>
    /// Parses a Scheduler Status message (16-bit register bitmap).
    /// </summary>
    public static bool TryParseStatus(
        ReadOnlySpan<byte> message,
        out ushort schedules)
    {
        schedules = 0;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.SchedulerStatus) ||
            reader.Remaining != 2)
        {
            return false;
        }

        schedules = reader.ReadU16();
        return true;
    }

    /// <summary>构建 Scheduler Action Get 消息（载荷：Index(1)）。</summary>
    /// <param name="index">Schedule Register 条目索引（0x00-0x0F）。</param>
    public static byte[] ActionGet(byte index) =>
        MeshMessageWriter.Create(MeshOpcode.SchedulerActionGet)
            .AddU8(index)
            .ToArray();

    /// <summary>
    /// Builds a Scheduler Action Set message (80 bits).
    /// </summary>
    /// <remarks>
    /// Octet 0 = Index(4) | Year low nibble(4). The remaining 76 bits are packed
    /// little-endian: Year high(3) + Month(12) + Day(5) + Hour(5) + Minute(6) +
    /// Second(6) + DayOfWeek(7) + Action(4) + Transition Time(8) + Scene Number(16).
    /// </remarks>
    /// <param name="register">要写入的 Schedule Register 条目。</param>
    /// <param name="unacknowledged">true 时使用 Set Unacknowledged opcode。</param>
    public static byte[] ActionSet(
        ScheduleRegister register,
        bool unacknowledged = false)
    {
        ArgumentNullException.ThrowIfNull(register);
        if (register.Index > 0x0F)
            throw new ArgumentOutOfRangeException(nameof(register), "Index must be 0x00-0x0F.");

        // The 76-bit Schedule Register tail spans 72 bits (Year high 3 + Month 12 +
        // Day 5 + Hour 5 + Minute 6 + Second 6 + DayOfWeek 7 + Action 4 +
        // Transition Time 8 + Scene Number 16), stored as 9 little-endian octets.
        ulong low = (ulong)((uint)register.Year >> 4)
            | ((ulong)register.Month << 3)
            | ((ulong)register.Day << 15)
            | ((ulong)register.Hour << 20)
            | ((ulong)register.Minute << 25)
            | ((ulong)register.Second << 31)
            | ((ulong)register.DayOfWeek << 37)
            | ((ulong)register.Action << 44)
            | ((ulong)register.TransitionTime.Encode() << 48)
            | ((ulong)(register.SceneNumber & 0xFF) << 56);
        var high = (byte)(register.SceneNumber >> 8);

        return MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.SchedulerActionSetUnack
                    : MeshOpcode.SchedulerActionSet)
            .AddU8((byte)((register.Index & 0x0F) | ((register.Year & 0x0F) << 4)))
            .AddU64(low)
            .AddU8(high)
            .ToArray();
    }

    /// <summary>构建 Scheduler Action Set Unacknowledged 消息。</summary>
    public static byte[] ActionSetUnacknowledged(ScheduleRegister register) =>
        ActionSet(register, unacknowledged: true);

    /// <summary>
    /// Parses a Scheduler Action Status message (Index(4) + Year low(4) +
    /// 76-bit Schedule Register, or a single Index octet when the entry is not
    /// defined).
    /// </summary>
    public static bool TryParseActionStatus(
        ReadOnlySpan<byte> message,
        out ScheduleRegister? register)
    {
        register = null;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.SchedulerActionStatus) ||
            reader.Remaining != 10)
        {
            return false;
        }

        var first = reader.ReadU8();
        var index = (byte)(first & 0x0F);
        var year = (byte)((first >> 4) & 0x0F);

        var low = reader.ReadU64();
        var high = reader.ReadU8();
        year |= (byte)((low & 0x07) << 4);
        var month = (ushort)((low >> 3) & 0x0FFF);
        var day = (byte)((low >> 15) & 0x1F);
        var hour = (byte)((low >> 20) & 0x1F);
        var minute = (byte)((low >> 25) & 0x3F);
        var second = (byte)((low >> 31) & 0x3F);
        var dayOfWeek = (byte)((low >> 37) & 0x7F);
        var action = (byte)((low >> 44) & 0x0F);
        var transition = (byte)((low >> 48) & 0xFF);
        var sceneNumber = (ushort)((low >> 56) | ((ulong)high << 8));

        register = new ScheduleRegister(
            index,
            year,
            month,
            day,
            hour,
            minute,
            second,
            dayOfWeek,
            action,
            TransitionTime.Decode(transition),
            sceneNumber);
        return true;
    }
}

/// <summary>Schedule Register entry (Section 5.1.4.2, Table 5.5).</summary>
public sealed class ScheduleRegister
{
    public ScheduleRegister(
        byte index,
        byte year,
        ushort month,
        byte day,
        byte hour,
        byte minute,
        byte second,
        byte dayOfWeek,
        byte action,
        TransitionTime transitionTime,
        ushort sceneNumber)
    {
        Index = index;
        Year = year;
        Month = month;
        Day = day;
        Hour = hour;
        Minute = minute;
        Second = second;
        DayOfWeek = dayOfWeek;
        Action = action;
        TransitionTime = transitionTime;
        SceneNumber = sceneNumber;
    }

    /// <summary>Index of the Schedule Register entry (0x00-0x0F).</summary>
    public byte Index { get; set; }

    /// <summary>2 least significant digits of the year, or 0x64 = any year.</summary>
    public byte Year { get; set; }

    /// <summary>12-bit month bitmask (bit 0 = January ... bit 11 = December).</summary>
    public ushort Month { get; set; }

    /// <summary>0x00 = any day, 0x01-0x1F = day of month.</summary>
    public byte Day { get; set; }

    /// <summary>0x00-0x17 = hour, 0x18 = any hour, 0x19 = random hour.</summary>
    public byte Hour { get; set; }

    /// <summary>0x00-0x3B = minute, 0x3C = any, 0x3D/0x3E = every 15/20 min, 0x3F = random.</summary>
    public byte Minute { get; set; }

    /// <summary>0x00-0x3B = second, 0x3C = any, 0x3D/0x3E = every 15/20 s, 0x3F = random.</summary>
    public byte Second { get; set; }

    /// <summary>7-bit day-of-week bitmask (bit 0 = Monday ... bit 6 = Sunday).</summary>
    public byte DayOfWeek { get; set; }

    /// <summary>0x0 = Turn Off, 0x1 = Turn On, 0x2 = Scene Recall, 0xF = Inactive.</summary>
    public byte Action { get; set; }

    public TransitionTime TransitionTime { get; set; }

    /// <summary>0x0000 = no scene.</summary>
    public ushort SceneNumber { get; set; }
}

/// <summary>Scheduler Action values (Table 5.13).</summary>
public enum SchedulerAction : byte
{
    TurnOff = 0x0,
    TurnOn = 0x1,
    SceneRecall = 0x2,
    Inactive = 0xF
}
