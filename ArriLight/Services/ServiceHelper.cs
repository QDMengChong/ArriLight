using Microsoft.Extensions.DependencyInjection;

namespace ArriLight;

internal static class ServiceHelper
{
    public static T GetRequiredService<T>() where T : notnull
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("应用服务尚未初始化。");
        return services.GetRequiredService<T>();
    }
}
