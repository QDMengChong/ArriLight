using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArriLight;

public partial class LegalDocumentPage : ContentPage, IQueryAttributable, INotifyPropertyChanged
{
    private string _documentTitle = "用户协议";
    private string _documentContent = AgreementContent;

    public string DocumentTitle
    {
        get => _documentTitle;
        private set => SetField(ref _documentTitle, value);
    }

    public string DocumentContent
    {
        get => _documentContent;
        private set => SetField(ref _documentContent, value);
    }

    public LegalDocumentPage()
    {
        InitializeComponent();
        BindingContext = this;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var kind = query.TryGetValue("kind", out var value) ? value?.ToString() : null;
        var isPrivacy = string.Equals(kind, "privacy", StringComparison.OrdinalIgnoreCase);

        DocumentTitle = isPrivacy ? "隐私政策" : "用户协议";
        DocumentContent = isPrivacy ? PrivacyContent : AgreementContent;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }

    private const string AgreementContent =
        "欢迎使用 ArriLight。使用本应用前，请阅读并理解本协议。\n\n" +
        "1. 本应用用于发现、连接和控制兼容的灯光设备。\n\n" +
        "2. 请遵守适用法律法规，并妥善保管账号及设备访问权限。\n\n" +
        "3. 设备兼容性、服务范围和正式条款将在产品发布前进一步完善。\n\n" +
        "当前内容为界面设计阶段的示例文本，不构成最终法律文件。";

    private const string PrivacyContent =
        "我们重视你的隐私与个人信息安全。\n\n" +
        "1. 蓝牙权限仅用于发现、连接和控制附近的兼容设备。\n\n" +
        "2. 扫码权限仅在你主动使用扫码添加设备时调用。\n\n" +
        "3. 未经你的授权，我们不会将个人信息用于与核心功能无关的用途。\n\n" +
        "当前内容为界面设计阶段的示例文本，正式版本发布前应由专业人员审核。";
}
