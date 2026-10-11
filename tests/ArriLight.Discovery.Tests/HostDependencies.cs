namespace ArriLight;

public static class AppResources
{
    public const string Ble_AddressRequired = "Address required";
    public const string Ble_Disabled = "Bluetooth disabled";
    public const string Device_UnnamedBle = "Unnamed device";
    public const string Mesh_Badge = "Mesh";
    public const string Gatt_Badge = "GATT";
}

public sealed class AppDiagnosticService
{
    public TimingScope BeginBleTiming(string stage, string? address = null, string? detail = null) => new();
    public void Information(string area, string message) { }
    public void Warning(string area, string message) { }
}

public sealed class TimingScope : IDisposable
{
    public void Mark(string message) { }
    public void SetResult(string result) { }
    public void Dispose() { }
}
