using System.Text;
using System.Text.Json;
using System.IO.Compression;

namespace ArriOpto.BLE.Mesh;

/// <summary>
/// Mesh 网络的 JSON 序列化与“分享载荷”编解码。
/// 持久化使用 v2 多网络格式；分享载荷 v2 只包含网络身份与地址分配游标
/// （不含设备记录与子分组），让另一台手机能把新设备配进同一个网络。
/// </summary>
public static class MeshNetworkSerializer
{
    public const string SharePayloadV2Prefix = "arri-mesh:v2:";
    public const string LegacySharePayloadPrefix = "arri-mesh:v1:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>把多个网络序列化为 v2 持久化格式（{ version, networks: [...] }）。</summary>
    public static string ToNetworksJson(IReadOnlyList<MeshNetwork> networks) =>
        JsonSerializer.Serialize(
            new NetworksEnvelope { Networks = networks.ToList() },
            JsonOptions);

    /// <summary>解析 v2 多网络持久化格式；失败返回 null。</summary>
    public static bool TryParseNetworks(
        string json,
        out List<MeshNetwork>? networks)
    {
        networks = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var envelope = JsonSerializer.Deserialize<NetworksEnvelope>(
                json,
                JsonOptions);
            if (envelope?.Networks is not { Count: > 0 })
                return false;

            foreach (var network in envelope.Networks)
            {
                if (!IsValidNetwork(network))
                    return false;
                network.EnsureStableId();
                network.EnsureSystemGroups();
            }

            networks = envelope.Networks;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string ToJson(MeshNetwork network) =>
        JsonSerializer.Serialize(network, JsonOptions);

    /// <summary>解析单个网络（旧版单网络持久化与旧分享载荷兼容路径）。</summary>
    public static bool TryParse(string json, out MeshNetwork? network)
    {
        network = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<MeshNetwork>(json, JsonOptions);
            if (!IsValidNetwork(parsed))
                return false;

            parsed!.EnsureStableId();
            parsed.EnsureSystemGroups();
            network = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 生成适合放入二维码的网络分享载荷（v2：JSON 先 GZip 压缩再 Base64）。
    /// 包含网络身份（密钥、IV、TTL）、地址分配游标与设备清单（BLE 地址 +
    /// 单播地址，不含 DeviceKey）。导入方分配自己的 Provisioner 地址和序列号，
    /// 避免两台手机共享 (SRC, SEQ) 触发回放保护冲突；无 DeviceKey 的导入设备
    /// 只能被"仅控制"（应用密钥消息），不能做配置操作。
    /// </summary>
    public static string EncodeSharePayload(MeshNetwork network)
    {
        var content = new NetworkShareV2
        {
            Version = 2,
            Id = network.Id,
            Name = network.Name,
            IsDefault = network.IsDefault,
            NetKey = network.NetKey,
            AppKey = network.AppKey,
            NetKeyIndex = network.NetKeyIndex,
            AppKeyIndex = network.AppKeyIndex,
            IvIndex = network.IvIndex,
            Ttl = network.Ttl,
            NextUnicastAddress = network.NextUnicastAddress,
            NextGroupAddress = network.NextGroupAddress,
            Devices = network.Nodes
                .Where(node => node.UnicastAddress != 0)
                .Select(node => new SharedDeviceInfoV2
                {
                    BluetoothAddress = node.BluetoothAddress,
                    Name = node.Name,
                    UnicastAddress = node.UnicastAddress
                })
                .ToList()
        };
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, JsonOptions));
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(
                   compressed,
                   CompressionLevel.Optimal,
                   leaveOpen: true))
        {
            gzip.Write(json, 0, json.Length);
        }

        return SharePayloadV2Prefix +
               Convert.ToBase64String(compressed.ToArray());
    }

    /// <summary>
    /// 解析分享载荷并构造可导入的网络。v2 只含网络信息：导入方分配自己的
    /// Provisioner 地址（下一个空闲单播地址）与序列号，设备/子分组为空。
    /// 旧 v1 载荷（整网或分组信封）按整网导入，兼容旧版本分享码。
    /// </summary>
    public static bool TryDecodeSharePayload(
        string payload,
        out MeshNetwork? network)
    {
        network = null;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var value = payload.Trim();
        if (value.StartsWith(SharePayloadV2Prefix, StringComparison.Ordinal))
        {
            value = value[SharePayloadV2Prefix.Length..];
            return TryDecodeShareV2(value, out network);
        }

        if (value.StartsWith(LegacySharePayloadPrefix, StringComparison.Ordinal))
            value = value[LegacySharePayloadPrefix.Length..];

        try
        {
            var bytes = Convert.FromBase64String(value);
            var json = TryDecompress(bytes) ?? Encoding.UTF8.GetString(bytes);

            // 新格式：分组分享信封（整网 + 被分享分组 ID）
            try
            {
                var envelope = JsonSerializer.Deserialize<ShareEnvelope>(
                    json,
                    JsonOptions);
                if (envelope?.Network is not null &&
                    !string.IsNullOrWhiteSpace(envelope.SharedGroupId))
                {
                    network = envelope.Network;
                    return PrepareImportedNetwork(network);
                }
            }
            catch (JsonException)
            {
                // 旧格式：直接是整网 JSON，走下面的兼容路径
            }

            return TryParse(json, out network);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>解析 v2 分享载荷并构造网络（不含设备与子分组）。</summary>
    private static bool TryDecodeShareV2(
        string base64,
        out MeshNetwork? network)
    {
        network = null;
        try
        {
            var json = TryDecompress(Convert.FromBase64String(base64))
                ?? throw new InvalidDataException("v2 分享载荷必须是 GZip 压缩 JSON。");
            var share = JsonSerializer.Deserialize<NetworkShareV2>(json, JsonOptions);
            if (share is null || share.Version != 2)
                return false;
            if (share.NetKey.Length != 16 || share.AppKey.Length != 16)
                return false;

            network = new MeshNetwork
            {
                Id = share.Id,
                Name = share.Name,
                IsDefault = false, // 导入的网络不抢占本机默认网络
                NetKey = share.NetKey,
                AppKey = share.AppKey,
                NetKeyIndex = share.NetKeyIndex,
                AppKeyIndex = share.AppKeyIndex,
                IvIndex = share.IvIndex,
                Ttl = share.Ttl,
                NextUnicastAddress = share.NextUnicastAddress,
                // 本机 Provisioner 地址与序列号由导入方在 MeshNetworkManager 中
                // 分配（下一个空闲单播地址，序列号从 0 开始）：不同手机使用
                // 不同 SRC，回放保护按 (SRC, SEQ) 判定，互不冲突。
                NextGroupAddress = share.NextGroupAddress
            };
            foreach (var device in share.Devices)
            {
                if (device.UnicastAddress == 0 ||
                    string.IsNullOrWhiteSpace(device.BluetoothAddress))
                {
                    continue;
                }

                network.Nodes.Add(new MeshNode
                {
                    BluetoothAddress = device.BluetoothAddress
                        .ToUpperInvariant(),
                    Name = device.Name,
                    UnicastAddress = device.UnicastAddress
                    // DeviceKey 为空：仅控制模式，不能发送配置消息
                });
            }
            network.EnsureStableId();
            network.EnsureSystemGroups();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>尝试 GZip 解压；非压缩内容返回 null（兼容旧版未压缩载荷）。</summary>
    private static string? TryDecompress(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>校验网络身份字段是否完整，并补齐系统分组。</summary>
    private static bool IsValidNetwork(MeshNetwork? network)
    {
        if (network is null ||
            network.NetKey.Length != 16 ||
            network.AppKey.Length != 16)
        {
            return false;
        }

        network.EnsureSystemGroups();
        return true;
    }

    /// <summary>导入路径的公共校验与修正（旧 v1 整网/分组信封）。</summary>
    private static bool PrepareImportedNetwork(MeshNetwork network) =>
        IsValidNetwork(network);

    /// <summary>持久化 v2 信封。</summary>
    private sealed class NetworksEnvelope
    {
        public int Version { get; set; } = 2;

        public List<MeshNetwork>? Networks { get; set; }
    }

    /// <summary>网络分享 v2 载荷：只含网络身份与地址分配游标。</summary>
    private sealed class NetworkShareV2
    {
        public int Version { get; set; }

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public bool IsDefault { get; set; }

        public byte[] NetKey { get; set; } = [];

        public byte[] AppKey { get; set; } = [];

        public ushort NetKeyIndex { get; set; }

        public ushort AppKeyIndex { get; set; }

        public byte[] IvIndex { get; set; } = [];

        public byte Ttl { get; set; }

        public ushort NextUnicastAddress { get; set; }

        public ushort NextGroupAddress { get; set; }

        /// <summary>设备清单（BLE 地址 + 单播地址，不含 DeviceKey）。</summary>
        public List<SharedDeviceInfoV2> Devices { get; set; } = [];
    }

    /// <summary>分享载荷中的设备条目（不含 DeviceKey，仅支持应用消息控制）。</summary>
    private sealed class SharedDeviceInfoV2
    {
        public string BluetoothAddress { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public ushort UnicastAddress { get; set; }
    }

    /// <summary>旧 v1 分组分享载荷的信封结构（兼容旧二维码）。</summary>
    private sealed class ShareEnvelope
    {
        public int Version { get; set; }

        public string? SharedGroupId { get; set; }

        public MeshNetwork? Network { get; set; }
    }
}
