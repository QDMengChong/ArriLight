using System.Security.Cryptography;
using ArriOpto.BLE.Mesh;

namespace ArriLight;

/// <summary>
/// 持久化当前应用可使用的 Mesh 网络和节点身份。
/// 本阶段只保留默认网络，分组和分享留给后续网络管理阶段。
/// </summary>
public sealed class MeshNetworkManager
{
    private const string PreferencesKey = "mesh_networks_v2";
    private readonly object _syncRoot = new();
    private List<MeshNetwork> _networks;

    public MeshNetworkManager()
    {
        _networks = LoadOrCreate();
    }

    public event EventHandler? NetworkChanged;

    public IReadOnlyList<MeshNetwork> Networks
    {
        get
        {
            lock (_syncRoot)
                return _networks.ToList();
        }
    }

    public MeshNetwork DefaultNetwork
    {
        get
        {
            lock (_syncRoot)
            {
                return _networks.FirstOrDefault(network => network.IsDefault)
                    ?? throw new InvalidOperationException("Mesh 默认网络不存在。");
            }
        }
    }

    public MeshNode? FindNode(string bluetoothAddress)
    {
        lock (_syncRoot)
        {
            return _networks
                .Select(network => network.FindNode(bluetoothAddress))
                .FirstOrDefault(node => node is not null);
        }
    }

    public MeshNetwork? FindNetworkForNode(string bluetoothAddress)
    {
        lock (_syncRoot)
        {
            return _networks.FirstOrDefault(network =>
                network.FindNode(bluetoothAddress) is not null);
        }
    }

    public MeshNetworkConfig RestoreConfigFor(string bluetoothAddress)
    {
        lock (_syncRoot)
        {
            var network = _networks.FirstOrDefault(item =>
                item.FindNode(bluetoothAddress) is not null)
                ?? throw new InvalidOperationException("设备不在任何 Mesh 网络中。");
            var node = network.FindNode(bluetoothAddress)
                ?? throw new InvalidOperationException("设备不在任何 Mesh 网络中。");
            return MeshNetworkConfig.FromNetwork(network, node);
        }
    }

    public MeshNode EnsureNode(
        string bluetoothAddress,
        MeshNetwork network,
        string name = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bluetoothAddress);
        ArgumentNullException.ThrowIfNull(network);

        MeshNode node;
        var changed = false;
        lock (_syncRoot)
        {
            var managedNetwork = _networks.FirstOrDefault(item =>
                string.Equals(item.Id, network.Id, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Mesh 网络不存在。");
            node = managedNetwork.FindNode(bluetoothAddress)
                ?? new MeshNode
                {
                    BluetoothAddress = bluetoothAddress.Trim().ToUpperInvariant(),
                    Name = name,
                    UnicastAddress = managedNetwork.AllocateUnicastAddress()
                };
            if (!managedNetwork.Nodes.Contains(node))
            {
                managedNetwork.Nodes.Add(node);
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(name) && node.Name != name)
            {
                node.Name = name.Trim();
                changed = true;
            }

            if (changed)
                SaveUnsafe();
        }

        if (changed)
            NetworkChanged?.Invoke(this, EventArgs.Empty);
        return node;
    }

    public void UpdateNodeAfterProvision(
        string bluetoothAddress,
        MeshNetwork network,
        byte[] deviceKey,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(deviceKey);
        if (deviceKey.Length != 16)
            throw new ArgumentException("Mesh DeviceKey 必须为 16 字节。", nameof(deviceKey));

        lock (_syncRoot)
        {
            var node = network.FindNode(bluetoothAddress)
                ?? throw new InvalidOperationException("Mesh 节点不存在。");
            node.DeviceKey = deviceKey.ToArray();
            if (!string.IsNullOrWhiteSpace(name))
                node.Name = name.Trim();
            SaveUnsafe();
        }

        NetworkChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetNodeName(string bluetoothAddress, string name)
    {
        var normalized = name.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        lock (_syncRoot)
        {
            var node = FindNodeUnsafe(bluetoothAddress)
                ?? throw new InvalidOperationException("Mesh 节点不存在。");
            node.Name = normalized;
            SaveUnsafe();
        }

        NetworkChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SyncSequenceFrom(MeshNetwork network, uint sequenceNumber)
    {
        lock (_syncRoot)
        {
            if (sequenceNumber <= network.SequenceNumber)
                return;
            network.SequenceNumber = sequenceNumber;
            SaveUnsafe();
        }
    }

    private MeshNode? FindNodeUnsafe(string bluetoothAddress) =>
        _networks
            .Select(network => network.FindNode(bluetoothAddress))
            .FirstOrDefault(node => node is not null);

    private List<MeshNetwork> LoadOrCreate()
    {
        var json = Preferences.Default.Get(PreferencesKey, string.Empty);
        if (MeshNetworkSerializer.TryParseNetworks(json, out var loaded) &&
            loaded is { Count: > 0 })
        {
            var defaultNetwork = loaded.FirstOrDefault(network => network.IsDefault)
                ?? loaded[0];
            foreach (var network in loaded)
                network.IsDefault = ReferenceEquals(network, defaultNetwork);
            return loaded;
        }

        var createdNetwork = new MeshNetwork
        {
            Name = "默认网络",
            IsDefault = true,
            NetKey = RandomNumberGenerator.GetBytes(16),
            AppKey = RandomNumberGenerator.GetBytes(16)
        };
        createdNetwork.EnsureStableId();
        createdNetwork.EnsureSystemGroups();
        var networks = new List<MeshNetwork> { createdNetwork };
        Preferences.Default.Set(PreferencesKey, MeshNetworkSerializer.ToNetworksJson(networks));
        return networks;
    }

    private void SaveUnsafe() =>
        Preferences.Default.Set(
            PreferencesKey,
            MeshNetworkSerializer.ToNetworksJson(_networks));
}
