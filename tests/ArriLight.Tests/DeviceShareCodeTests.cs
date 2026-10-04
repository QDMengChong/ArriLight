using System.Text.Json;
using System.Text.Json.Nodes;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceShareCodeTests
{
    private const string Prefix = "ARRILIGHT:2:";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void ShareCodeContainsExactlyFourRequiredFieldsAsDirectJson()
    {
        var device = CreateFullDevice();

        var code = DeviceShareCode.Encode(device);

        Assert.StartsWith(Prefix, code);
        Assert.InRange(code.Length, Prefix.Length + 1, DeviceShareCode.MaxEncodedLength);
        using var json = JsonDocument.Parse(code[Prefix.Length..]);
        Assert.Equal(new[] { "address", "capabilities", "name", "token" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(device.Name, json.RootElement.GetProperty("name").GetString());
        Assert.Equal(device.Address, json.RootElement.GetProperty("address").GetString());
        Assert.Equal(device.Token, json.RootElement.GetProperty("token").GetString());
        Assert.Equal((ulong)device.Capabilities!.Value,
            json.RootElement.GetProperty("capabilities").GetUInt64());
        Assert.True(DeviceShareCode.TryParse(code, out var content));
        Assert.Equal(new DeviceShareContent(device.Name, device.Address, device.Token!,
            device.Capabilities.Value), content);
    }

    [Fact]
    public void LocalMetadataAndConfigurationAreNeverEncodedOrValidatedForSharing()
    {
        var original = CreateFullDevice();
        var invalidLocalFields = original with
        {
            AddedAt = default,
            IsPinned = false,
            FirmwareVersion = null!,
            IsDemo = false,
            BlinkEffectParameters = new(0),
            BreatheEffectParameters = new(ushort.MaxValue),
            CustomPresets = [new(99, "", double.NaN, -2, 8, 101, "invalid",
                (AgpLightMode)99, 0)],
            TimerSeconds = -1
        };

        Assert.Equal(DeviceShareCode.Encode(original), DeviceShareCode.Encode(invalidLocalFields));
    }

    [Theory]
    [InlineData("aa-bb-cc-dd-ee-01", "AA:BB:CC:DD:EE:01")]
    [InlineData(" AA:BB:CC:DD:EE:01 ", "AA:BB:CC:DD:EE:01")]
    [InlineData("ac17b4d6-b7b2-4ca0-a6d2-94b812f12345", "AC17B4D6-B7B2-4CA0-A6D2-94B812F12345")]
    public void SharedNameAddressAndTokenAreNormalized(string address, string expectedAddress)
    {
        var device = CreateFullDevice() with
        {
            Name = "  客厅主灯  ",
            Address = address,
            Token = "a1b2c3d4"
        };

        Assert.True(DeviceShareCode.TryParse(DeviceShareCode.Encode(device), out var content));

        Assert.Equal("客厅主灯", content.Name);
        Assert.Equal(expectedAddress, content.Address);
        Assert.Equal("A1B2C3D4", content.Token);
    }

    [Fact]
    public void ExplicitNoCapabilitiesIsAcceptedAndPreserved()
    {
        var device = CreateFullDevice() with { Capabilities = AgpCapabilities.None };

        Assert.True(DeviceShareCode.TryParse(DeviceShareCode.Encode(device), out var content));

        Assert.Equal(AgpCapabilities.None, content.Capabilities);
    }

    [Fact]
    public void MaximumLengthNameIsAccepted()
    {
        var device = CreateFullDevice() with { Name = new string('灯', 128) };

        Assert.True(DeviceShareCode.TryParse(DeviceShareCode.Encode(device), out var content));

        Assert.Equal(device.Name, content.Name);
    }

    [Fact]
    public void IncomingJsonIsNormalizedIndependentlyOfTheEncoder()
    {
        var json = CreateShareJson();
        json["name"] = "  接收灯  ";
        json["address"] = "aa-bb-cc-dd-ee-01";
        json["token"] = "a1b2c3d4";

        Assert.True(DeviceShareCode.TryParse(Prefix + json.ToJsonString(), out var content));

        Assert.Equal("接收灯", content.Name);
        Assert.Equal("AA:BB:CC:DD:EE:01", content.Address);
        Assert.Equal("A1B2C3D4", content.Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    [InlineData("unrelated text")]
    [InlineData("AA:BB:CC:DD:EE:01")]
    [InlineData("aa-bb-cc-dd-ee-01")]
    [InlineData("ac17b4d6-b7b2-4ca0-a6d2-94b812f12345")]
    [InlineData("ARRILIGHT:1:")]
    [InlineData("ARRILIGHT:1:AAAA")]
    [InlineData("ARRILIGHT:2:")]
    [InlineData("ARRILIGHT:2:AAAA")]
    public void InvalidCodesAndAddressOnlyCodesAreRejected(string? code)
    {
        Assert.False(DeviceShareCode.TryParse(code, out _));
    }

    [Theory]
    [InlineData("OTHERAPP:2:")]
    [InlineData("ARRILIGHT:0:")]
    [InlineData("ARRILIGHT:1:")]
    [InlineData("ARRILIGHT:3:")]
    [InlineData("ARRILIGHT:100:")]
    public void LegacyAndDifferentApplicationVersionsAreRejected(string prefix)
    {
        var code = DeviceShareCode.Encode(CreateFullDevice());

        Assert.False(DeviceShareCode.TryParse(prefix + code[Prefix.Length..], out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("{\"name\":")]
    public void InvalidJsonDoesNotThrowAndIsRejected(string json)
    {
        Assert.False(DeviceShareCode.TryParse(Prefix + json, out _));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("address")]
    [InlineData("token")]
    [InlineData("capabilities")]
    public void EachShareFieldIsRequired(string field)
    {
        var json = CreateShareJson();
        Assert.True(json.Remove(field));

        Assert.False(DeviceShareCode.TryParse(Prefix + json.ToJsonString(), out _));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Address")]
    [InlineData("Token")]
    [InlineData("Capabilities")]
    public void FieldNamesAreStrictlyCamelCase(string pascalCaseField)
    {
        var json = CreateShareJson();
        var camelCaseField = char.ToLowerInvariant(pascalCaseField[0]) + pascalCaseField[1..];
        var fieldValue = json[camelCaseField]!.DeepClone();
        Assert.True(json.Remove(camelCaseField));
        json[pascalCaseField] = fieldValue;

        Assert.False(DeviceShareCode.TryParse(Prefix + json.ToJsonString(), out _));
    }

    [Theory]
    [InlineData("firmwareVersion")]
    [InlineData("addedAt")]
    [InlineData("isPinned")]
    [InlineData("isDemo")]
    [InlineData("blinkEffectParameters")]
    [InlineData("breatheEffectParameters")]
    [InlineData("customPresets")]
    [InlineData("timerSeconds")]
    [InlineData("unknownProperty")]
    public void ExtraFieldsAreRejected(string field)
    {
        var json = CreateShareJson();
        json[field] = "unwanted data";

        Assert.False(DeviceShareCode.TryParse(Prefix + json.ToJsonString(), out _));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("address")]
    [InlineData("token")]
    [InlineData("capabilities")]
    public void RequiredFieldsCannotBeNull(string field)
    {
        var json = CreateShareJson();
        json[field] = null;

        Assert.False(DeviceShareCode.TryParse(Prefix + json.ToJsonString(), out _));
    }

    [Theory]
    [MemberData(nameof(InvalidSharedFields))]
    public void InvalidShareFieldValuesAreRejected(SavedDevice device)
    {
        var json = JsonSerializer.Serialize(new
        {
            device.Name,
            device.Address,
            device.Token,
            device.Capabilities
        }, JsonOptions);

        Assert.False(DeviceShareCode.TryParse(Prefix + json, out _));
        Assert.Throws<InvalidOperationException>(() => DeviceShareCode.Encode(device));
    }

    public static IEnumerable<object[]> InvalidSharedFields()
    {
        var device = CreateFullDevice();
        yield return [device with { Name = null! }];
        yield return [device with { Name = "  " }];
        yield return [device with { Name = new string('灯', 129) }];
        yield return [device with { Address = "invalid" }];
        yield return [device with { Address = null! }];
        yield return [device with { Token = null }];
        yield return [device with { Token = "" }];
        yield return [device with { Token = "A1B2C3" }];
        yield return [device with { Token = "A1B2C3G4" }];
        yield return [device with { Capabilities = null }];
    }

    [Fact]
    public void WrongCapabilityJsonTypeIsRejected()
    {
        var json = CreateShareJson();
        json["capabilities"] = "SecureControl";

        Assert.False(DeviceShareCode.TryParse(Prefix + json.ToJsonString(), out _));
    }

    [Fact]
    public void CodeOverMaximumLengthIsRejectedEvenWhenItsJsonIsValid()
    {
        var json = DeviceShareCode.Encode(CreateFullDevice());
        var padded = json + new string(' ', DeviceShareCode.MaxEncodedLength);

        Assert.False(DeviceShareCode.TryParse(padded, out _));
    }

    [Fact]
    public void ImportUsesLocalDefaultsAndCurrentAddedDateAndRaisesOneEvent()
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        repository.GetAll();
        var eventCount = 0;
        repository.DevicesChanged += (_, _) => eventCount++;
        var source = CreateFullDevice();
        Assert.True(DeviceShareCode.TryParse(DeviceShareCode.Encode(source), out var content));
        var before = DateTimeOffset.Now;

        Assert.True(repository.TryAddShared(content));

        var after = DateTimeOffset.Now;
        Assert.Equal(1, eventCount);
        var imported = Assert.IsType<SavedDevice>(repository.GetByAddress(source.Address));
        Assert.Equal(source.Name, imported.Name);
        Assert.Equal(source.Token, imported.Token);
        Assert.Equal(source.Capabilities, imported.Capabilities);
        Assert.InRange(imported.AddedAt, before, after);
        Assert.NotEqual(source.AddedAt, imported.AddedAt);
        Assert.False(imported.IsPinned);
        Assert.False(imported.IsDemo);
        Assert.Equal(SavedDevice.UnknownFirmwareVersion, imported.FirmwareVersion);
        Assert.Equal(new BlinkEffectParameters(1_000), imported.BlinkEffectParameters);
        Assert.Equal(new BreatheEffectParameters(1_000), imported.BreatheEffectParameters);
        Assert.NotNull(imported.CustomPresets);
        Assert.Empty(imported.CustomPresets);
        Assert.Equal(60, imported.TimerSeconds);
        var reloaded = new DeviceRepository();
        Assert.Equal(JsonSerializer.Serialize(imported),
            JsonSerializer.Serialize(reloaded.GetByAddress(source.Address)));
    }

    [Fact]
    public void DuplicateImportKeepsExistingTokenAndLocalConfigurationWithoutAnEvent()
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        var source = CreateFullDevice();
        Assert.True(repository.TryAdd(source.Name, source.Address));
        Assert.True(repository.SetTokenAndCapabilities(source.Address,
            [0xA1, 0xB2, 0xC3, 0xD4], source.Capabilities!.Value));
        Assert.True(repository.SetFirmwareVersion(source.Address, "2.7.3"));
        Assert.True(repository.SetPinned(source.Address, true));
        Assert.True(repository.SetBlinkEffectParameters(source.Address, new(1_250)));
        Assert.True(repository.SetBreatheEffectParameters(source.Address, new(2_400)));
        Assert.True(repository.SetCustomPresets(source.Address, source.CustomPresets!));
        Assert.True(repository.SetTimerSeconds(source.Address, 3_600));
        var existingJson = JsonSerializer.Serialize(repository.GetByAddress(source.Address));
        var eventCount = 0;
        repository.DevicesChanged += (_, _) => eventCount++;
        var replacement = new DeviceShareContent("覆盖名称", source.Address.ToLowerInvariant(),
            "01020304", AgpCapabilities.None);

        Assert.False(repository.TryAddShared(replacement));

        Assert.Equal(0, eventCount);
        Assert.Equal(existingJson, JsonSerializer.Serialize(repository.GetByAddress(source.Address)));
    }

    [Fact]
    public async Task ImportedSharedTokenAuthenticatesWithoutOpeningPairingWindow()
    {
        await using var fixture = new PairingFixture();
        Assert.True(fixture.Repository.SetTokenAndCapabilities(
            fixture.Address, [0xA1, 0xB2, 0xC3, 0xD4], fixture.Ble.Capabilities));
        var code = DeviceShareCode.Encode(fixture.Repository.GetByAddress(fixture.Address)!);
        Assert.True(fixture.Repository.Remove(fixture.Address));
        Assert.True(DeviceShareCode.TryParse(code, out var content));
        Assert.True(fixture.Repository.TryAddShared(content));

        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);

        Assert.Contains(AgpOpcode.AuthToken, fixture.Ble.Requests);
        Assert.DoesNotContain(AgpOpcode.AuthGetToken, fixture.Ble.Requests);
        var authentication = Assert.Single(fixture.Ble.RequestMessages,
            request => request.Opcode == AgpOpcode.AuthToken);
        Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }, authentication.Payload.ToArray());
        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
    }

    private static SavedDevice CreateFullDevice() => new(
        "客厅彩色主灯",
        "AA:BB:CC:DD:EE:01",
        new DateTimeOffset(2026, 10, 2, 12, 34, 56, TimeSpan.FromHours(8)),
        IsPinned: true,
        FirmwareVersion: "2.7.3",
        IsDemo: true,
        Token: "A1B2C3D4",
        BlinkEffectParameters: new(1_250),
        BreatheEffectParameters: new(2_400),
        CustomPresets: Enumerable.Range(0, 6).Select(slot => new SavedControlPreset(
            slot,
            $"自定义预设{slot + 1}",
            slot / 10.0,
            0.72,
            0.35,
            46 + slot,
            "#FFAA55",
            slot % 2 == 0 ? AgpLightMode.Blink : AgpLightMode.Breathe,
            (ushort)(1_000 + slot * 200))).ToArray(),
        TimerSeconds: 3_600,
        Capabilities: AgpCapabilities.SecureControl | AgpCapabilities.OnOff |
                      AgpCapabilities.Hsl | AgpCapabilities.Ctl | AgpCapabilities.Effects |
                      AgpCapabilities.RelativeTimer | AgpCapabilities.Scheduler);

    private static JsonObject CreateShareJson() => JsonNode.Parse(
        DeviceShareCode.Encode(CreateFullDevice())[Prefix.Length..])!.AsObject();
}

