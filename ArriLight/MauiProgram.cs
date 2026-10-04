using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;
using ArriOpto.BLE.Ota;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using ZXing.Net.Maui.Controls;

namespace ArriLight;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        Localization.Initialize();
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<ILocalizationService>(Localization.Current);
        builder.Services.AddSingleton<OpenSourceLicenseCatalog>();
        builder.Services.AddSingleton(CrossBluetoothLE.Current);
        builder.Services.AddSingleton<IAdapter>(
            _ => CrossBluetoothLE.Current.Adapter);
        builder.Services.AddSingleton<AppDiagnosticService>();
        builder.Services.AddSingleton<BluetoothDiscoveryService>();
        builder.Services.AddSingleton<BluetoothConnectionService>();
        builder.Services.AddSingleton<AgpLightControlService>();
        builder.Services.AddSingleton<DeviceRepository>();
        builder.Services.AddSingleton<FirmwareUpdateConfiguration>();
        builder.Services.AddSingleton(
            _ => new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            });
        builder.Services.AddSingleton<PluginBleOtaClient>();
        builder.Services.AddSingleton<FirmwareUpdateService>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
