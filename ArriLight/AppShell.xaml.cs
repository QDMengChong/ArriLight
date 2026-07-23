namespace ArriLight
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute(nameof(MessageCenterPage), typeof(MessageCenterPage));
            Routing.RegisterRoute(nameof(AppVersionPage), typeof(AppVersionPage));
            Routing.RegisterRoute(nameof(AboutUsPage), typeof(AboutUsPage));
            Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
            Routing.RegisterRoute(nameof(LegalDocumentPage), typeof(LegalDocumentPage));
        }
    }
}
