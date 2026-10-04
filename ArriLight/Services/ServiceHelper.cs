using Microsoft.Extensions.DependencyInjection;

namespace ArriLight;

internal static class ServiceHelper
{
    public static T GetRequiredService<T>() where T : notnull
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException(AppResources.Common_ServicesNotInitialized);
        return services.GetRequiredService<T>();
    }
}
