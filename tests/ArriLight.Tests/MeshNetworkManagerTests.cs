using Xunit;

namespace ArriLight.Tests;

public sealed class MeshNetworkManagerTests
{
    [Fact]
    public void PersistsDefaultNetworkNodeAndSequence()
    {
        Preferences.Default.Clear();
        try
        {
            var manager = new MeshNetworkManager();
            var network = manager.DefaultNetwork;
            var node = manager.EnsureNode("AA:BB:CC:DD:EE:01", network, "测试 Mesh");
            manager.UpdateNodeAfterProvision(
                node.BluetoothAddress,
                network,
                Enumerable.Repeat((byte)0x11, 16).ToArray());
            manager.SyncSequenceFrom(network, 7);

            var restored = new MeshNetworkManager();
            var restoredNode = restored.FindNode(node.BluetoothAddress);

            Assert.NotNull(restoredNode);
            Assert.Equal(node.UnicastAddress, restoredNode!.UnicastAddress);
            Assert.Equal("测试 Mesh", restoredNode.Name);
            Assert.Equal(16, restoredNode.DeviceKey.Length);
            Assert.Equal((uint)7, restored.DefaultNetwork.SequenceNumber);
            Assert.True(restored.RestoreConfigFor(node.BluetoothAddress).HasDeviceKey);
        }
        finally
        {
            Preferences.Default.Clear();
        }
    }
}
