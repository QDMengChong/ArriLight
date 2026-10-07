using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace ArriLight.ViewModels;

public partial class MessageCenterPageViewModel : ViewModelBase
{
    public ObservableCollection<AppMessage> Messages { get; } =
    [
        new(AppResources.Messages_WelcomeTitle, AppResources.Messages_WelcomeContent, AppResources.Messages_TodayTime),
        new(AppResources.Messages_StatusTitle, AppResources.Messages_StatusContent, AppResources.Messages_YesterdayTime)
    ];

    public MessageCenterPageViewModel() { }
}

public sealed record AppMessage(string Title, string Content, string Time);
