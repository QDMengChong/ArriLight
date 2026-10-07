namespace ArriLight;

public partial class SchedulePage : ContentPage, IQueryAttributable
{
    public SchedulePageViewModel ViewModel { get; }
    public SchedulePage() : this(ServiceHelper.GetRequiredService<SchedulePageViewModel>()) { }
    public SchedulePage(SchedulePageViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        BindingContext = viewModel;
        viewModel.ActionVisualChanged += UpdateActionButtons;
        UpdateActionButtons();
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ViewModel.OnAppearingAsync();
    }
    protected override void OnDisappearing()
    {
        ViewModel.OnDisappearing();
        base.OnDisappearing();
    }
    public void ApplyQueryAttributes(IDictionary<string, object> query) => ViewModel.ApplyQueryAttributes(query);
    private void UpdateActionButtons()
    {
        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var inactiveBackground = isDark
            ? Color.FromArgb("#263247")
            : Colors.Transparent;
        var inactiveText = isDark
            ? Color.FromArgb("#A6B0C2")
            : Color.FromArgb("#6F7B90");

        ActionOnButton.BackgroundColor = ViewModel.IsActionOn
            ? Color.FromArgb("#3157E8")
            : inactiveBackground;
        ActionOnButton.TextColor = ViewModel.IsActionOn ? Colors.White : inactiveText;
        ActionOffButton.BackgroundColor = ViewModel.IsActionOn
            ? inactiveBackground
            : Color.FromArgb("#3157E8");
        ActionOffButton.TextColor = ViewModel.IsActionOn ? inactiveText : Colors.White;
    }
    private async void OnDeleteScheduleClicked(object? sender, EventArgs e)
    {
        if (sender is BindableObject { BindingContext: LightScheduleItem schedule })
            await ViewModel.DeleteScheduleCommand.ExecuteAsync(schedule);
    }
    private async void OnScheduleEnabledToggled(object? sender, ToggledEventArgs e)
    {
        if (sender is Switch { BindingContext: LightScheduleItem schedule } toggle &&
            !await ViewModel.SetScheduleEnabledAsync(schedule, e.Value))
            toggle.IsToggled = schedule.IsEnabled;
    }
}
