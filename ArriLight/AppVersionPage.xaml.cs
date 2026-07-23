namespace ArriLight;

public partial class AppVersionPage : ContentPage
{
    public string VersionText => $"版本 {AppInfo.Current.VersionString}（{AppInfo.Current.BuildString}）";

    public AppVersionPage()
    {
        InitializeComponent();
        BindingContext = this;
    }
}
