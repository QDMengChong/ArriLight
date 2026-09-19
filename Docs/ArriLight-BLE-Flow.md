# ArriLight BLE 流程说明

本文档说明当前 ArriLight APP 的 BLE 扫描、连接、主页状态查询、设备控制和绑定流程。

## 1. 服务边界

ArriLight 只负责 APP 层的页面和调度。BLE 协议实现位于 `ArriOpto.BLE`，PW01 固件不在本项目修改范围内。

主要服务注册在 `MauiProgram.cs`，其中 `BluetoothDiscoveryService`、`BluetoothConnectionService`、`AgpLightControlService` 和 `DeviceRepository` 均为单例。

当前 APP 仍然只维护一条活动 BLE GATT 会话。后台扫描可以与主页的一台设备状态查询同时存在，但不支持多台 PW01 同时建立独立 GATT 控制会话。

## 2. 后台扫描流程

扫描由 `BluetoothDiscoveryService` 统一管理，页面只订阅 `DeviceFound` 事件。

### 主页

1. `MainPage.OnAppearing` 重新加载本地设备列表。
2. 清空本次主页扫描的状态查询队列和已查询集合。
3. 订阅 `BluetoothDiscoveryService.DeviceFound`。
4. 启动或重置 60 秒低延迟、主动匹配 BLE 扫描。
5. 扫描收到广播后更新设备缓存，并向订阅页面发布 `DiscoveredBluetoothDevice`。
6. `MainPage` 只关注“已保存在我的设备列表中、且不是演示设备”的广播设备。

主页离开时取消页面的状态查询、取消事件订阅，并停止服务级扫描。进入添加页时会重新启动并重置 60 秒窗口。

### 添加页面

添加页面进入时启动或重置 60 秒扫描并订阅广播事件。页面离开时只取消本页面的等待，不主动停止服务级扫描，因此其他页面仍可以继续使用扫描结果。

用户选择设备进行添加时，添加页面会显式停止扫描，然后继续执行原有的命名和绑定流程。绑定流程中的物理触摸确认、倒计时和结果提示保持不变。

### 控制页面

控制页面进入时先等待后台扫描停止，再执行连接、能力读取和控制操作，避免后台扫描影响当前设备的 GATT 操作。

## 3. 设备连接流程

设备连接入口主要是 `BluetoothConnectionService.ConnectAsync`，调用链如下：

```text
页面或 AGP 服务
    ↓
BluetoothConnectionService.ConnectAsync
    ↓
BluetoothDiscoveryService.FindDeviceAsync
    ↓
扫描缓存命中，或等待后台扫描收到目标广播
    ↓
Plugin.BLE 连接 IDevice
    ↓
获取 GATT Service/Characteristic
    ↓
AgpGattTransport
    ↓
ArriOpto.BLE.Agp AgpClient
```

如果扫描缓存中已有设备，连接流程不会重新扫描；如果没有缓存，`FindDeviceAsync` 会监听当前后台扫描，等待目标设备广播，不会停止正在运行的全量扫描。

`BluetoothConnectionService` 使用一个连接锁和一个当前设备对象，因此同一时刻只允许一个活动 GATT 设备。切换设备时，需要先清理通知并断开当前设备。

## 4. 主页状态查询流程

主页状态查询由 BLE 广播触发，而不是打开主页后直接遍历所有已保存设备。

1. 扫描收到一个广播。
2. 主页按地址查找本地“我的设备”列表。
3. 未保存设备和演示设备不会进入查询队列。
4. 已保存设备进入待查询队列，并记录 RSSI。
5. 等待约 200 ms 收集同一批广播。
6. 按 RSSI 从强到弱排序，信号强的设备优先查询。
7. 每台设备在本次主页扫描窗口内只查询一次，不再进行第二遍重试。
8. 查询超时时间为 5 秒。

查询调用 `AgpLightControlService.GetOnOffStateWithoutAuthorizationAsync`，流程如下：

```text
获取 AGP 操作锁
    ↓
清理旧的 AGP 会话
    ↓
连接目标 PW01
    ↓
读取公开 Light State 特征
    ↓
解码开关状态
    ↓
更新主页设备状态
    ↓
释放 AGP 会话并断开 BLE
```

主页查询不读取能力位、不获取 Token、不执行 AGP 授权，因此不会弹出绑定确认窗口。

5 秒超时覆盖连接、GATT 服务发现和公开状态读取的整个过程。超时或其他异常会把设备标记为离线；用户操作到来时，后台查询会被取消并让出 AGP 操作锁。

## 5. 设备控制流程

用户从主页进入真实设备控制页时，主页不会等待连接完成，而是立即导航。控制页进入后执行：

1. 停止后台扫描。
2. 连接目标设备。
3. 读取 AGP 能力位。
4. 如果设备支持安全控制，读取本地 Token 并执行认证；没有 Token 时按绑定流程处理。
5. 读取当前 Light State、Color State 或 CTL 范围等控制页所需数据。
6. 根据能力位显示亮度、HSL、CTL 或组合控制界面。

控制指令通过 `AgpLightControlService` 串行发送，例如开关、亮度、HSL、CTL、定时和计划任务。控制页复用当前 AGP 会话，用户离开页面后由连接服务和页面生命周期负责清理。

## 6. 用户操作与后台任务优先级

当前优先级如下：

```text
用户灯控/授权
    >
进入设备控制页
    >
主页单台状态查询
    >
后台 BLE 广播扫描
```

当用户点击开关、进入控制页或离开主页时，主页会取消正在进行的状态查询。控制页会先停止扫描，再执行连接和控制操作。

## 7. 重要限制

- 当前只支持一条活动 GATT 会话，不支持多台 PW01 同时控制。
- BLE 广播只能说明设备可能可达，不能直接表示灯的真实开关状态；真实状态仍需 GATT 读取。
- 扫描缓存是短期连接优化，不等于设备当前一定在线。
- 主页状态查询是一次性查询；设备在本次扫描窗口内再次广播不会重复查询。
- Android 手机和 BLE 芯片对“扫描 + GATT 连接”并行的稳定性可能不同，实际发布前应在目标手机上验证连接超时和 GATT 133 错误。
