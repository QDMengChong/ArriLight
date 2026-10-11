using Xunit;

namespace ArriLight.Tests;

/// <summary>
/// Mesh 亮度换算：UI 侧是 0-100 百分比，Mesh Light HSL 的 Lightness 分量是 0-65535。
/// 换算错误会让亮度看起来"没反应"或"跳变"。
/// </summary>
public sealed class MeshLightnessConversionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 655)]
    [InlineData(50, 32768)]
    [InlineData(100, 65535)]
    public void PercentToLightness(byte percent, ushort expected) =>
        Assert.Equal(expected, MeshLightness.ToLightness(percent));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(655, 1)]
    [InlineData(32768, 50)]
    [InlineData(65535, 100)]
    public void LightnessToPercent(ushort lightness, byte expected) =>
        Assert.Equal(expected, MeshLightness.ToPercent(lightness));

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(75)]
    [InlineData(100)]
    public void RoundTripStaysWithinOneStep(byte percent)
    {
        var converted = MeshLightness.ToPercent(MeshLightness.ToLightness(percent));
        Assert.InRange(converted, percent - 1, percent + 1);
    }

    [Fact]
    public void PercentNeverUnderflowsSilently()
    {
        // 极小亮度也必须保持非零映射区间的可辨识性，不能全部塌成 0。
        Assert.True(MeshLightness.ToLightness(1) > 0);
    }
}
