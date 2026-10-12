using System.Text.Json.Serialization;
using ArriOpto.BLE.Agp;
using ArriOpto.BLE.Mesh.Models;

namespace ArriLight;

/// <summary>已确认可用的灯光模型及所在元素；与 GATT 页共用显示能力。</summary>
public sealed record MeshControlProfile(
    ushort? OnOffAddress = null,
    ushort? LightnessAddress = null,
    ushort? HslAddress = null,
    ushort? CtlAddress = null,
    AgpCtlRange? TemperatureRange = null)
{
    [JsonIgnore]
    public AgpCtlRange CtlRange => TemperatureRange ?? new(
        AgpColorModelCodec.MinimumCtlTemperatureKelvin,
        AgpColorModelCodec.MaximumCtlTemperatureKelvin);

    [JsonIgnore]
    public AgpCapabilities Capabilities =>
        (OnOffAddress is null ? AgpCapabilities.None : AgpCapabilities.OnOff) |
        (LightnessAddress is null && HslAddress is null && CtlAddress is null
            ? AgpCapabilities.None : AgpCapabilities.Lightness16) |
        (HslAddress is null ? AgpCapabilities.None : AgpCapabilities.Hsl) |
        (CtlAddress is null ? AgpCapabilities.None : AgpCapabilities.Ctl);

    public static MeshControlProfile FromComposition(ushort primaryAddress, CompositionDataStatus composition)
    {
        ushort? Find(ushort model) => composition.Elements
            .Select((element, index) => (element, index))
            .Where(item => item.element.SigModelIds.Contains(model))
            .Select(item => (ushort?)checked((ushort)(primaryAddress + item.index)))
            .FirstOrDefault();

        return new(
            Find(SigModelIds.GenericOnOffServer),
            Find(SigModelIds.LightLightnessServer),
            Find(SigModelIds.LightHslServer),
            Find(SigModelIds.LightCtlServer));
    }
}
