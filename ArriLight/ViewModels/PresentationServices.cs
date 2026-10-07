
namespace ArriLight.ViewModels;

// These contracts contain no MAUI types. Platform hosts provide navigation,
// native dialogs and UI dispatch; view models retain the decisions and state.
public interface IUiDispatcher
{
    void Post(Action action);
    Task InvokeAsync(Action action);
    Task InvokeAsync(Func<Task> action);
    Task<T> InvokeAsync<T>(Func<T> action);
}

public interface IAppNavigator
{
    Task GoToAsync(string route, bool animate = true);
}

public interface IUserInteraction
{
    void ShowToast(string message);
    void ShowToastForDuration(string message, TimeSpan duration);
    Task AlertAsync(string title, string message, string cancel);
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
    Task<string?> PromptAsync(string title, string message, string accept, string cancel,
        string placeholder = "", string initialValue = "", int maxLength = -1, bool numeric = false);
    Task<string?> ActionSheetAsync(string title, string cancel, params string[] buttons);
    Task<bool> OpenUriAsync(string uri);
}

public enum ApplicationTheme { System, Light, Dark }

public interface IApplicationPresentation
{
    string Version { get; }
    string Build { get; }
    ApplicationTheme Theme { get; set; }
}
