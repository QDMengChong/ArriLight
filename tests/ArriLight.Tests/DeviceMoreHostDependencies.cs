namespace ArriLight;

public sealed class DeviceManualPage;
public sealed class DeviceSharePage;
public sealed class ToggledEventArgs : EventArgs
{
    public bool Value { get; init; }
}

public sealed class TestSwitch
{
    public bool IsToggled { get; set; }
}

public partial class DeviceMorePage
{
    private readonly TestSwitch PinnedSwitch = new();
    private void InitializeComponent() { }
    internal async Task LoadModeForTestAsync()
    {
        ReloadDevice();
        await LoadLocalModeAsync();
    }
    internal Task SetModeForTestAsync() => SetLocalModeAsync();
    internal void DisappearForTest() => OnDisappearing();
}
