namespace ArriLight;

// Each effect owns its parameter model even when the current fields coincide.
public sealed record BlinkEffectParameters(ushort PeriodMilliseconds = 1_000);

public sealed record BreatheEffectParameters(ushort PeriodMilliseconds = 1_000);
