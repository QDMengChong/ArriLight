using ArriOpto.BLE.Mesh;
using ArriOpto.BLE.Mesh.Crypto;
using Xunit;

namespace ArriLight.Tests;

public sealed class MeshSegmentedAccessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReassemblesOutOfOrderAcrossSequenceBoundaryAndAcknowledgesWithoutDuplicateDelivery(bool appKey, bool longMic)
    {
        var config = Config();
        var receiver = new MeshSegmentedAccessReceiver();
        var access = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
        var packets = Segments(config, access, appKey, longMic);
        for (var index = packets.Count - 1; index > 0; index--)
        {
            Assert.Null(receiver.Accept(packets[index], config));
            Assert.Null(receiver.Accept(packets[index], config));
        }
        var result = receiver.Accept(packets[0], config);
        Assert.NotNull(result);
        Assert.Equal(access, result!.Value.Message!.AccessPdu);
        Assert.Equal((ushort)2, result.Value.Message.Source);
        Assert.Equal((ushort)1, result.Value.Message.Destination);
        Assert.False(MeshSegmentedAccessReceiver.IsAccessProxyPdu(result.Value.Acknowledgment, config));
        Assert.True(MeshNetworkLayer.TryDecryptNetworkPdu(result.Value.Acknowledgment.AsSpan(1), config.EncryptionKey,
            config.PrivacyKey, config.Nid, config.IvIndex, out var ack));
        Assert.Equal(new byte[] { 0, 127, 252, 0, 0, 0, 15 }, ack.TransportPdu);
        var duplicate = receiver.Accept(packets[0], config);
        Assert.NotNull(duplicate);
        Assert.Null(duplicate!.Value.Message);
        Assert.NotEqual(result.Value.Acknowledgment, duplicate.Value.Acknowledgment);
    }

    [Fact]
    public void IncompleteOrInvalidMicCannotPublishAnAccessMessageOrAcknowledgment()
    {
        var config = Config();
        var receiver = new MeshSegmentedAccessReceiver();
        var wrongKey = Config();
        wrongKey.DeviceKey[0] ^= 0xFF;
        var packets = Segments(wrongKey, new byte[30], appKey: false, longMic: false);
        foreach (var packet in packets) Assert.Null(receiver.Accept(packet, config));
        var valid = Segments(config, new byte[30], appKey: false, longMic: false);
        Assert.Null(receiver.Accept(valid[0], config));
        Assert.Null(receiver.Accept(valid[2], config));
        Assert.NotNull(receiver.Accept(valid[1], config));
    }

    private static MeshNetworkConfig Config() => MeshNetworkConfig.FromNetwork(new MeshNetwork
    {
        NetKey = Convert.FromHexString("7DD7364CD842AD18C17C2B820C84C3D6"),
        AppKey = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        IvIndex = [0x12, 0x34, 0x56, 0x78],
        ProvisionerAddress = 1
    }, new MeshNode { UnicastAddress = 2, DeviceKey = Convert.FromHexString("E1183B94B1C84032088DF776CDDBD615") });

    private static IReadOnlyList<byte[]> Segments(MeshNetworkConfig config, byte[] access, bool appKey, bool longMic)
    {
        byte[] seq0 = [0, 0x1F, 0xFF];
        var upper = appKey ? MeshTransportLayer.EncryptUpperTransportPdu(config.AppKey, longMic, seq0, 2, 1, config.IvIndex, access) :
            MeshTransportLayer.EncryptDeviceKeyUpperTransportPdu(config.DeviceKey, longMic, seq0, 2, 1, config.IvIndex, access);
        return MeshTransportLayer.CreateSegmentedLowerTransportPdus(appKey, appKey ? config.Aid : (byte)0, longMic, seq0, upper)
            .Select((part, index) =>
            {
                var seq = 0x1FFF + index;
                byte[] sequence = [(byte)(seq >> 16), (byte)(seq >> 8), (byte)seq];
                var network = MeshNetworkLayer.BuildNetworkPdu(config.EncryptionKey, config.PrivacyKey, config.Nid,
                    config.IvIndex, 0, config.Ttl, sequence, 2, 1, part);
                return MeshProxyPdu.Create(MeshProxyPdu.MessageTypeNetwork, network);
            }).ToArray();
    }
}
