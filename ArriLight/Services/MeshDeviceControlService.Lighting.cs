using System.Collections.Concurrent;
using ArriOpto.BLE.Agp;
using ArriOpto.BLE.Mesh;
using ArriOpto.BLE.Mesh.Models;

namespace ArriLight;

public partial class MeshDeviceControlService
{
    private readonly ConcurrentDictionary<string, MeshControlProfile> _profiles =
        new(StringComparer.OrdinalIgnoreCase);

    public MeshControlProfile? GetCachedControlProfile(string address) =>
        _profiles.GetValueOrDefault(address) ?? _repository?.GetByAddress(address)?.MeshProfile;

    public virtual async Task<MeshControlProfile?> GetControlProfileAsync(
        string address, bool refresh = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!refresh && GetCachedControlProfile(address) is { } cached)
            return cached;

        return await RunProxySessionAsync<MeshControlProfile?>(address, "Mesh.Capabilities.Get", StatusPriority,
            async (client, node, token) =>
            {
                MeshControlProfile? profile = null;
                if (node.DeviceKey.Length == 16)
                {
                    var composition = await ReadCompositionDataAsync(client, node, address, token);
                    if (composition is { Page: 0 } data)
                    {
                        var models = MeshControlProfile.FromComposition(node.UnicastAddress, data);
                        // PW01 自动绑定 HSL/CTL，但其扩展的 Lightness 模型可能没有绑定。
                        // 配置模型绑定确认后才公开控件，并按实际元素地址发送。
                        profile = new(
                            await BindAsync(models.OnOffAddress, SigModelIds.GenericOnOffServer),
                            models.HslAddress is null && models.CtlAddress is null
                                ? await BindAsync(models.LightnessAddress, SigModelIds.LightLightnessServer) : null,
                            await BindAsync(models.HslAddress, SigModelIds.LightHslServer),
                            await BindAsync(models.CtlAddress, SigModelIds.LightCtlServer));
                    }
                }

                // 旧固件或仅控制导入缺少组成数据时，不凭协议类型猜能力。
                profile ??= await ProbeProfileAsync(client, node, address, token);
                if (profile is not null)
                    CacheProfile(address, profile);
                return profile;

                async Task<ushort?> BindAsync(ushort? element, ushort model)
                {
                    if (element is null) return null;
                    var statusTask = client.WaitForAccessMessageFromAsync(node.UnicastAddress,
                        MeshOpcode.Encode(MeshOpcode.ConfigModelAppStatus), StatusTimeout, token);
                    await client.SendDeviceKeyMessageAsync(ConfigModel.ModelAppBind(element.Value,
                        _networkManager.FindNetworkForNode(address)!.AppKeyIndex, new ModelIdentifier(model)), token);
                    try
                    {
                        var response = await statusTask;
                        if (ConfigModel.TryParseModelAppStatus(response.AccessPdu, out var status) &&
                            status.StatusCode == 0 && status.ElementAddress == element &&
                            status.AppKeyIndex == _networkManager.FindNetworkForNode(address)!.AppKeyIndex &&
                            status.ModelId == new ModelIdentifier(model))
                            return element;
                    }
                    catch (TimeoutException) { }
                    _diagnostics.Warning("Mesh/Config", $"模型绑定未确认：device={address}，model=0x{model:X4}。");
                    return null;
                }
            }, cancellationToken);
    }

    private void CacheProfile(string address, MeshControlProfile profile)
    {
        _profiles[address] = profile;
        _repository?.SetMeshControlProfile(address, profile);
    }

    private async Task<CompositionDataStatus?> ReadCompositionDataAsync(
        MeshClient client, MeshNode node, string address, CancellationToken token)
    {
        var transport = _sessions[address].Transport!;
        var completion = new TaskCompletionSource<MeshAccessMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(object? sender, MeshAccessMessage message)
        {
            if (message.Source == node.UnicastAddress &&
                ConfigModel.TryParseCompositionDataStatus(message.AccessPdu, out _)) completion.TrySetResult(message);
        }
        client.DetailedAccessMessageReceived += Received;
        transport.SegmentedAccessMessageReceived += Received;
        try
        {
            await client.SendDeviceKeyMessageAsync(ConfigModel.CompositionDataGet(), token);
            var message = await completion.Task.WaitAsync(StatusTimeout, token);
            return ConfigModel.TryParseCompositionDataStatus(message.AccessPdu, out var parsed) ? parsed : null;
        }
        catch (TimeoutException) { return null; }
        finally
        {
            client.DetailedAccessMessageReceived -= Received;
            transport.SegmentedAccessMessageReceived -= Received;
        }
    }

    private async Task<MeshControlProfile?> ProbeProfileAsync(
        MeshClient client, MeshNode node, string address, CancellationToken token)
    {
        var onOff = await SendAndWaitAsync(client, node, MeshOpcode.GenericOnOffStatus,
            GenericOnOffModel.Get(), "OnOff.Get(能力)", token);
        var hasOnOff = onOff is not null && GenericOnOffModel.TryParseStatus(onOff.AccessPdu, out _);
        var hsl = await ReadHslAsync(client, node, address, node.UnicastAddress, token);
        var ctl = await ReadCtlAsync(client, node, address, node.UnicastAddress, token);
        var lightness = !hsl && !ctl &&
            await ReadLightnessAsync(client, node, address, node.UnicastAddress, token);
        return hasOnOff || hsl || ctl || lightness ? new MeshControlProfile(
            hasOnOff ? node.UnicastAddress : null,
            lightness ? node.UnicastAddress : null,
            hsl ? node.UnicastAddress : null,
            ctl ? node.UnicastAddress : null) : null;
    }

    public virtual async Task<MeshDeviceState?> GetLightingStateAsync(
        string address, CancellationToken cancellationToken = default)
    {
        var profile = await GetControlProfileAsync(address, cancellationToken: cancellationToken);
        if (profile is null) return null;
        return await RunProxySessionAsync<MeshDeviceState?>(address, "Mesh.Light.Get", StatusPriority,
            async (client, node, token) =>
            {
                var received = false;
                if (profile.HslAddress is { } hsl)
                    received |= await ReadHslAsync(client, node, address, hsl, token);
                if (profile.CtlAddress is { } ctl)
                {
                    received |= await ReadCtlAsync(client, node, address, ctl, token);
                    var response = await SendAndWaitAsync(client, ctl, MeshOpcode.LightCtlTemperatureRangeStatus,
                        LightCtlModel.TemperatureRangeGet(), "CTL.Range.Get", token);
                    if (response is not null &&
                        LightCtlModel.TryParseTemperatureRangeStatus(response.AccessPdu, out var range) &&
                        range.StatusCode == 0 &&
                        range.RangeMin >= AgpColorModelCodec.MinimumCtlTemperatureKelvin &&
                        range.RangeMax <= AgpColorModelCodec.MaximumCtlTemperatureKelvin && range.RangeMin <= range.RangeMax)
                        CacheProfile(address, profile with { TemperatureRange = new(range.RangeMin, range.RangeMax) });
                }
                if (profile.HslAddress is null && profile.CtlAddress is null && profile.LightnessAddress is { } lightness)
                    received |= await ReadLightnessAsync(client, node, address, lightness, token);
                if (!received)
                {
                    MarkRuntimeUnknown(address, "灯光状态未知");
                    return null;
                }
                return GetCachedState(address);
            }, cancellationToken);
    }

    private async Task<bool> ReadHslAsync(MeshClient client, MeshNode node, string address,
        ushort element, CancellationToken token)
    {
        var response = await SendAndWaitAsync(client, element, MeshOpcode.LightHslStatus, LightHslModel.Get(), "HSL.Get", token);
        if (response is null || !LightHslModel.TryParseStatus(response.AccessPdu, out var status)) return false;
        PublishState(address, node, lightnessPercent: ToPercent(status.Lightness), hue: status.Hue,
            saturation: status.Saturation, activeColorModel: GetCachedState(address)?.ActiveColorModel ?? AgpColorModel.Hsl);
        return true;
    }

    private async Task<bool> ReadCtlAsync(MeshClient client, MeshNode node, string address,
        ushort element, CancellationToken token)
    {
        var response = await SendAndWaitAsync(client, element, MeshOpcode.LightCtlStatus, LightCtlModel.Get(), "CTL.Get", token);
        if (response is null || !LightCtlModel.TryParseStatus(response.AccessPdu, out var status)) return false;
        PublishState(address, node, lightnessPercent: ToPercent(status.PresentLightness), temperatureKelvin: status.PresentTemperature,
            activeColorModel: GetCachedState(address)?.ActiveColorModel ?? AgpColorModel.Ctl);
        return true;
    }

    private async Task<bool> ReadLightnessAsync(MeshClient client, MeshNode node, string address,
        ushort element, CancellationToken token)
    {
        var response = await SendAndWaitAsync(client, element, MeshOpcode.LightLightnessStatus,
            LightLightnessModel.Get(), "Lightness.Get", token);
        if (response is null || !LightLightnessModel.TryParseStatus(response.AccessPdu, out var status)) return false;
        PublishState(address, node, lightnessPercent: ToPercent(status.PresentLightness));
        return true;
    }

    public virtual Task<bool> SetHslAsync(string address, byte brightnessPercent, double hue,
        double saturation, CancellationToken cancellationToken = default) =>
        RunProxySessionAsync(address, "Mesh.Hsl.Set", UserPriority,
            (client, node, token) => SetHslCoreAsync(client, node, address, brightnessPercent,
                ToComponent(hue), ToComponent(saturation), token), cancellationToken);

    public virtual Task<bool> SetCtlAsync(string address, byte brightnessPercent, ushort temperatureKelvin,
        CancellationToken cancellationToken = default) =>
        RunProxySessionAsync(address, "Mesh.Ctl.Set", UserPriority,
            (client, node, token) => SetCtlCoreAsync(client, node, address, brightnessPercent, temperatureKelvin, token),
            cancellationToken);

    private async Task<bool> SetBrightnessCoreAsync(MeshClient client, MeshNode node, string address,
        byte percent, CancellationToken token)
    {
        var profile = GetCachedControlProfile(address) ?? throw new InvalidOperationException(AppResources.Mesh_CapabilitiesUnavailable);
        var state = GetCachedState(address);
        if (profile.CtlAddress is { } ctl && (profile.HslAddress is null || state?.ActiveColorModel == AgpColorModel.Ctl))
        {
            if (state?.TemperatureKelvin is null && !await ReadCtlAsync(client, node, address, ctl, token))
                throw new InvalidOperationException(AppResources.Mesh_LightStateUnavailable);
            return await SetCtlCoreAsync(client, node, address, percent, GetCachedState(address)!.TemperatureKelvin!.Value, token);
        }
        if (profile.HslAddress is { } hsl)
        {
            if ((state?.Hue is null || state.Saturation is null) && !await ReadHslAsync(client, node, address, hsl, token))
                throw new InvalidOperationException(AppResources.Mesh_LightStateUnavailable);
            state = GetCachedState(address)!;
            return await SetHslCoreAsync(client, node, address, percent, state.Hue!.Value, state.Saturation!.Value, token);
        }
        var element = profile.LightnessAddress ?? throw new NotSupportedException(AppResources.Mesh_ModelUnavailable);
        var response = await SendAndWaitAsync(client, element, MeshOpcode.LightLightnessStatus,
            LightLightnessModel.Set(ToLightness(percent), NextTid(address)), "Lightness.Set", token);
        if (response is null || !LightLightnessModel.TryParseStatus(response.AccessPdu, out var status))
            throw UnknownLightingResult(address);
        PublishState(address, node, isOn: status.PresentLightness > 0, lightnessPercent: ToPercent(status.PresentLightness));
        return true;
    }

    private async Task<bool> SetHslCoreAsync(MeshClient client, MeshNode node, string address,
        byte percent, ushort hue, ushort saturation, CancellationToken token)
    {
        var element = GetCachedControlProfile(address)?.HslAddress ?? throw new NotSupportedException(AppResources.Mesh_ModelUnavailable);
        var response = await SendAndWaitAsync(client, element, MeshOpcode.LightHslStatus,
            LightHslModel.Set(ToLightness(percent), hue, saturation, NextTid(address)), "HSL.Set", token);
        if (response is null || !LightHslModel.TryParseStatus(response.AccessPdu, out var status))
            throw UnknownLightingResult(address);
        PublishState(address, node, isOn: status.Lightness > 0, lightnessPercent: ToPercent(status.Lightness),
            hue: status.Hue, saturation: status.Saturation, activeColorModel: AgpColorModel.Hsl);
        return true;
    }

    private async Task<bool> SetCtlCoreAsync(MeshClient client, MeshNode node, string address,
        byte percent, ushort temperature, CancellationToken token)
    {
        var profile = GetCachedControlProfile(address);
        var element = profile?.CtlAddress ?? throw new NotSupportedException(AppResources.Mesh_ModelUnavailable);
        temperature = Math.Clamp(temperature, profile!.CtlRange.TemperatureMinimumKelvin, profile.CtlRange.TemperatureMaximumKelvin);
        var response = await SendAndWaitAsync(client, element, MeshOpcode.LightCtlStatus,
            LightCtlModel.Set(ToLightness(percent), temperature, 0, NextTid(address)), "CTL.Set", token);
        if (response is null || !LightCtlModel.TryParseStatus(response.AccessPdu, out var status))
            throw UnknownLightingResult(address);
        PublishState(address, node, isOn: status.PresentLightness > 0, lightnessPercent: ToPercent(status.PresentLightness),
            temperatureKelvin: status.PresentTemperature, activeColorModel: AgpColorModel.Ctl);
        return true;
    }

    private TimeoutException UnknownLightingResult(string address)
    {
        MarkRuntimeUnknown(address, "灯光设置未收到 Status");
        return new TimeoutException(AppResources.Mesh_LightResultUnknown);
    }

    private static ushort ToComponent(double value) =>
        (ushort)Math.Round(Math.Clamp(value, 0, 1) * ushort.MaxValue);
}
