using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class LegalDocumentPageViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _documentTitle = AppResources.Legal_AgreementTitle;
    [ObservableProperty]
    private string _documentContent = AgreementContent;

    public LegalDocumentPageViewModel() { }

    public override void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var kind = query.TryGetValue("kind", out var value) ? value?.ToString() : null;
        var isPrivacy = string.Equals(kind, "privacy", StringComparison.OrdinalIgnoreCase);

        DocumentTitle = isPrivacy ? AppResources.Legal_PrivacyTitle : AppResources.Legal_AgreementTitle;
        DocumentContent = isPrivacy ? PrivacyContent : AgreementContent;
    }

    private static string AgreementContent => AppResources.Legal_AgreementContent;
    private static string PrivacyContent => AppResources.Legal_PrivacyContent;
}
