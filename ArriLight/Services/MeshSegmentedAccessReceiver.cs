using System.Security.Cryptography;
using ArriOpto.BLE.Mesh;
using ArriOpto.BLE.Mesh.Crypto;

namespace ArriLight;

/// <summary>补齐当前 MeshClient 未接收的下层传输分段，例如 Composition Data Status。</summary>
internal sealed class MeshSegmentedAccessReceiver
{
    private readonly object _sync = new();
    private readonly Dictionary<(ushort Source, ushort Destination, uint Sequence), Assembly> _pending = [];

    public (MeshAccessMessage? Message, byte[] Acknowledgment)? Accept(byte[] proxyPdu, MeshNetworkConfig config)
    {
        if (proxyPdu.Length < 2 || (proxyPdu[0] & 0x3F) != MeshProxyPdu.MessageTypeNetwork ||
            !MeshNetworkLayer.TryDecryptNetworkPdu(proxyPdu.AsSpan(1), config.EncryptionKey,
                config.PrivacyKey, config.Nid, config.IvIndex, out var network) ||
            !IsAccessProxyPdu(proxyPdu, config) || network.Dst != config.ProvisionerAddress)
            return null;
        var transport = network.TransportPdu;
        if (transport.Length < 5 || (transport[0] & 0x80) == 0) return null;
        var seq = ((uint)network.SequenceNumber[0] << 16) | ((uint)network.SequenceNumber[1] << 8) | network.SequenceNumber[2];
        var seqZero = (ushort)(((transport[1] & 0x7F) << 6) | (transport[2] >> 2));
        var seqAuth = (seq & ~0x1FFFu) | seqZero;
        if (seqAuth > seq)
        {
            if (seqAuth < 0x2000) return null;
            seqAuth -= 0x2000;
        }
        var offset = ((transport[2] & 3) << 3) | (transport[3] >> 5);
        var last = transport[3] & 0x1F;
        var aszmic = (transport[1] & 0x80) != 0;
        var appKey = (transport[0] & 0x40) != 0;
        if (offset > last || transport.Length > 16 || (offset != last && transport.Length != 16) ||
            (appKey && (transport[0] & 0x3F) != config.Aid) || (!appKey && !config.HasDeviceKey)) return null;
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _pending.Where(pair => now - pair.Value.CreatedAt > TimeSpan.FromSeconds(10)).Select(pair => pair.Key).ToArray())
                _pending.Remove(expired);
            var key = (network.Src, network.Dst, seqAuth);
            if (!_pending.TryGetValue(key, out var assembly))
            {
                if (_pending.Count >= 8) return null;
                assembly = new Assembly(transport[0], aszmic, last + 1, now);
                _pending[key] = assembly;
            }
            if (assembly.Header != transport[0] || assembly.Aszmic != aszmic || assembly.Parts.Length != last + 1) return null;
            assembly.Parts[offset] ??= transport[4..];
            assembly.Received |= 1u << offset;
            var full = last == 31 ? uint.MaxValue : (1u << (last + 1)) - 1;
            if (assembly.Received != full) return null;
            MeshAccessMessage? message = null;
            if (!assembly.Delivered)
            {
                var upper = assembly.Parts.SelectMany(part => part!).ToArray();
                if (upper.Length <= MeshTransportLayer.GetTransMicLength(aszmic))
                { _pending.Remove(key); return null; }
                byte[] sequence = [(byte)(seqAuth >> 16), (byte)(seqAuth >> 8), (byte)seqAuth];
                var nonce = appKey ? MeshNonce.CreateApplicationNonce(aszmic, sequence, network.Src, network.Dst, config.IvIndex) :
                    MeshNonce.CreateDeviceNonce(aszmic, sequence, network.Src, network.Dst, config.IvIndex);
                try
                {
                    var access = MeshCrypto.DecryptCcm(appKey ? config.AppKey : config.DeviceKey, nonce, upper,
                        micSize: MeshTransportLayer.GetTransMicLength(aszmic));
                    message = new(network.Src, network.Dst, access);
                    assembly.Delivered = true;
                }
                catch (CryptographicException) { _pending.Remove(key); return null; }
            }
            // OBO=0, SeqZero=13 bits, BlockAck=32 bits (network byte order).
            byte[] ack = [0, (byte)(seqZero >> 6), (byte)(seqZero << 2),
                (byte)(full >> 24), (byte)(full >> 16), (byte)(full >> 8), (byte)full];
            var acknowledgment = MeshNetworkLayer.BuildNetworkPdu(config.EncryptionKey, config.PrivacyKey,
                config.Nid, config.IvIndex, 1, config.Ttl, config.NextSequenceNumber(),
                config.ProvisionerAddress, network.Src, ack);
            return (message, MeshProxyPdu.Create(MeshProxyPdu.MessageTypeNetwork, acknowledgment));
        }
    }

    // 协议库的解密结果未保留 CTL；按其 Privacy PECB 规则读回控制位。
    internal static bool IsAccessProxyPdu(byte[] proxyPdu, MeshNetworkConfig config)
    {
        var input = new byte[16];
        config.IvIndex.CopyTo(input, 5);
        proxyPdu.AsSpan(8, 7).CopyTo(input.AsSpan(9));
        var pecb = MeshCrypto.EncryptAesEcbBlock(config.PrivacyKey, input);
        return ((proxyPdu[2] ^ pecb[0]) & 0x80) == 0;
    }

    private sealed class Assembly(byte header, bool aszmic, int count, DateTimeOffset createdAt)
    {
        public byte Header { get; } = header;
        public bool Aszmic { get; } = aszmic;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public byte[]?[] Parts { get; } = new byte[count][];
        public uint Received { get; set; }
        public bool Delivered { get; set; }
    }
}
