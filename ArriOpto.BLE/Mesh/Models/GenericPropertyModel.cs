using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Generic Property model messages (Mesh Model Specification 1.1, Section 3.2.8).
/// </summary>
public static class GenericPropertyModel
{
    // ---- User Properties ----
    /// <summary>构建 Generic User Properties Get 消息（列出全部用户属性 ID）。</summary>
    public static byte[] UserPropertiesGet() =>
        MeshMessageWriter.Create(MeshOpcode.GenericUserPropertiesGet).ToArray();

    /// <summary>
    /// 构建 Generic User Properties Status 消息。
    /// 载荷：User Property IDs（2*N 字节，每个 ID 小端）。
    /// </summary>
    public static byte[] UserPropertiesStatus(IReadOnlyCollection<ushort> propertyIds) =>
        BuildPropertyIdList(MeshOpcode.GenericUserPropertiesStatus, propertyIds);

    /// <summary>构建 Generic User Property Get 消息（查询单个属性）。</summary>
    public static byte[] UserPropertyGet(ushort propertyId) =>
        MeshMessageWriter.Create(MeshOpcode.GenericUserPropertyGet)
            .AddU16(propertyId)
            .ToArray();

    /// <summary>
    /// 构建 Generic User Property Set（或 Set Unacknowledged）消息。
    /// 载荷：Property ID(2) + Property Value(变长)。
    /// </summary>
    public static byte[] UserPropertySet(
        ushort propertyId,
        ReadOnlySpan<byte> value,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.GenericUserPropertySetUnack
                    : MeshOpcode.GenericUserPropertySet)
            .AddU16(propertyId)
            .AddBytes(value)
            .ToArray();

    /// <summary>
    /// Parses a Generic User Property Status message.
    /// Payload: Property ID(2) + [User Access(1) + Property Value].
    /// </summary>
    public static bool TryParseUserPropertyStatus(
        ReadOnlySpan<byte> message,
        out GenericPropertyStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.GenericUserPropertyStatus) ||
            reader.Remaining < 2)
        {
            return false;
        }

        return TryParsePropertyStatus(reader, out status);
    }

    // ---- Admin Properties ----
    /// <summary>构建 Generic Admin Properties Get 消息。</summary>
    public static byte[] AdminPropertiesGet() =>
        MeshMessageWriter.Create(MeshOpcode.GenericAdminPropertiesGet).ToArray();

    /// <summary>构建 Generic Admin Properties Status 消息（ID 列表，2*N 字节）。</summary>
    public static byte[] AdminPropertiesStatus(IReadOnlyCollection<ushort> propertyIds) =>
        BuildPropertyIdList(MeshOpcode.GenericAdminPropertiesStatus, propertyIds);

    /// <summary>构建 Generic Admin Property Get 消息。</summary>
    public static byte[] AdminPropertyGet(ushort propertyId) =>
        MeshMessageWriter.Create(MeshOpcode.GenericAdminPropertyGet)
            .AddU16(propertyId)
            .ToArray();

    /// <summary>
    /// 构建 Generic Admin Property Set（或 Set Unacknowledged）消息。
    /// 载荷：Property ID(2) + Admin User Access(1) + Property Value(变长)。
    /// </summary>
    public static byte[] AdminPropertySet(
        ushort propertyId,
        byte userAccess,
        ReadOnlySpan<byte> value,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.GenericAdminPropertySetUnack
                    : MeshOpcode.GenericAdminPropertySet)
            .AddU16(propertyId)
            .AddU8(userAccess)
            .AddBytes(value)
            .ToArray();

    public static bool TryParseAdminPropertyStatus(
        ReadOnlySpan<byte> message,
        out GenericPropertyStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.GenericAdminPropertyStatus) ||
            reader.Remaining < 2)
        {
            return false;
        }

        return TryParsePropertyStatus(reader, out status);
    }

    // ---- Manufacturer Properties ----
    /// <summary>构建 Generic Manufacturer Properties Get 消息。</summary>
    public static byte[] ManufacturerPropertiesGet() =>
        MeshMessageWriter.Create(MeshOpcode.GenericManufacturerPropertiesGet).ToArray();

    /// <summary>构建 Generic Manufacturer Properties Status 消息（ID 列表）。</summary>
    public static byte[] ManufacturerPropertiesStatus(IReadOnlyCollection<ushort> propertyIds) =>
        BuildPropertyIdList(MeshOpcode.GenericManufacturerPropertiesStatus, propertyIds);

    /// <summary>构建 Generic Manufacturer Property Get 消息。</summary>
    public static byte[] ManufacturerPropertyGet(ushort propertyId) =>
        MeshMessageWriter.Create(MeshOpcode.GenericManufacturerPropertyGet)
            .AddU16(propertyId)
            .ToArray();

    /// <summary>Manufacturer Property Set carries Property ID(2) + User Access(1).</summary>
    public static byte[] ManufacturerPropertySet(
        ushort propertyId,
        byte userAccess,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.GenericManufacturerPropertySetUnack
                    : MeshOpcode.GenericManufacturerPropertySet)
            .AddU16(propertyId)
            .AddU8(userAccess)
            .ToArray();

    public static bool TryParseManufacturerPropertyStatus(
        ReadOnlySpan<byte> message,
        out GenericPropertyStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.GenericManufacturerPropertyStatus) ||
            reader.Remaining < 2)
        {
            return false;
        }

        return TryParsePropertyStatus(reader, out status);
    }

    // ---- Client Properties ----
    /// <summary>Client Properties Get carries a Client Property ID(2).</summary>
    public static byte[] ClientPropertiesGet(ushort clientPropertyId) =>
        MeshMessageWriter.Create(MeshOpcode.GenericClientPropertiesGet)
            .AddU16(clientPropertyId)
            .ToArray();

    /// <summary>构建 Generic Client Properties Status 消息（ID 列表）。</summary>
    public static byte[] ClientPropertiesStatus(IReadOnlyCollection<ushort> propertyIds) =>
        BuildPropertyIdList(MeshOpcode.GenericClientPropertiesStatus, propertyIds);

    public static bool TryParseClientPropertiesStatus(
        ReadOnlySpan<byte> message,
        out IReadOnlyList<ushort> propertyIds)
    {
        propertyIds = [];
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.GenericClientPropertiesStatus) ||
            reader.Remaining % 2 != 0)
        {
            return false;
        }

        var ids = new List<ushort>(reader.Remaining / 2);
        while (reader.HasRemaining)
            ids.Add(reader.ReadU16());
        propertyIds = ids;
        return true;
    }

    private static byte[] BuildPropertyIdList(
        byte opcode,
        IReadOnlyCollection<ushort> propertyIds)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var writer = MeshMessageWriter.Create(opcode);
        foreach (var id in propertyIds)
            writer.AddU16(id);
        return writer.ToArray();
    }

    private static bool TryParsePropertyStatus(
        MeshMessageReader reader,
        out GenericPropertyStatus status)
    {
        var propertyId = reader.ReadU16();
        if (reader.Remaining == 0)
        {
            status = new GenericPropertyStatus(propertyId, null, []);
            return true;
        }

        if (reader.Remaining < 1)
        {
            status = default;
            return false;
        }

        var access = reader.ReadU8();
        var value = reader.ReadRemaining();
        status = new GenericPropertyStatus(propertyId, access, value);
        return true;
    }
}

/// <summary>Property ID with optional User Access and raw Property Value.</summary>
public readonly record struct GenericPropertyStatus(
    ushort PropertyId,
    byte? UserAccess,
    byte[] PropertyValue);
