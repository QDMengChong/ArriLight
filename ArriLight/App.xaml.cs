using Microsoft.Extensions.DependencyInjection;

namespace ArriLight
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            UserAppTheme = ThemePreference.Load();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}
