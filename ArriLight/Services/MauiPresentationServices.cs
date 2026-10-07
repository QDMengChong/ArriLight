using ArriLight.ViewModels;

namespace ArriLight;

public sealed class MauiFirmwareFilePicker : IFirmwareFilePicker
{
    public async Task<IFirmwareFile?> PickAsync(string title)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = title });
        return file is null ? null : new SelectedFirmwareFile(file);
    }
}

public sealed class MauiUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);
    public Task InvokeAsync(Action action) => MainThread.InvokeOnMainThreadAsync(action);
    public Task InvokeAsync(Func<Task> action) => MainThread.InvokeOnMainThreadAsync(action);
    public Task<T> InvokeAsync<T>(Func<T> action) => MainThread.InvokeOnMainThreadAsync(action);
}

public sealed class ShellAppNavigator : IAppNavigator
{
    public Task GoToAsync(string route, bool animate = true) => Shell.Current.GoToAsync(route, animate);
}

public sealed class MauiUserInteraction : IUserInteraction
{
    private static Page CurrentPage => Shell.Current.CurrentPage;
    public void ShowToast(string message) => ToastService.Show(message);
    public void ShowToastForDuration(string message, TimeSpan duration) => ToastService.ShowForDuration(message, duration);
    public Task AlertAsync(string title, string message, string cancel) =>
        CurrentPage.DisplayAlertAsync(title, message, cancel);
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        CurrentPage.DisplayAlertAsync(title, message, accept, cancel);
    public Task<string?> PromptAsync(string title, string message, string accept, string cancel,
        string placeholder = "", string initialValue = "", int maxLength = -1, bool numeric = false) =>
        CurrentPage.DisplayPromptAsync(title, message, accept, cancel, placeholder, maxLength,
            numeric ? Keyboard.Numeric : Keyboard.Default, initialValue)!;
    public Task<string?> ActionSheetAsync(string title, string cancel, params string[] buttons) =>
        CurrentPage.DisplayActionSheetAsync(title, cancel, null, buttons)!;
    public Task<bool> OpenUriAsync(string uri) => Launcher.Default.TryOpenAsync(uri);
}

public sealed class MauiApplicationPresentation : IApplicationPresentation
{
    public string Version => AppInfo.Current.VersionString;
    public string Build => AppInfo.Current.BuildString;
    public ApplicationTheme Theme
    {
        get => ThemePreference.Load() switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark => ApplicationTheme.Dark,
            _ => ApplicationTheme.System
        };
        set
        {
            var theme = value switch
            {
                ApplicationTheme.Light => AppTheme.Light,
                ApplicationTheme.Dark => AppTheme.Dark,
                _ => AppTheme.Unspecified
            };
            if (Application.Current is { } application)
                application.UserAppTheme = theme;
            ThemePreference.Save(theme);
        }
    }
}
