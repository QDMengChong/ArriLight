using Android.App;
using Android.Content.PM;
using Android.OS;

namespace ArriLight
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        private readonly BackExitGate _backExitGate = new();
        private RootBackCallback? _backCallback;
        private Shell? _shell;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // MAUI disables its predictive-back callback at the root. Register
            // our own AndroidX callback so root back presses still reach us.
            _backCallback = new RootBackCallback(this);
            OnBackPressedDispatcher.AddCallback(this, _backCallback);
            _shell = Shell.Current;
            if (_shell is not null)
                _shell.Navigated += OnShellNavigated;
        }

        protected override void OnPause()
        {
            _backExitGate.Reset();
            base.OnPause();
        }

        protected override void OnDestroy()
        {
            if (_shell is not null)
                _shell.Navigated -= OnShellNavigated;
            _shell = null;
            _backCallback?.Remove();
            _backCallback?.Dispose();
            _backCallback = null;
            base.OnDestroy();
        }

        private void OnShellNavigated(object? sender, ShellNavigatedEventArgs e) =>
            _backExitGate.Reset();

        private void HandleBackPressed()
        {
            var shell = Shell.Current;
            if (shell is null || shell.Navigation.ModalStack.Count > 0 ||
                shell.Navigation.NavigationStack.Count > 1)
            {
                _backExitGate.Reset();
                // Let MAUI handle page navigation, modal pages and page-specific
                // back handlers, then restore our root callback.
                var callback = _backCallback!;
                callback.Enabled = false;
                try
                {
                    OnBackPressedDispatcher.OnBackPressed();
                }
                finally
                {
                    callback.Enabled = true;
                }
                return;
            }

            if (shell.SendBackButtonPressed())
            {
                _backExitGate.Reset();
                return;
            }

            if (_backExitGate.ShouldExit())
                Finish();
            else
                ToastService.ShowForDuration(
                    AppResources.Navigation_BackAgainToExit,
                    BackExitGate.ConfirmationWindow);
        }

        private sealed class RootBackCallback(MainActivity activity)
            : AndroidX.Activity.OnBackPressedCallback(true)
        {
            public override void HandleOnBackPressed() => activity.HandleBackPressed();
        }
    }
}
