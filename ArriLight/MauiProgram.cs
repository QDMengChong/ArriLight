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
        builder.Services.AddSingleton<IUiDispatcher, MauiUiDispatcher>();
        builder.Services.AddSingleton<IAppNavigator, ShellAppNavigator>();
        builder.Services.AddSingleton<IUserInteraction, MauiUserInteraction>();
        builder.Services.AddSingleton<IApplicationPresentation, MauiApplicationPresentation>();
        builder.Services.AddSingleton<IFirmwareFilePicker, MauiFirmwareFilePicker>();
        builder.Services.AddTransient<AboutUsPageViewModel>();
        builder.Services.AddTransient<AppVersionPageViewModel>();
        builder.Services.AddTransient<BluetoothScanPageViewModel>();
        builder.Services.AddTransient<DebugConfigurationPageViewModel>();
        builder.Services.AddTransient<DeviceControlPageViewModel>();
        builder.Services.AddTransient<DeviceMorePageViewModel>();
        builder.Services.AddTransient<DeviceSharePageViewModel>();
        builder.Services.AddTransient<FirmwareUpdatePageViewModel>();
        builder.Services.AddTransient<LegalDocumentPageViewModel>();
        builder.Services.AddTransient<MainPageViewModel>();
        builder.Services.AddTransient<MessageCenterPageViewModel>();
        builder.Services.AddTransient<OpenSourceLicensePageViewModel>();
        builder.Services.AddTransient<OpenSourceLicensesPageViewModel>();
        builder.Services.AddTransient<ProfilePageViewModel>();
        builder.Services.AddTransient<QrCodeScanPageViewModel>();
        builder.Services.AddTransient<SchedulePageViewModel>();
        builder.Services.AddTransient<SettingsPageViewModel>();

        builder.Services.AddSingleton(CrossBluetoothLE.Current);
        builder.Services.AddSingleton<IAdapter>(
            _ => CrossBluetoothLE.Current.Adapter);
        builder.Services.AddSingleton<AppDiagnosticService>();
        builder.Services.AddSingleton<BluetoothDiscoveryService>();
        builder.Services.AddSingleton<BluetoothConnectionService>();
        builder.Services.AddSingleton<AgpLightControlService>();
        builder.Services.AddSingleton<MeshNetworkManager>();
        builder.Services.AddSingleton<MeshLightControlService>();
        builder.Services.AddSingleton<MeshDeviceControlService>();
        // AGP Runtime 与 Mesh 控制共享一份设备预算/串行器。
        builder.Services.AddSingleton<DeviceWorkScheduler>();
        builder.Services.AddSingleton<DeviceRepository>();
        builder.Services.AddSingleton<AppSettingsService>();
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
