using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions;
using Xunit;

namespace ArriLight.Tests;

public sealed class AgpAdvertisementTests
{
    // PW01 AD type 0x21 payload: BLE little-endian AGP UUID, version, status.
    private const string PairingOpenData = "D7F906F860FC0F87084433C40124098F0101";

    [Theory]
    [InlineData(AdvertisementRecordType.ServiceData128Bit, 1, AgpBroadcastStatus.PairingOpen)]
    [InlineData(AdvertisementRecordType.ServiceData128Bit, 2, AgpBroadcastStatus.BondedOnly)]
    [InlineData(AdvertisementRecordType.ServiceData, 1, AgpBroadcastStatus.PairingOpen)]
    [InlineData(AdvertisementRecordType.ServiceData, 2, AgpBroadcastStatus.BondedOnly)]
    public void AndroidAndIosServiceDataHaveTheSameStatus(
        AdvertisementRecordType type, byte statusByte, AgpBroadcastStatus expected)
    {
        var data = Convert.FromHexString(PairingOpenData);
        data[17] = statusByte;
        AdvertisementRecord[] records = [new(type, data)];

        Assert.True(AgpAdvertisement.TryGetBroadcastStatus(records, out var status));
        Assert.Equal(expected, status);
        Assert.Equal(expected == AgpBroadcastStatus.PairingOpen,
            AgpAdvertisement.IsPairingOpen(records));
    }

    [Theory]
    [InlineData(AdvertisementRecordType.ServiceData128Bit)]
    [InlineData(AdvertisementRecordType.ServiceData)]
    public void InvalidDataCannotOpenPairing(AdvertisementRecordType type)
    {
        var wrongUuid = Convert.FromHexString(PairingOpenData);
        wrongUuid[0] ^= 1;
        var wrongVersion = Convert.FromHexString(PairingOpenData);
        wrongVersion[16] = 2;
        var unknownStatus = Convert.FromHexString(PairingOpenData);
        unknownStatus[17] = 3;
        var bigEndianUuid = Convert.FromHexString(PairingOpenData);
        Array.Reverse(bigEndianUuid, 0, 16);
        var dotNetGuidUuid = AgpAdvertisement.StatusServiceId.ToByteArray()
            .Concat(new byte[] { 1, 1 }).ToArray();

        byte[][] invalidPayloads =
        [
            [], [1, 1], [0x27, 0x18, 1, 1],
            Convert.FromHexString(PairingOpenData)[..17],
            wrongUuid, wrongVersion, unknownStatus, bigEndianUuid, dotNetGuidUuid
        ];
        foreach (var data in invalidPayloads)
        {
            AdvertisementRecord[] records = [new(type, data)];
            Assert.False(AgpAdvertisement.TryGetBroadcastStatus(records, out var status));
            Assert.Equal(AgpBroadcastStatus.Unknown, status);
            Assert.False(AgpAdvertisement.IsPairingOpen(records));
        }
    }

    [Theory]
    [InlineData(AdvertisementRecordType.ManufacturerSpecificData)]
    [InlineData(AdvertisementRecordType.UuidsComplete128Bit)]
    [InlineData(AdvertisementRecordType.ServiceDataUuid32Bit)]
    public void OtherRecordTypesCannotImpersonateAgp(AdvertisementRecordType type)
    {
        AdvertisementRecord[] records = [new(type, Convert.FromHexString(PairingOpenData))];

        Assert.False(AgpAdvertisement.TryGetBroadcastStatus(records, out var status));
        Assert.Equal(AgpBroadcastStatus.Unknown, status);
        Assert.False(AgpAdvertisement.IsPairingOpen(records));
    }

    [Fact]
    public void MissingRecordsCannotOpenPairing()
    {
        Assert.False(AgpAdvertisement.TryGetBroadcastStatus(null, out var status));
        Assert.Equal(AgpBroadcastStatus.Unknown, status);
        Assert.False(AgpAdvertisement.IsPairingOpen([]));
    }

    [Fact]
    public void UnrelatedServiceDataDoesNotHideIosAgpRecord()
    {
        AdvertisementRecord[] records =
        [
            new(AdvertisementRecordType.ServiceData, [0x0F, 0x18, 100]),
            new(AdvertisementRecordType.CompleteLocalName, "PhyLight-12345678"u8.ToArray()),
            new(AdvertisementRecordType.ServiceData, Convert.FromHexString(PairingOpenData))
        ];

        Assert.True(AgpAdvertisement.IsPairingOpen(records));
    }
}
