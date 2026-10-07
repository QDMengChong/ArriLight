using ArriOpto.BLE.Agp;

namespace ArriLight;

public partial class DeviceControlPage
{
    private const int EffectPeriodStepMilliseconds = 100;


    private bool _isEffectSettingsOpen;
    private bool _effectSettingsCancelled;
    private TaskCompletionSource<ushort?>? _effectSettingsCompletion;
#if ANDROID
    private Android.App.Dialog? _effectSettingsDialog;
#endif

    private void LoadEffectParameters(SavedDevice? device = null) => ViewModel.LoadEffectParameters(device);

    private ushort GetEffectPeriod(AgpLightMode mode) => ViewModel.GetEffectPeriod(mode);

    private void RememberEffectPeriod(AgpLightMode mode, ushort period) => ViewModel.RememberEffectPeriod(mode, period);

    private static bool IsValidEffectPeriod(ushort period) => DeviceControlPageViewModel.IsValidEffectPeriod(period);

    private Task ApplyEffectParametersAsync(AgpLightMode mode, ushort period) => ViewModel.ApplyEffectParametersAsync(mode, period);

    private Task ShowEffectSettingsAsync(AgpLightMode mode) => mode switch
    {
        AgpLightMode.Blink => ShowBlinkSettingsAsync(),
        AgpLightMode.Breathe => ShowBreatheSettingsAsync(),
        _ => Task.CompletedTask
    };

    // Separate entry points allow either effect to gain its own controls later.
    private Task ShowBlinkSettingsAsync() => ShowEffectPeriodSettingsAsync(
        AgpLightMode.Blink, AppResources.Effect_BlinkSettings, GetEffectPeriod(AgpLightMode.Blink));

    private Task ShowBreatheSettingsAsync() => ShowEffectPeriodSettingsAsync(
        AgpLightMode.Breathe, AppResources.Effect_BreatheSettings, GetEffectPeriod(AgpLightMode.Breathe));

    private async Task ShowEffectPeriodSettingsAsync(AgpLightMode mode, string title, ushort initialPeriod)
    {
        if (_isEffectSettingsOpen || !IsDeviceOnline || !_effectsSupported ||
            mode is not (AgpLightMode.Blink or AgpLightMode.Breathe))
            return;

        _isEffectSettingsOpen = true;
        _effectSettingsCancelled = false;
        var completion = new TaskCompletionSource<ushort?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _effectSettingsCompletion = completion;
        try
        {
            var period = SnapEffectPeriod(initialPeriod);
            var nativeShown = false;
#if ANDROID
            nativeShown = await MainThread.InvokeOnMainThreadAsync(
                () => TryShowEffectSettingsDialog(title, period, completion));
#endif
            if (!nativeShown)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    // The page-level overlay is also capability-dependent.
                    // Create it only when a user opens effect settings.
                    await EnsureEffectSettingsOverlayLoadedAsync(title, period);
                    SetEffectSettingsOverlayVisible(true);
                });
            }
            _diagnostics.Information(
                "Light/EffectSettings",
                $"特效参数对话框已打开：device={DeviceAddress}, mode={mode}。");

            var result = await completion.Task;
            await MainThread.InvokeOnMainThreadAsync(HideEffectSettings);
            if (result is { } selectedPeriod && !_effectSettingsCancelled &&
                IsDeviceOnline && _effectsSupported)
            {
                await ApplyEffectParametersAsync(mode, selectedPeriod);
            }
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(HideEffectSettings);
            _effectSettingsCompletion = null;
            _isEffectSettingsOpen = false;
        }
    }

    private void OnEffectSettingsPeriodChanged(object? sender, ValueChangedEventArgs e)
    {
        var period = SnapEffectPeriod(e.NewValue);
        if (sender is Slider slider && Math.Abs(slider.Value - period) > 0.1)
            slider.Value = period;
        if (EffectSettingsPeriodLabel is not null)
            EffectSettingsPeriodLabel.Text = FormatEffectPeriod(period);
    }

    private void OnEffectSettingsCancelClicked(object? sender, EventArgs e) =>
        CloseEffectSettings();

    private void OnEffectSettingsApplyClicked(object? sender, EventArgs e) =>
        CompleteEffectSettings(SnapEffectPeriod(EffectSettingsPeriodSlider.Value));

    private void CompleteEffectSettings(ushort? period)
    {
        _effectSettingsCompletion?.TrySetResult(period);
        HideEffectSettings();
    }

    private void CloseEffectSettings()
    {
        _effectSettingsCancelled = true;
        CompleteEffectSettings(null);
    }

    private void HideEffectSettings()
    {
#if ANDROID
        var dialog = _effectSettingsDialog;
        _effectSettingsDialog = null;
        dialog?.Dismiss();
        dialog?.Dispose();
#endif
        if (HasEffectSettingsOverlay)
            SetEffectSettingsOverlayVisible(false);
    }

    protected override bool OnBackButtonPressed()
    {
        if (_isEffectSettingsOpen)
        {
            CloseEffectSettings();
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private static ushort SnapEffectPeriod(double value) =>
        (ushort)Math.Clamp(
            Math.Round(value / EffectPeriodStepMilliseconds) * EffectPeriodStepMilliseconds,
            AgpModelCodec.MinimumEffectPeriodMilliseconds,
            AgpModelCodec.MaximumEffectPeriodMilliseconds);

    private static string FormatEffectPeriod(ushort period) => Localization.Format(nameof(AppResources.Effect_PeriodDisplay), period);

#if ANDROID
    private bool TryShowEffectSettingsDialog(
        string title,
        ushort period,
        TaskCompletionSource<ushort?> completion)
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null)
            return false;

        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var textColor = Android.Graphics.Color.ParseColor(isDark ? "#F3F6FC" : "#17233D");
        var secondaryColor = Android.Graphics.Color.ParseColor(isDark ? "#A6B0C2" : "#7C879D");
        var primaryColor = Android.Graphics.Color.ParseColor("#3157E8");
        var card = new Android.Widget.LinearLayout(activity)
        {
            Orientation = Android.Widget.Orientation.Vertical
        };
        card.SetPadding(
            EffectDialogPixels(activity, 28), EffectDialogPixels(activity, 22),
            EffectDialogPixels(activity, 28), EffectDialogPixels(activity, 22));
        card.Background = EffectDialogBackground(
            activity, Android.Graphics.Color.ParseColor(isDark ? "#1B2535" : "#FFFFFF"), 20);

        var titleLabel = EffectDialogText(activity, title, 20, textColor);
        titleLabel.Typeface = Android.Graphics.Typeface.DefaultBold;
        card.AddView(titleLabel, EffectDialogLayout(activity, 14));
        card.AddView(
            EffectDialogText(activity, AppResources.Effect_AdjustPeriod, 14, secondaryColor),
            EffectDialogLayout(activity, 14));
        var periodLabel = EffectDialogText(activity, FormatEffectPeriod(period), 16, textColor);
        card.AddView(periodLabel, EffectDialogLayout(activity, 10));

        var slider = new Android.Widget.SeekBar(activity)
        {
            Max = (AgpModelCodec.MaximumEffectPeriodMilliseconds -
                   AgpModelCodec.MinimumEffectPeriodMilliseconds) / EffectPeriodStepMilliseconds,
            Progress = (period - AgpModelCodec.MinimumEffectPeriodMilliseconds) /
                       EffectPeriodStepMilliseconds,
            ContentDescription = AppResources.Effect_Period,
            ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(primaryColor),
            ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(primaryColor)
        };
        ushort SelectedPeriod() => (ushort)(AgpModelCodec.MinimumEffectPeriodMilliseconds +
            slider.Progress * EffectPeriodStepMilliseconds);
        slider.ProgressChanged += (_, _) => periodLabel.Text = FormatEffectPeriod(SelectedPeriod());
        card.AddView(slider, new Android.Widget.LinearLayout.LayoutParams(
            Android.Views.ViewGroup.LayoutParams.MatchParent, EffectDialogPixels(activity, 48))
        {
            BottomMargin = EffectDialogPixels(activity, 16)
        });

        var actions = new Android.Widget.LinearLayout(activity)
        {
            Orientation = Android.Widget.Orientation.Horizontal
        };
        var cancel = new Android.Widget.Button(activity) { Text = AppResources.Common_Cancel, TextSize = 16 };
        cancel.SetTextColor(textColor);
        cancel.Background = EffectDialogBackground(
            activity, Android.Graphics.Color.ParseColor(isDark ? "#263652" : "#EDF3FF"), 12);
        cancel.Click += (_, _) => CloseEffectSettings();
        actions.AddView(cancel, new Android.Widget.LinearLayout.LayoutParams(
            0, EffectDialogPixels(activity, 52), 1f)
        {
            RightMargin = EffectDialogPixels(activity, 6)
        });
        var apply = new Android.Widget.Button(activity) { Text = AppResources.Common_Apply, TextSize = 16 };
        apply.SetTextColor(Android.Graphics.Color.White);
        apply.Background = EffectDialogBackground(activity, primaryColor, 12);
        apply.Click += (_, _) => CompleteEffectSettings(SelectedPeriod());
        actions.AddView(apply, new Android.Widget.LinearLayout.LayoutParams(
            0, EffectDialogPixels(activity, 52), 1f)
        {
            LeftMargin = EffectDialogPixels(activity, 6)
        });
        card.AddView(actions, EffectDialogLayout(activity, 0));

        var container = new Android.Widget.FrameLayout(activity);
        container.AddView(card, new Android.Widget.FrameLayout.LayoutParams(
            Android.Views.ViewGroup.LayoutParams.MatchParent,
            Android.Views.ViewGroup.LayoutParams.WrapContent)
        {
            LeftMargin = EffectDialogPixels(activity, 28),
            RightMargin = EffectDialogPixels(activity, 28),
            Gravity = Android.Views.GravityFlags.Center
        });
        var dialog = new Android.App.Dialog(activity);
        _effectSettingsDialog = dialog;
        dialog.RequestWindowFeature((int)Android.Views.WindowFeatures.NoTitle);
        dialog.SetContentView(container);
        dialog.SetCancelable(true);
        dialog.SetCanceledOnTouchOutside(false);
        dialog.DismissEvent += (_, _) => completion.TrySetResult(null);
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
        return true;
    }

    private static Android.Widget.TextView EffectDialogText(
        Android.Content.Context context, string text, float size, Android.Graphics.Color color)
    {
        var label = new Android.Widget.TextView(context)
        {
            Text = text,
            TextSize = size,
            Gravity = Android.Views.GravityFlags.Center
        };
        label.SetTextColor(color);
        return label;
    }

    private static Android.Widget.LinearLayout.LayoutParams EffectDialogLayout(
        Android.Content.Context context, int bottomMargin) =>
        new(Android.Views.ViewGroup.LayoutParams.MatchParent,
            Android.Views.ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = EffectDialogPixels(context, bottomMargin)
        };

    private static Android.Graphics.Drawables.GradientDrawable EffectDialogBackground(
        Android.Content.Context context, Android.Graphics.Color color, int radius)
    {
        var background = new Android.Graphics.Drawables.GradientDrawable();
        background.SetColor(color);
        background.SetCornerRadius(EffectDialogPixels(context, radius));
        return background;
    }

    private static int EffectDialogPixels(Android.Content.Context context, int dp) =>
        (int)Math.Round(dp * (context.Resources?.DisplayMetrics?.Density ?? 1f));
#endif
}
