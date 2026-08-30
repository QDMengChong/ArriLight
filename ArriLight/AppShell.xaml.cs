namespace ArriLight
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute(nameof(MessageCenterPage), typeof(MessageCenterPage));
            Routing.RegisterRoute(nameof(AppVersionPage), typeof(AppVersionPage));
            Routing.RegisterRoute(nameof(AboutUsPage), typeof(AboutUsPage));
            Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
            Routing.RegisterRoute(nameof(LegalDocumentPage), typeof(LegalDocumentPage));
            Routing.RegisterRoute(nameof(DeviceControlPage), typeof(DeviceControlPage));
            Routing.RegisterRoute(
                nameof(HslDeviceControlPage),
                typeof(HslDeviceControlPage));
            Routing.RegisterRoute(
                nameof(CtlDeviceControlPage),
                typeof(CtlDeviceControlPage));
            Routing.RegisterRoute(
                nameof(HslCtlDeviceControlPage),
                typeof(HslCtlDeviceControlPage));
            Routing.RegisterRoute(nameof(SchedulePage), typeof(SchedulePage));
            Routing.RegisterRoute(nameof(BluetoothScanPage), typeof(BluetoothScanPage));
            Routing.RegisterRoute(nameof(QrCodeScanPage), typeof(QrCodeScanPage));
            Routing.RegisterRoute(nameof(DeviceMorePage), typeof(DeviceMorePage));
            Routing.RegisterRoute(nameof(DeviceManualPage), typeof(DeviceManualPage));
            Routing.RegisterRoute(nameof(FirmwareUpdatePage), typeof(FirmwareUpdatePage));
            Routing.RegisterRoute(nameof(DeviceSharePage), typeof(DeviceSharePage));
        }
    }
}
