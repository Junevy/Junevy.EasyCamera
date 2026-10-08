# Junevy.EasyCamera

工业相机统一操作类库：以一套抽象封装不同品牌工业相机 SDK，便于第三方项目快速接入相机能力、通过接口无缝更换相机硬件、通过 Channel 管理相机帧流。

## 当前状况

| 品牌 | 状态 | 说明 |
|---|---|---|
| 海康 HikVision | ✅ 可用 | 基于 `MvCameraControl.Net`，含完整生命周期/并发回归测试 |
| 华睿 IRayple | 🚧 未完成 | 类型标记 `[Obsolete("未开发完毕", true)]`，DI 启用即抛 `NotImplementedException` |
| Basler | 📋 预留 | DI 启用即抛 `NotImplementedException`，待接入 |
| Cognex | 📋 规划中 | 预留扩展点 |

- 双目标框架：`net48` + `net8.0`（net8.0 下声明 Windows 平台）；运行时须 **x64**（厂商 SDK 为 AMD64 专用）。消费方需安装厂商运行时，详见「运行与安装前置条件」。
- 解决方案结构：`Junevy.EasyCamera.Core`（抽象与契约）/ `Junevy.EasyCamera`（DI 与厂商适配）/ `Junevy.EasyCamera.Tests`（x64 测试宿主）。
- 厂商 SDK DLL 位于仓库 `Libs/`，构建自包含，并随 NuGet 包分发给消费方（见 1.1.1 变更）；.NET SDK 版本由 `global.json`（9.0.316）固定，包版本由 `Directory.Packages.props` 中央管理。
- 版本 1.1.0 起：厂商独有能力（可达性探测 / 采集缓冲配置 / 设备改名）以**能力接口**表达（`ILinkStatusProbeProvider`、`IBufferConfigurable`、`INamedCameraInfo`），新增能力不再破坏既有实现者。
- 详见 [AGENTS.md](AGENTS.md)（项目开发约定）与 [docs/](docs/)（审查/计划文档）。

## 运行与安装前置条件

1. **运行时 x64**：`net48` 与 `net8.0` 均已固定 `PlatformTarget=x64`（海康/华睿 SDK 为 AMD64 专用）。宿主若按 AnyCPU/x86 发布，会抛 `BadImageFormatException`。
2. **必须安装厂商相机运行时（MVS 等）**：厂商的**原生**驱动 DLL（如 `MvCameraControl.dll`）不在 NuGet 包内，由厂商安装程序部署，并需其 `Runtime\Win64_x64` 在 PATH（安装程序通常自动配置）。缺它时报错与"托管封装缺失"相似但成因不同，见下方排错。
3. **厂商托管封装随包分发（1.1.1 起）**：`MvCameraControl.Net.dll`、`MVSDK_Net.dll` 已打进 `lib/net48` 与 `lib/net8.0`，消费方只需正常 `Install-Package`/`PackageReference`，**不需要**手工拷贝 DLL。
   - ⚠️ 不要手工把 DLL 拷到 exe 旁边：对 .NET Core **无效**（程序集解析走 `deps.json` 的 TPA 列表，默认加载器不探测 exe 目录）。这曾导致"拷了还是崩"的误判。

**排错对照表**

| 现象 | 原因 | 处理 |
|---|---|---|
| `FileNotFoundException: Could not load file or assembly 'MvCameraControl.Net'` | 包内缺厂商托管封装（≤1.1.0 的已知缺陷） | 升级到 1.1.1，并清理旧版本 NuGet 缓存（NuGet 按 id+version 缓存，同版本重发不生效） |
| `BadImageFormatException` | 宿主非 x64 | 改 `PlatformTarget=x64` |
| `Initialize` 抛 `DllNotFoundException` / 枚举不到设备，但托管封装已在 | 厂商原生运行时未安装或不在 PATH | 安装 MVS 运行时，确认 `...\Common Files\MVS\Runtime\Win64_x64` 在 PATH |

## 构建

```powershell
dotnet build .\Junevy.EasyCamera\Junevy.EasyCamera.csproj -c Release -v:minimal
dotnet test .\Junevy.EasyCamera.Tests\Junevy.EasyCamera.Tests.csproj -c Release --logger "console;verbosity=minimal"
```

## 推荐使用方式（依赖注入）

```csharp
// 1. 注册（Host/ServiceProvider 场景）
services.AddEasyCamera(
    options => options.EnableHikVision = true,
    stream =>
    {
        stream.StreamCapacity = 5;                        // 每个订阅者的有界帧缓存（帧数）
        stream.BackpressureMode = BackpressureMode.DropOldest; // 队列满时丢最旧（保留最新画面）
        // stream.BackpressureMode = BackpressureMode.RejectNewest; // 或：拒收新帧、按序处理已入队帧
        stream.CameraBufferCapacity = 10;                 // 采集端缓冲（仅部分厂商支持）
    });

// 2. 解析并初始化 SDK（进程级一次；内部按实例引用计数，Finalize 在引用归零时执行）
var sdk = provider.GetRequiredService<ICameraSdkSystem>();
sdk.Initialize();
try
{
    var cameraService = provider.GetRequiredService<ICameraService>();

    // 3. 枚举并打开相机
    var info = cameraService.EnumerateCameras().First();
    cameraService.OpenCamera(info, cameraKey: "cam-1");

    // 4. 订阅帧流（capacity <= 0 时使用 StreamOptions.StreamCapacity）
    cameraService.SubscribeFrameStream("cam-1", "my-subscriber",
        (cameraKey, frame) =>
        {
            using (frame)                       // 帧必须 Dispose：引用计数归零即释放非托管缓冲
            {
                var data = frame.Data;          // 托管副本（安全）
                // 处理图像……需要跨线程保帧时先 frame.AddRef()，用完对应 Dispose()
            }
            return Task.CompletedTask;
        },
        whenException: ex => { /* 不提供时 handler 异常将终止该订阅并自动摘除 */ });

    // 5. 开始/停止取流、参数与触发
    cameraService.StartGrab("cam-1");
    cameraService.SetParam("cam-1", "ExposureTime", 5000f);

    // 取参推荐 TryGetParam：可区分"值恰为 default"与"获取失败"
    if (cameraService.TryGetParam<int>("cam-1", "Width", out var width)) { /* ... */ }

    cameraService.StopGrab("cam-1");
    cameraService.SetTrigger("cam-1", "Line1", enableTrigger: true); // 设置后需重新 StartGrab
    cameraService.StartGrab("cam-1");

    // 6. 排查丢帧：统计随流累计，Dropped 持续增长说明消费慢于采集
    var stats = cameraService.GetStreamStatistics("cam-1");   // Published / Delivered / Dropped

    // 7. 关闭相机（帧流保留，重开同名 key 后订阅自动继续生效）
    cameraService.Close("cam-1");
}
finally
{
    sdk.Dispose();
}
```

### 掉线处理（1.2.0+）

拔线或断网后，相机保持注册但不可用，`CameraDisconnected` 事件通知掉线（线程池线程，同一次连接最多一次）。库不做自动重连，由调用方决定何时重连：

```csharp
cameraService.CameraDisconnected += (_, e) =>
{
    // e.CameraKey 为打开时使用的 key；此处在线程池线程上，更新界面需自行封送
    Log.Warning("相机 {Key} 掉线：{Reason}", e.CameraKey, e.Reason);
};

// 重连：对同一 key 再次 OpenCamera（旧句柄会被释放并重建），成功后重新 StartGrab；帧流订阅保留
if (cameraService.OpenCamera(info, "cam-1").IsSuccess)
    cameraService.StartGrab("cam-1");
```

设备 IP 或枚举信息已变化时，先 `Close("cam-1")`，再用新的枚举结果调用 `OpenCamera`。

## 简易使用（Builder，不依赖 DI）

```csharp
using Junevy.EasyCamera;

using var host = EasyCamera.Create(b => b.EnableHikVision()
                                             .WithStreamOptions(o => o.StreamCapacity = 5));
host.Sdk.Initialize();
// host.Service 用法与 DI 方式的 3–7 步相同
host.Dispose();   // 幂等；释放顺序：相机 → 帧流 → SDK
```

### Prism 等自带容器框架

把 `host.Sdk` / `host.Service` 以**单例实例**注册进宿主容器（`IContainerRegistry.RegisterInstance`，切勿注册成瞬态），之后 ViewModel 构造函数注入 `ICameraService` 照常工作；完整模式见 [skills/using-junevy-easycamera/SKILL.md](skills/using-junevy-easycamera/SKILL.md)。若你的 Prism 底层本就是 Microsoft.Extensions.DependencyInjection（官方适配包），直接 `services.AddEasyCamera(...)` 即可。

## 帧与资源管理约定（重要）

- **每帧仅在相机回调中克隆一次**：SDK 回调帧在回调内归还采集队列，订阅者拿到的帧是对 SDK 缓冲的独立克隆。
- **分发为引用计数浅共享**：`Publish` 对每个订阅者只做 `AddRef + TryWrite` 同一实例，不逐订阅者深拷贝；订阅者为只读消费者，禁止修改帧数据。
- 帧引用计数初始为 1（发布方初始引用，由流持有并释放），每入队一个订阅者 +1；**引用归零时由最后一个释放者回收非托管缓冲**，且仅回收一次。
- 需要异步保帧：在 handler 返回前调用 `frame.AddRef()`，使用完成后对应 `Dispose()`。
- 优先使用 `frame.Data`（懒缓存托管副本，已释放帧仍可安全读取）；`frame.PixelDataPtr` 在帧释放后返回 `IntPtr.Zero`，禁止保存或跨 handler 使用。
- 帧的宽高/步长/像素格式在构造时快照，读取不再触碰原生内存；`Data`/`GetBitmap` 与引用计数互斥，帧释放后不会读到已释放的原生内存。
- 订阅队列为有界队列，**背压策略由 `StreamOptions.BackpressureMode` 决定**：`DropOldest`（默认，丢最旧、保留最新画面）与 `RejectNewest`（队列满时拒收新帧、按序处理已入队帧）。两者都不阻塞采集线程。
- 同 Key 重复订阅会**原子替换**旧订阅者；未提供 `whenException` 时，handler 异常会终止该订阅并自动摘除（帧仍会释放）。
- 丢帧不再静默：`ICameraService.GetStreamStatistics(cameraKey)` 返回累计 `Published/Delivered/Dropped`。

## 相机链路状态（连接对话框/状态徽章用）

`ICameraService.ProbeCameraLinkStatus(info, ct)` 返回五态（`Junevy.EasyCamera.Core.Abstractions.CameraLinkStatus`）：

| 状态 | 含义 |
|---|---|
| `Unknown`（默认，=0） | 未能判定：未探测、已取消，或缺少可用的探测手段 |
| `Connected` | 本进程已持有该相机连接（任意 cameraKey 注册且序列号匹配） |
| `Idle` | 在线、未被本进程持有且可达——当前可以打开 |
| `Occupied` | 在线但不可达：被其它客户端独占 |
| `Unreachable` | 重新枚举中不存在：掉线、被拔出、未上电 |

- 非侵入优先：海康走 `DeviceEnumerator.IsDeviceAccessible`（独占模式，与打开权限一致）；厂商不支持时回退侵入式 `probe:{serial}` 开关探测，探测用的相机连接与帧流都不留在注册表中。
- 探测会触发厂商枚举（已按 `info.InterfaceType` 缩小范围），**应在后台线程调用**。
- 本进程独占持有的相机会令可达性查询返回 false，因此库内先查连接注册表再问厂商。

## 路线图

- Basler（Pylon）适配、IRayple 补齐（同步模型、回调排空、帧引用防护）
- Cognex 适配调研
- 线阵相机扩展接口 `ILineScanCamera` 落地（行触发、行计数）
- 帧托管副本改用 `ArrayPool<byte>.Shared` 租用，降低 LOH 压力（需先确定 `IFrame.Data` 的长度契约）
