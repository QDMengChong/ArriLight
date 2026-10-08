namespace ArriLight
{
    public sealed class TestUiDispatcher : IUiDispatcher
    {
        public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);
        public Task InvokeAsync(Action action) => MainThread.InvokeOnMainThreadAsync(action);
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }
    public sealed class TestAppNavigator : IAppNavigator
    {
        public Task GoToAsync(string route, bool animate = true) => Shell.Current.GoToAsync(route, animate);
    }
    public sealed class TestUserInteraction : IUserInteraction
    {
        public static ContentPage? CurrentPage { get; set; }
        public void ShowToast(string message) => ToastService.Show(message);
        public void ShowToastForDuration(string message, TimeSpan duration) => ToastService.ShowForDuration(message, duration);
        public Task AlertAsync(string title, string message, string cancel) =>
            CurrentPage?.DisplayAlertAsync(title, message, cancel) ?? Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
            CurrentPage!.DisplayAlertAsync(title, message, accept, cancel);
        public Task<string?> PromptAsync(string title, string message, string accept, string cancel,
            string placeholder = "", string initialValue = "", int maxLength = -1, bool numeric = false) => Task.FromResult<string?>(initialValue);
        public Task<string?> ActionSheetAsync(string title, string cancel, params string[] buttons) => Task.FromResult<string?>(cancel);
        public Task<bool> OpenUriAsync(string uri) => Task.FromResult(true);
    }
    public sealed class TestApplicationPresentation : IApplicationPresentation
    {
        public string Version => "1.0";
        public string Build => "2";
        public ApplicationTheme Theme { get; set; }
    }
    public sealed class TestFirmwareFilePicker : IFirmwareFilePicker
    {
        public Task<IFirmwareFile?> PickAsync(string title) => Task.FromResult<IFirmwareFile?>(null);
    }
    public partial class FirmwareUpdatePage
    {
        public string ConnectionText { get => ViewModel.ConnectionText; }
        public string CurrentVersion { get => ViewModel.CurrentVersion; }
    }
    public partial class AppVersionPage
    {
        public bool IsDebugModeEnabled { get => ViewModel.IsDebugModeEnabled; }
        public string VersionText { get => ViewModel.VersionText; }
    }
    public partial class DebugConfigurationPage
    {
        public string ConfigurationText { get => ViewModel.ConfigurationText; set => ViewModel.ConfigurationText = value; }
        public string StatusText { get => ViewModel.StatusText; }
        public bool CanSave { get => ViewModel.CanSave; }
        public bool IsConfigurationBusy { get => ViewModel.IsConfigurationBusy; }
    }
}
namespace ArriLight.ViewModels
{
    public partial class MainPageViewModel
    {
        internal Task QueryForTestAsync(LightDeviceItem device, TimeSpan timeout) =>
            QueryDeviceStatusAsync(device, timeout, CancellationToken.None);
        internal void MarkOfflineForTest(LightDeviceItem device) => MarkDeviceOffline(device);
        internal void SetHomeRefreshForTest(Task task, CancellationTokenSource source)
        { _statusRefreshTask = task; _statusRefreshCancellation = source; }
    }
    public partial class AppVersionPageViewModel
    { internal Task TapForTestAsync(DateTimeOffset now) => HandleVersionTapAsync(now); }
    public partial class BluetoothScanPageViewModel
    {
        internal Task ScanForTest => _scanTask ?? Task.CompletedTask;
        internal Task? CountdownForTest => _bindingCountdownTask;
        internal Task AddForTestAsync(string name, string address) => AddDeviceAsync(name, address);
        internal Task ShowProgressForTestAsync(CancellationToken token) =>
            UpdateBindingProgressAsync(AgpBindingStage.Connecting, token, _bindingCancellation!.Token);
        internal void SetPageCancellationForTest(CancellationTokenSource source)
        { _bindingCancellation = source; _bindingDeadline = DateTimeOffset.UtcNow + AgpLightControlService.BindingTimeout; }
        internal void ExpireCountdownForTest() => _bindingDeadline = DateTimeOffset.UtcNow;
    }
    public partial class DeviceControlPageViewModel
    {
        internal void SetEffectForTest(ArriOpto.BLE.Agp.AgpLightMode mode) => EffectMode = mode;
        internal Task LocalModeLoadForTest => _localModeLoadTask;
        internal async Task LoadModeForTestAsync()
        {
            await EnsureConnectedAsync(showError: false);
            await _localModeLoadTask;
        }
    }
}
