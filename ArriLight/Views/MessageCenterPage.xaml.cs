using System.Collections.ObjectModel;

namespace ArriLight;

public partial class MessageCenterPage : ContentPage
{
    public ObservableCollection<AppMessage> Messages { get; } =
    [
        new(AppResources.Messages_WelcomeTitle, AppResources.Messages_WelcomeContent, AppResources.Messages_TodayTime),
        new(AppResources.Messages_StatusTitle, AppResources.Messages_StatusContent, AppResources.Messages_YesterdayTime)
    ];

    public MessageCenterPage()
    {
        InitializeComponent();
        BindingContext = this;
    }
}

public sealed record AppMessage(string Title, string Content, string Time);
