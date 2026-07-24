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
}
