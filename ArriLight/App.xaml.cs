namespace ArriLight
{
    public partial class App : Application
    {
        private readonly AgpLightControlService _agpLight;
        private Window? _currentWindow;

        public App(AgpLightControlService agpLight)
        {
            _agpLight = agpLight;
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
            try
            {
                // Finish() destroys the Android Activity, not the DI singletons.
                // Created also covers a reused MAUI Window with a new native window.
                await _agpLight.StartForNewWindowAsync();
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
            try
            {
                await _agpLight.SuspendForBackgroundAsync();
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
            try
            {
                await _agpLight.DisconnectForApplicationCloseAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BLE/Lifecycle] 关闭时断开失败：{exception.Message}");
            }
        }
    }
}
