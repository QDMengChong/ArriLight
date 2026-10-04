using Microsoft.Extensions.DependencyInjection;

namespace ArriLight
{
    public partial class App : Application
    {
        private readonly AgpLightControlService _agpLight;

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
            window.Stopped += OnWindowStopped;
            window.Resumed += OnWindowResumed;
            window.Destroying += OnWindowDestroying;
            return window;
        }

        private async void OnWindowStopped(object? sender, EventArgs e)
        {
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
