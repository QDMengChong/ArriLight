namespace ArriLight;

public static class ToastService
{
    public static void Show(string message)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Android.Widget.Toast
                .MakeText(
                    Android.App.Application.Context,
                    message,
                    Android.Widget.ToastLength.Short)
                ?.Show();
        });
#else
        System.Diagnostics.Debug.WriteLine($"Toast: {message}");
#endif
    }

    /// <summary>显示指定时长的短提示，适合一次性的操作结果反馈。</summary>
    public static void ShowForDuration(string message, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            Show(message);
            return;
        }

#if ANDROID
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var toast = Android.Widget.Toast.MakeText(
                Android.App.Application.Context,
                message,
                Android.Widget.ToastLength.Short);
            toast?.Show();
            await Task.Delay(duration);
            toast?.Cancel();
        });
#else
        System.Diagnostics.Debug.WriteLine($"Toast: {message}");
#endif
    }
}
