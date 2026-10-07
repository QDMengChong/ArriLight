using CommunityToolkit.Mvvm.ComponentModel;

namespace ArriLight.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public virtual Task OnAppearingAsync() => Task.CompletedTask;
    public virtual void OnDisappearing() { }

    public virtual void ApplyQueryAttributes(IDictionary<string, object> query) { }
}
