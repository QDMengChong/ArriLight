namespace ArriOpto.BLE.Mesh;

/// <summary>
/// Transition Time field (Mesh Model Specification 1.1, Section 3.2.9).
/// </summary>
/// <remarks>
/// Bit 0-5 = Number of Steps, bit 6-7 = Step Resolution
/// (0 = 100 ms, 1 = 1 s, 2 = 10 s, 3 = 10 min). 0x00 means instantaneous,
/// 0x3F means unknown.
/// </remarks>
public readonly record struct TransitionTime(byte Value)
{
    public const byte InstantaneousValue = 0x00;
    public const byte UnknownValue = 0x3F;

    public static TransitionTime Instantaneous => new(InstantaneousValue);

    public static TransitionTime Unknown => new(UnknownValue);

    /// <summary>
    /// Builds the smallest Transition Time encoding that covers the requested
    /// duration, using the highest resolution that fits.
    /// </summary>
    public static TransitionTime FromMilliseconds(double milliseconds)
    {
        if (milliseconds <= 0)
            return Instantaneous;

        var ms = Math.Max(1, milliseconds);
        if (ms <= 100 * 63)
        {
            return new TransitionTime(
                (byte)(Math.Min(63, Math.Max(1, (int)Math.Round(ms / 100.0)))));
        }

        if (ms <= 1000 * 63)
        {
            return new TransitionTime(
                (byte)((1 << 6) | Math.Min(63, Math.Max(1, (int)Math.Round(ms / 1000.0)))));
        }

        if (ms <= 10000 * 63)
        {
            return new TransitionTime(
                (byte)((2 << 6) | Math.Min(63, Math.Max(1, (int)Math.Round(ms / 10000.0)))));
        }

        return new TransitionTime(
            (byte)((3 << 6) | Math.Min(63, Math.Max(1, (int)Math.Round(ms / 600000.0)))));
    }

    public static TransitionTime FromSeconds(double seconds) =>
        FromMilliseconds(seconds * 1000);

    public byte Encode() => Value;

    public static TransitionTime Decode(byte value) => new(value);

    /// <summary>Total duration in milliseconds (0 for instantaneous).</summary>
    public double TotalMilliseconds
    {
        get
        {
            if (Value == InstantaneousValue || Value == UnknownValue)
                return 0;

            var steps = Value & 0x3F;
            var resolutionMs = (Value >> 6) switch
            {
                0 => 100.0,
                1 => 1000.0,
                2 => 10000.0,
                _ => 600000.0
            };
            return steps * resolutionMs;
        }
    }
}
