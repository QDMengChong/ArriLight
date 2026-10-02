namespace ArriLight;

internal static class DeviceControlPageRoutes
{
    private static readonly PreparedControlPageRouteFactory Light =
        new(nameof(DeviceControlPage), () => new DeviceControlPage());
    private static readonly PreparedControlPageRouteFactory Demo =
        new(nameof(HslCtlDeviceControlPage), () => new HslCtlDeviceControlPage());

    internal static void Register()
    {
        Routing.RegisterRoute(nameof(DeviceControlPage), Light);
        Routing.RegisterRoute(nameof(HslCtlDeviceControlPage), Demo);
    }

    internal static void Prepare(bool isDemo) => (isDemo ? Demo : Light).Prepare();
}
