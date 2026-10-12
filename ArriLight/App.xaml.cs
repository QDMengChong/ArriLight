namespace ArriLight
{
    public partial class App : Application
    {
        private readonly AgpLightControlService _agpLight;
        private readonly MeshDeviceControlService? _meshControl;
        private readonly BluetoothDiscoveryService? _discoveryService;
        private Window? _currentWindow;
        internal Task CurrentStartupTask { get; private set; } = Task.CompletedTask;
        internal Task CurrentStopTask { get; private set; } = Task.CompletedTask;
        internal Task CurrentResumeTask { get; private set; } = Task.CompletedTask;

        public App(
            AgpLightControlService agpLight,
            BluetoothDiscoveryService? discoveryService = null,
            MeshDeviceControlService? meshControl = null)
        {
            _agpLight = agpLight;
            _discoveryService = discoveryService;
            _meshControl = meshControl;
            InitializeComponent();
            UserAppTheme = ThemePreference.Load();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            Localization.ApplySavedPreferenceForNewWindow();
            var window = new Window(new AppShell());
            _currentWindow = window;
            window.Created += OnWindowCreated;
            window.Stopped += OnWindowStopped;
            window.Resumed += OnWindowResumed;
            window.Destroying += OnWindowDestroying;
            return window;
        }

        private async void OnWindowCreated(object? sender, EventArgs e)
        {
            if (!ReferenceEquals(sender, _currentWindow))
                return;
            CurrentStartupTask = StartWindowAsync();
            await CurrentStartupTask;
        }

        private async Task StartWindowAsync()
        {
            try
            {
                // Finish() destroys the Android Activity, not the DI singletons.
                // Created also covers a reused MAUI Window with a new native window.
                await _agpLight.StartForNewWindowAsync();
                await _agpLight.StartRuntimeAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BLE/Lifecycle] 新窗口恢复失败：{exception.Message}");
            }
        }

        private async void OnWindowStopped(object? sender, EventArgs e)
        {
            if (!ReferenceEquals(sender, _currentWindow))
                return;
            CurrentStopTask = StopWindowAsync();
            await CurrentStopTask;
        }

        private async Task StopWindowAsync()
        {
            try
            {
                // Invalidate every queued/connecting/online runtime immediately,
                // before waiting for native scan cleanup.
                var suspension = _agpLight.SuspendForBackgroundAsync();
                var meshSuspension = _meshControl?.SuspendForBackgroundAsync();
                try
                {
                    if (_discoveryService is not null) await _discoveryService.StopAsync();
                }
                finally
                {
                    await suspension;
                    if (meshSuspension is not null) await meshSuspension;
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BLE/Lifecycle] 后台断开失败：{exception.Message}");
            }
        }

        private async void OnWindowResumed(object? sender, EventArgs e)
        {
            if (!ReferenceEquals(sender, _currentWindow))
                return;
            CurrentResumeTask = ResumeWindowAsync();
            await CurrentResumeTask;
        }

        private async Task ResumeWindowAsync()
        {
            try
            {
                await _agpLight.ResumeAfterBackgroundAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BLE/Lifecycle] 前台恢复失败：{exception.Message}");
            }
        }

        private async void OnWindowDestroying(object? sender, EventArgs e)
        {
            if (!ReferenceEquals(sender, _currentWindow))
                return;
            CurrentStopTask = CloseWindowAsync();
            await CurrentStopTask;
        }

        private async Task CloseWindowAsync()
        {
            try
            {
                var closing = _agpLight.DisconnectForApplicationCloseAsync();
                var meshClosing = _meshControl?.DisconnectForApplicationCloseAsync();
                try
                {
                    if (_discoveryService is not null) await _discoveryService.StopAsync();
                }
                finally
                {
                    await closing;
                    if (meshClosing is not null) await meshClosing;
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BLE/Lifecycle] 关闭时断开失败：{exception.Message}");
            }
        }
    }
}
