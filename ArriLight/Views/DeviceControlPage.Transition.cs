namespace ArriLight;

public partial class DeviceControlPage
{
    private const uint EntranceAnimationMilliseconds = 120;
    private const double EntranceOffset = 14;

    private static bool EntranceAnimationEnabled()
    {
#if ANDROID
        var resolver = Android.App.Application.Context.ContentResolver;
        if (resolver is not null)
        {
            try
            {
                return Android.Provider.Settings.Global.GetFloat(
                    resolver, Android.Provider.Settings.Global.AnimatorDurationScale, 1f) > 0;
            }
            catch { /* Use the normal transition if the system setting is unavailable. */ }
        }
#endif
        return true;
    }

    private void PrepareEntranceTransition()
    {
        if (!EntranceAnimationEnabled())
            return;
        ControlPageContent.Opacity = 0.94;
        ControlPageContent.TranslationX = EntranceOffset;
    }

    private void ResetEntranceTransition()
    {
        ControlPageContent.CancelAnimations();
        ControlPageContent.Opacity = 1;
        ControlPageContent.TranslationX = 0;
        ControlPageContent.TranslationY = 0;
    }

    private async Task PlayEntranceTransitionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!EntranceAnimationEnabled())
        {
            ResetEntranceTransition();
            return;
        }
        using var timing = _diagnostics.BeginBleTiming("UI.Control.Entrance", DeviceAddress);
        await Task.WhenAll(
            ControlPageContent.FadeToAsync(1, EntranceAnimationMilliseconds, Easing.CubicOut),
            ControlPageContent.TranslateToAsync(0, 0, EntranceAnimationMilliseconds, Easing.CubicOut));
        cancellationToken.ThrowIfCancellationRequested();
        timing.SetResult("shown");
    }
}
