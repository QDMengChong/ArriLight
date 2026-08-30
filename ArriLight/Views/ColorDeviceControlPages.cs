namespace ArriLight;

/// <summary>仅支持 AGP HSL 色彩模型的灯控页面。</summary>
public sealed class HslDeviceControlPage : DeviceControlPage
{
    public HslDeviceControlPage()
        : base(AgpControlPageKind.Hsl)
    {
    }
}

/// <summary>仅支持 AGP CTL 色彩模型的灯控页面。</summary>
public sealed class CtlDeviceControlPage : DeviceControlPage
{
    public CtlDeviceControlPage()
        : base(AgpControlPageKind.Ctl)
    {
    }
}

/// <summary>同时支持 AGP HSL 与 CTL 色彩模型的灯控页面。</summary>
public sealed class HslCtlDeviceControlPage : DeviceControlPage
{
    public HslCtlDeviceControlPage()
        : base(AgpControlPageKind.HslAndCtl)
    {
    }
}
