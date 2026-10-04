using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArriLight;

public partial class LegalDocumentPage : ContentPage, IQueryAttributable, INotifyPropertyChanged
{
    private string _documentTitle = AppResources.Legal_AgreementTitle;
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

        DocumentTitle = isPrivacy ? AppResources.Legal_PrivacyTitle : AppResources.Legal_AgreementTitle;
        DocumentContent = isPrivacy ? PrivacyContent : AgreementContent;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }

    private static string AgreementContent => AppResources.Legal_AgreementContent;
    private static string PrivacyContent => AppResources.Legal_PrivacyContent;
}
