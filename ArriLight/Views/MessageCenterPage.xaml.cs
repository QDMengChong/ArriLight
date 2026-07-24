using System.Collections.ObjectModel;

namespace ArriLight;

public partial class MessageCenterPage : ContentPage
{
    public ObservableCollection<AppMessage> Messages { get; } =
    [
        new("欢迎使用 ArriLight", "感谢你的加入！现在可以前往设备页添加并连接第一盏灯。", "今天 09:30"),
        new("设备状态提醒", "客厅主灯已恢复在线，可以继续进行亮度与色彩控制。", "昨天 18:42")
    ];

    public MessageCenterPage()
    {
        InitializeComponent();
        BindingContext = this;
    }
}

public sealed record AppMessage(string Title, string Content, string Time);
