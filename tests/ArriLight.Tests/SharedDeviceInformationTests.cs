using System.Text.Json;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class SharedDeviceInformationTests
{
    [Theory]
    [InlineData(AgpLightMode.Blink)]
    [InlineData(AgpLightMode.Breathe)]
    public async Task ImportedAuthorizationReadsHardwareStateFirmwareAndOnlyCurrentEffect(
        AgpLightMode mode)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.SecureControl | AgpCapabilities.OnOff | AgpCapabilities.Effects;
        var imported = ImportSharedDevice(fixture);
        fixture.Ble.LightStatePacket = StatePacket(true, 73, mode);
        fixture.Ble.FirmwareRevision = " 3.4.5\0\r\n";
        fixture.Ble.CurrentEffectMode = mode;
        fixture.Ble.CurrentEffectPeriodMilliseconds = 2_500;

        var profile = await fixture.Service.GetControlProfileAsync(fixture.Address);

        Assert.True(profile.LightState!.IsOn);
        Assert.Equal((byte)73, profile.LightState.PresentBrightness);
        Assert.Equal(mode, profile.EffectMode);
        Assert.Equal((ushort)2_500, profile.EffectPeriodMilliseconds);
        Assert.Contains(AgpOpcode.AuthToken, fixture.Ble.Requests);
        Assert.DoesNotContain(AgpOpcode.AuthGetToken, fixture.Ble.Requests);
        Assert.DoesNotContain(AgpProfile.CapabilitiesCharacteristicId, fixture.Ble.Reads);
        Assert.Contains(AgpProfile.LightStateCharacteristicId, fixture.Ble.Reads);
        Assert.Contains(FakeBle.FirmwareRevisionCharacteristicId, fixture.Ble.Reads);
        var authentication = Assert.Single(fixture.Ble.RequestMessages,
            request => request.Opcode == AgpOpcode.AuthToken);
        Assert.Equal(Convert.FromHexString("A1B2C3D4"), authentication.Payload.ToArray());
        var saved = fixture.Repository.GetByAddress(fixture.Address)!;
        Assert.Equal("3.4.5", saved.FirmwareVersion);
        Assert.Equal(imported.AddedAt, saved.AddedAt);
        Assert.Equal("A1B2C3D4", saved.Token);
        Assert.Equal((ushort)(mode == AgpLightMode.Blink ? 2_500 : 1_000),
            saved.BlinkEffectParameters!.PeriodMilliseconds);
        Assert.Equal((ushort)(mode == AgpLightMode.Breathe ? 2_500 : 1_000),
            saved.BreatheEffectParameters!.PeriodMilliseconds);
        Assert.Empty(saved.CustomPresets!);
        Assert.Equal(60, saved.TimerSeconds);
        Assert.Equal(saved.FirmwareVersion,
            new DeviceRepository().GetByAddress(fixture.Address)!.FirmwareVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnreadableFirmwareRemainsUnknownAndLaterSuccessfulReadUpdatesIt(
        bool throwsDuringRead)
    {
        await using var fixture = new PairingFixture();
        ImportSharedDevice(fixture);
        fixture.Ble.FirmwareRevision = "7.8.9";
        if (throwsDuringRead)
            fixture.Ble.FirmwareReadFailure = new IOException("firmware read failed");
        else
            fixture.Ble.FirmwareReadResult = 5;

        await fixture.Service.GetControlProfileAsync(fixture.Address);

        Assert.Equal("未知", fixture.Repository.GetByAddress(fixture.Address)!.FirmwareVersion);
        fixture.Ble.FirmwareReadFailure = null;
        fixture.Ble.FirmwareReadResult = 0;
        await fixture.Service.GetControlProfileAsync(fixture.Address);

        Assert.Equal("7.8.9", fixture.Repository.GetByAddress(fixture.Address)!.FirmwareVersion);
        Assert.Equal(2, fixture.Ble.Reads.Count(id => id == FakeBle.FirmwareRevisionCharacteristicId));
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \0\r\n ")]
    public async Task MissingOrEmptyFirmwareDoesNotReplaceUnknownAndDoesNotPreventRetry(string? revision)
    {
        await using var fixture = new PairingFixture();
        ImportSharedDevice(fixture);
        fixture.Ble.FirmwareRevision = revision;

        await fixture.Service.GetControlProfileAsync(fixture.Address);

        Assert.Equal("未知", fixture.Repository.GetByAddress(fixture.Address)!.FirmwareVersion);
        fixture.Ble.FirmwareRevision = "4.5.6";
        await fixture.Service.GetControlProfileAsync(fixture.Address);

        Assert.Equal("4.5.6", fixture.Repository.GetByAddress(fixture.Address)!.FirmwareVersion);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
    }

    [Fact]
    public async Task FailedEffectReadKeepsLocalDefaultsAndCanBeRetried()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Effects;
        ImportSharedDevice(fixture);
        fixture.Ble.CurrentEffectMode = AgpLightMode.Blink;
        fixture.Ble.CurrentEffectPeriodMilliseconds = 3_200;
        fixture.Ble.EffectResult = AgpResultCode.Busy;

        var first = await fixture.Service.GetControlProfileAsync(fixture.Address);

        Assert.Null(first.EffectPeriodMilliseconds);
        Assert.Equal((ushort)1_000,
            fixture.Repository.GetByAddress(fixture.Address)!.BlinkEffectParameters!.PeriodMilliseconds);
        fixture.Ble.EffectResult = AgpResultCode.Success;
        await fixture.Service.GetControlProfileAsync(fixture.Address);

        var saved = fixture.Repository.GetByAddress(fixture.Address)!;
        Assert.Equal((ushort)3_200, saved.BlinkEffectParameters!.PeriodMilliseconds);
        Assert.Equal((ushort)1_000, saved.BreatheEffectParameters!.PeriodMilliseconds);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.EffectGet));
    }

    [Theory]
    [InlineData((ushort)199)]
    [InlineData((ushort)10_001)]
    public async Task OutOfRangeHardwareEffectPeriodDoesNotOverwriteLocalDefault(ushort period)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Effects;
        ImportSharedDevice(fixture);
        fixture.Ble.CurrentEffectMode = AgpLightMode.Blink;
        fixture.Ble.CurrentEffectPeriodMilliseconds = period;

        await fixture.Service.GetControlProfileAsync(fixture.Address);

        var saved = fixture.Repository.GetByAddress(fixture.Address)!;
        Assert.Equal((ushort)1_000, saved.BlinkEffectParameters!.PeriodMilliseconds);
        Assert.Equal((ushort)1_000, saved.BreatheEffectParameters!.PeriodMilliseconds);
    }

    [Fact]
    public async Task RejectedSharedAuthorizationPreservesRecordWithoutReadingPrivateInformation()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Effects;
        var imported = ImportSharedDevice(fixture);
        fixture.Ble.AuthenticationResult = AgpResultCode.NotAuthorized;
        fixture.Ble.FirmwareRevision = "9.9.9";
        fixture.Ble.CurrentEffectMode = AgpLightMode.Blink;
        fixture.Ble.CurrentEffectPeriodMilliseconds = 3_200;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetControlProfileAsync(fixture.Address));

        Assert.Equal(JsonSerializer.Serialize(imported),
            JsonSerializer.Serialize(fixture.Repository.GetByAddress(fixture.Address)));
        Assert.Contains(AgpOpcode.AuthToken, fixture.Ble.Requests);
        Assert.DoesNotContain(AgpOpcode.EffectGet, fixture.Ble.Requests);
        Assert.DoesNotContain(AgpProfile.LightStateCharacteristicId, fixture.Ble.Reads);
        Assert.DoesNotContain(FakeBle.FirmwareRevisionCharacteristicId, fixture.Ble.Reads);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address));
    }

    [Fact]
    public async Task ConnectionFailurePreservesImportedRecordAndDoesNotInventHardwareData()
    {
        await using var fixture = new PairingFixture();
        var imported = ImportSharedDevice(fixture);
        fixture.Ble.OnConnect = (_, _) => Task.FromException(new IOException("radio unavailable"));

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.GetControlProfileAsync(fixture.Address));

        Assert.Equal(JsonSerializer.Serialize(imported),
            JsonSerializer.Serialize(fixture.Repository.GetByAddress(fixture.Address)));
        Assert.Empty(fixture.Ble.Requests);
        Assert.Empty(fixture.Ble.Reads);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address));
    }

    [Fact]
    public async Task HardwareReadPreservesExistingPresetsTimerAndInactiveEffectSettings()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Effects;
        fixture.Repository.SetTokenAndCapabilities(fixture.Address,
            [0xA1, 0xB2, 0xC3, 0xD4], fixture.Ble.Capabilities);
        fixture.Repository.SetFirmwareVersion(fixture.Address, "local-known-version");
        fixture.Repository.SetPinned(fixture.Address, true);
        fixture.Repository.SetTimerSeconds(fixture.Address, 900);
        fixture.Repository.SetBlinkEffectParameters(fixture.Address, new(1_400));
        fixture.Repository.SetBreatheEffectParameters(fixture.Address, new(1_600));
        fixture.Repository.SetCustomPresets(fixture.Address,
            [new(4, "本机预设", 0.2, 0.4, 0.6, 72, "#ABCDEF", null, null)]);
        var before = fixture.Repository.GetByAddress(fixture.Address)!;
        fixture.Ble.FirmwareRevision = "hardware-version";
        fixture.Ble.CurrentEffectMode = AgpLightMode.Blink;
        fixture.Ble.CurrentEffectPeriodMilliseconds = 2_200;

        await fixture.Service.GetControlProfileAsync(fixture.Address);

        var after = fixture.Repository.GetByAddress(fixture.Address)!;
        Assert.Equal(before.AddedAt, after.AddedAt);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.IsPinned, after.IsPinned);
        Assert.Equal(before.TimerSeconds, after.TimerSeconds);
        Assert.Equal(before.CustomPresets, after.CustomPresets);
        Assert.Equal(before.FirmwareVersion, after.FirmwareVersion);
        Assert.Equal(before.BreatheEffectParameters, after.BreatheEffectParameters);
        Assert.Equal((ushort)2_200, after.BlinkEffectParameters!.PeriodMilliseconds);
        Assert.DoesNotContain(FakeBle.FirmwareRevisionCharacteristicId, fixture.Ble.Reads);
    }

    [Fact]
    public async Task SchedulesIncludeDisabledAndNonconsecutiveConfiguredSlotsWhileFilteringEmptySlots()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Scheduler;
        ImportSharedDevice(fixture);
        fixture.Ble.ScheduleMaximumSlots = 5;
        var enabled = AgpSchedule.Weekly(1, true, AgpScheduleAction.On, 44, 0x05, 25_200);
        var disabled = AgpSchedule.Daily(4, false, AgpScheduleAction.Off, 0, 36_000);
        fixture.Ble.Schedules[1] = enabled;
        fixture.Ble.Schedules[4] = disabled;
        Assert.Equal(8, fixture.Service.GetCachedScheduleSlotCount(fixture.Address));

        var schedules = await fixture.Service.GetSchedulesAsync(fixture.Address);

        Assert.Equal(new[] { enabled, disabled }, schedules);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.ScheduleListGet);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, fixture.Ble.RequestMessages
            .Where(request => request.Opcode == AgpOpcode.ScheduleGet)
            .Select(request => request.Payload.Span[0]).ToArray());
        Assert.Equal(5, fixture.Service.GetCachedScheduleSlotCount(fixture.Address));
    }

    [Fact]
    public async Task DeviceWithoutSchedulerMakesNoScheduleOrConnectionRequests()
    {
        await using var fixture = new PairingFixture();
        ImportSharedDevice(fixture);

        var schedules = await fixture.Service.GetSchedulesAsync(fixture.Address);

        Assert.Empty(schedules);
        Assert.Empty(fixture.Ble.ConnectAttempts);
        Assert.Empty(fixture.Ble.Requests);
        Assert.Empty(fixture.Ble.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedScheduleReadThrowsWithoutReturningAnEmptyOrPartialList(bool listFails)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Scheduler;
        ImportSharedDevice(fixture);
        fixture.Ble.ScheduleMaximumSlots = 5;
        fixture.Ble.Schedules[1] = AgpSchedule.Daily(1, true, AgpScheduleAction.On, 50, 28_800);
        if (listFails)
            fixture.Ble.ScheduleListResult = AgpResultCode.Busy;
        else
            fixture.Ble.ScheduleReadResults[2] = AgpResultCode.Busy;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetSchedulesAsync(fixture.Address));

        Assert.Contains("Busy", error.Message);
        Assert.Equal(8, fixture.Service.GetCachedScheduleSlotCount(fixture.Address));
        Assert.Equal(listFails ? Array.Empty<byte>() : new byte[] { 0, 1, 2 },
            fixture.Ble.RequestMessages.Where(request => request.Opcode == AgpOpcode.ScheduleGet)
                .Select(request => request.Payload.Span[0]).ToArray());
        Assert.NotNull(fixture.Repository.GetByAddress(fixture.Address));
    }

    [Fact]
    public async Task ScheduleResponseForAnotherSlotIsRejected()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Scheduler;
        ImportSharedDevice(fixture);
        fixture.Ble.Schedules[0] = AgpSchedule.Daily(1, true, AgpScheduleAction.On, 50, 28_800);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetSchedulesAsync(fixture.Address));

        Assert.Contains("日程槽", error.Message);
        Assert.Equal(8, fixture.Service.GetCachedScheduleSlotCount(fixture.Address));
    }

    private static SavedDevice ImportSharedDevice(PairingFixture fixture)
    {
        var source = fixture.Repository.GetByAddress(fixture.Address)! with
        {
            Name = "共享灯具",
            FirmwareVersion = "source-only-version",
            Token = "A1B2C3D4",
            Capabilities = fixture.Ble.Capabilities
        };
        Assert.True(DeviceShareCode.TryParse(DeviceShareCode.Encode(source), out var content));
        Assert.True(fixture.Repository.Remove(fixture.Address));
        Assert.True(fixture.Repository.TryAddShared(content));
        var imported = fixture.Repository.GetByAddress(fixture.Address)!;
        Assert.Equal("未知", imported.FirmwareVersion);
        Assert.Equal((ushort)1_000, imported.BlinkEffectParameters!.PeriodMilliseconds);
        Assert.Equal((ushort)1_000, imported.BreatheEffectParameters!.PeriodMilliseconds);
        return imported;
    }

    private static byte[] StatePacket(bool isOn, byte brightness, AgpLightMode mode) =>
        AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0,
            [8, 0, 0, 0, isOn ? (byte)1 : (byte)0, brightness, brightness, 50, 0, (byte)mode, 0]);
}
