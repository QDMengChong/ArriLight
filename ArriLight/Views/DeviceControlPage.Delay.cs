namespace ArriLight;
public partial class DeviceControlPage
{
    private static readonly bool ShowTimedControls = false;
    private bool HasTurnOnDelayView => TurnOnDelayControlLazyView.HasLazyViewLoaded;
    private Entry TurnOnDelaySecondsEntry => ((LazyTurnOnDelayControlView)TurnOnDelayControlLazyView.Content!).SecondsEntry;
    private void OnTurnOnDelaySecondsFocused(object? sender, FocusEventArgs e) => Dispatcher.Dispatch(() =>
    {
        TurnOnDelaySecondsEntry.CursorPosition = 0;
        TurnOnDelaySecondsEntry.SelectionLength = TurnOnDelaySecondsEntry.Text?.Length ?? 0;
    });
}
