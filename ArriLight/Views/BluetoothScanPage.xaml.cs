namespace ArriLight;

public partial class BluetoothScanPage : ContentPage
{
    public BluetoothScanPageViewModel ViewModel { get; }
    public BluetoothScanPage() : this(ServiceHelper.GetRequiredService<BluetoothScanPageViewModel>()) { }
    public BluetoothScanPage(BluetoothScanPageViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        BindingContext = viewModel;
        viewModel.ScrollToTopRequested += ScrollToTopAsync;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(viewModel.IsBindingPromptVisible) or nameof(viewModel.BindingStatus) or nameof(viewModel.BindingCountdown))
                RenderBindingPrompt();
        };
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
#if ANDROID
    // 原生 Dialog 的 DimBehind 会覆盖 Shell 顶栏、底部标签栏和页面内容；
    // XAML 页面内的遮罩只能覆盖当前 ContentPage。
    private Android.App.Dialog? _bindingDialog;
    private Android.Widget.TextView? _bindingDialogStatus;
    private Android.Widget.TextView? _bindingDialogCountdown;
#endif
    private async Task ScrollToTopAsync()
    {
        if (ViewModel.Devices.Count == 0)
            return;

        // 等待列表布局稳定，避免在布局未完成时滚动导致错位。
        await Task.Delay(150);

#if ANDROID
        // MAUI 的 CollectionView.ScrollTo(0, Start) 在 Android 上存在已知 bug
        // （dotnet/maui#27117），会滚动到第二个元素而不是第一个。
        // 这里直接操作平台 RecyclerView，强制滚动到 position 0。
        if (DeviceCollection.Handler?.PlatformView
                is AndroidX.RecyclerView.Widget.RecyclerView recyclerView &&
            recyclerView.GetLayoutManager()
                is AndroidX.RecyclerView.Widget.LinearLayoutManager layoutManager)
        {
            layoutManager.ScrollToPositionWithOffset(0, 0);
            return;
        }
#endif

        DeviceCollection.ScrollTo(
            0, position: ScrollToPosition.Start, animate: false);
    }
    private async void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        DeviceCollection.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is DiscoveredBluetoothDevice device)
            await ViewModel.AddDeviceSelectionCommand.ExecuteAsync(device);
    }
    private void RenderBindingPrompt()
    {
        BindingPromptOverlay.IsVisible = ViewModel.IsBindingPromptVisible;
        BindingPromptStatus.Text = ViewModel.BindingStatus;
        BindingPromptCountdown.Text = ViewModel.BindingCountdown;
#if ANDROID
        if (ViewModel.IsBindingPromptVisible)
            ShowBindingDialog();
        else
        {
            _bindingDialog?.Dismiss();
            _bindingDialog?.Dispose();
            _bindingDialog = null;
            _bindingDialogStatus = null;
            _bindingDialogCountdown = null;
        }
        if (_bindingDialogStatus is not null)
            _bindingDialogStatus.Text = ViewModel.BindingStatus;
        if (_bindingDialogCountdown is not null)
            _bindingDialogCountdown.Text = ViewModel.BindingCountdown;
#endif
    }
#if ANDROID
    private void ShowBindingDialog()
    {
        if (_bindingDialog?.IsShowing == true)
            return;

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null)
        {
            // 仅在极早期生命周期无法取得 Activity 时回退到页内遮罩。
            BindingPromptOverlay.IsVisible = true;
            return;
        }

        var context = activity;
        var horizontalPadding = ToPixels(context, 28);
        var verticalPadding = ToPixels(context, 22);
        var card = new Android.Widget.LinearLayout(context)
        {
            Orientation = Android.Widget.Orientation.Vertical
        };
        card.SetPadding(horizontalPadding, verticalPadding, horizontalPadding, verticalPadding);
        card.Background = CreateRoundedBackground(
            Android.Graphics.Color.White,
            ToPixels(context, 20));

        var title = CreateDialogText(
            context,
            AppResources.Pairing_InProgress,
            20,
            Android.Graphics.Color.ParseColor("#15213B"));
        title.Typeface = Android.Graphics.Typeface.DefaultBold;
        card.AddView(title, CreateDialogLayoutParams(context, bottomMarginDp: 14));

        _bindingDialogStatus = CreateDialogText(
            context,
            string.Empty,
            14,
            Android.Graphics.Color.ParseColor("#667085"));
        card.AddView(_bindingDialogStatus, CreateDialogLayoutParams(context, bottomMarginDp: 14));

        var message = CreateDialogText(
            context,
            AppResources.Pairing_Instructions,
            14,
            Android.Graphics.Color.ParseColor("#667085"));
        card.AddView(message, CreateDialogLayoutParams(context, bottomMarginDp: 18));

        _bindingDialogCountdown = CreateDialogText(
            context,
            string.Empty,
            16,
            Android.Graphics.Color.ParseColor("#2E5CF2"));
        _bindingDialogCountdown.Typeface = Android.Graphics.Typeface.DefaultBold;
        card.AddView(_bindingDialogCountdown, CreateDialogLayoutParams(context, bottomMarginDp: 20));

        var cancel = new Android.Widget.Button(context)
        {
            Text = AppResources.Pairing_CancelAdding
        };
        cancel.SetTextColor(Android.Graphics.Color.White);
        cancel.TextSize = 16;
        cancel.Background = CreateRoundedBackground(
            Android.Graphics.Color.ParseColor("#2E5CF2"),
            ToPixels(context, 12));
        cancel.Click += (_, _) => ViewModel.CancelBindingCommand.Execute(null);
        card.AddView(cancel, new Android.Widget.LinearLayout.LayoutParams(
            Android.Views.ViewGroup.LayoutParams.MatchParent,
            ToPixels(context, 52)));

        var container = new Android.Widget.FrameLayout(context);
        container.AddView(
            card,
            new Android.Widget.FrameLayout.LayoutParams(
                Android.Views.ViewGroup.LayoutParams.MatchParent,
                Android.Views.ViewGroup.LayoutParams.WrapContent)
            {
                LeftMargin = ToPixels(context, 28),
                RightMargin = ToPixels(context, 28),
                Gravity = Android.Views.GravityFlags.Center
            });

        var dialog = new Android.App.Dialog(context);
        dialog.RequestWindowFeature((int)Android.Views.WindowFeatures.NoTitle);
        dialog.SetContentView(container);
        dialog.SetCancelable(false);
        dialog.SetCanceledOnTouchOutside(false);
        dialog.Show();

        if (dialog.Window is { } window)
        {
            window.SetBackgroundDrawable(
                new Android.Graphics.Drawables.ColorDrawable(Android.Graphics.Color.Transparent));
            window.AddFlags(Android.Views.WindowManagerFlags.DimBehind);
            if (window.Attributes is { } attributes)
            {
                attributes.DimAmount = 0.55f;
                window.Attributes = attributes;
            }
            window.SetLayout(
                Android.Views.ViewGroup.LayoutParams.MatchParent,
                Android.Views.ViewGroup.LayoutParams.MatchParent);
        }

        _bindingDialog = dialog;
    }

    private static Android.Widget.LinearLayout.LayoutParams CreateDialogLayoutParams(
        Android.Content.Context context,
        int bottomMarginDp) =>
        new(
            Android.Views.ViewGroup.LayoutParams.MatchParent,
            Android.Views.ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = ToPixels(context, bottomMarginDp)
        };

    private static Android.Widget.TextView CreateDialogText(
        Android.Content.Context context,
        string text,
        float sizeSp,
        Android.Graphics.Color color)
    {
        var label = new Android.Widget.TextView(context)
        {
            Text = text,
            Gravity = Android.Views.GravityFlags.Center,
            TextSize = sizeSp
        };
        label.SetTextColor(color);
        return label;
    }

    private static Android.Graphics.Drawables.GradientDrawable CreateRoundedBackground(
        Android.Graphics.Color color,
        int cornerRadiusPixels)
    {
        var drawable = new Android.Graphics.Drawables.GradientDrawable();
        drawable.SetColor(color);
        drawable.SetCornerRadius(cornerRadiusPixels);
        return drawable;
    }

    private static int ToPixels(Android.Content.Context context, int dp) =>
        (int)Math.Round(
            dp * (context.Resources?.DisplayMetrics?.Density ?? 1f));
#endif

}
