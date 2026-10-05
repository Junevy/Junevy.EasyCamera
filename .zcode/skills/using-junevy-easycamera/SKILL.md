---
name: using-junevy-easycamera
description: Use when a .NET project integrates the Junevy.EasyCamera NuGet package to operate industrial cameras (HikVision/海康, future Basler/IRayple) — writing DI registration, enumerate/open cameras, subscribing frame streams, frame 释放/泄漏 questions, BadImageFormatException (x64), trigger configuration, or camera close/reopen flows. Use when 代码需要订阅工业相机帧流、处理 IFrame 生命周期、或排除相机接入故障。
---

# Using Junevy.EasyCamera

## Overview

Junevy.EasyCamera 是工业相机统一操作类库：一套抽象封装多品牌 SDK（当前海康可用；IRayple/Basler 预留，启用即抛 `NotImplementedException`），通过 `ICameraService` 门面完成枚举、开关、帧流订阅与参数访问。

**硬性环境**：net48 或 net8.0；仅 Windows；运行时必须 **x64**（厂商 SDK 为 AMD64 专用，AnyCPU/x86 会抛 `BadImageFormatException`）；需安装厂商相机运行时。安装：nuget.org 的 `Junevy.EasyCamera`（如未发布则用仓库构建产出的本地 .nupkg），Core 契约包为 `Junevy.EasyCamera.Core`。

**命名空间与关键类型**：扩展注册在 `Junevy.EasyCamera.Extensions`（`AddEasyCamera`，配置委托为 `Action<CameraOptions>` 与 `Action<StreamOptions>`）；契约在 `Junevy.EasyCamera.Core.Abstractions`（`ICameraService`、`ICameraSdkSystem`、`ICameraInfo`、`IFrame`、`CameraInterfaceType`、`CameraResult`）；手动构造的具体类在 `Junevy.EasyCamera.Common`（`CameraService`/`CameraManager`/`StreamManager`/`StreamOptions`/`AggregateCameraProvider`）；海康提供器在 `Junevy.EasyCamera.Vendors.HikVision`（`HikCameraProvider`）。`CameraInterfaceType` 成员：`GigE, Usb, GenTL, CameraLink, All, Unknown`。启用至少一个厂商时 DI 会注册 `ICameraSdkSystem` 单例（组合各厂商 SDK）；核心服务（`ICameraService` 等）均为单例注册，可安全注入 HostedService；`AddEasyCamera` 可重复调用不重复注册。

## 快速开始（DI，一次读完即可上手）

```csharp
// ── 注册（Program.cs）────────────────────────────────────────────
services.AddEasyCamera(
    options => options.EnableHikVision = true,
    stream   => stream.StreamCapacity = 5);      // 每订阅者的有界帧缓存（帧数）

// ── 生命周期（建议 IHostedService）──────────────────────────────
sdk.Initialize();                                 // ICameraSdkSystem，进程级一次；按实例幂等
var info = cameras.EnumerateCameras().FirstOrDefault();  // 单方法带默认参 All；无相机=空序列（枚举失败有 Trace 警告）
cameras.OpenCamera(info, "cam-1");                // 相机与帧流都以 cameraKey 注册
cameras.SubscribeFrameStream("cam-1", "sub-ui", OnFrameAsync,
    whenException: ex => log.LogError(ex, "..."));
cameras.StartGrab("cam-1");                       // 幂等
// ……运行期参数/触发……
cameras.StopGrab("cam-1");                        // 幂等
cameras.SetTrigger("cam-1", "Line1", enableTrigger: true);  // 内部会停流，之后必须重新 StartGrab
cameras.StartGrab("cam-1");
// ── 关停（顺序：停流 → 退订 → 关相机 → 释放 SDK）────────────────
cameras.UnsubscribeFrameStream("cam-1", "sub-ui");
cameras.Close("cam-1");                           // 释放相机；帧流保留（重开同名 key 后订阅自动生效）
sdk.Dispose();                                    // 释放本实例引用；引用归零才真正 Finalize
```

```csharp
// 帧回调签名（注意双参数，第一参是 cameraKey）：
private Task OnFrameAsync(string cameraKey, IFrame frame)
{
    var bitmap = frame.GetBitmap();   // 独立位图副本，帧释放后仍有效
    frame.Dispose();                  // 本帧引用在 handler 内恰好 Dispose 一次
    if (bitmap != null) postToUi(bitmap);   // UI 用完自行 Dispose 位图
    return Task.CompletedTask;
}
```

手动构造（不用 DI）：`new CameraService(new AggregateCameraProvider(new IVendorCameraProvider[]{ new HikCameraProvider() }), new CameraManager(), new StreamManager(), new StreamOptions())`。

## 非 DI 场景 / Prism 等自带容器框架

类库对容器零依赖。非 Microsoft.DI 场景用 **Builder 一行式入口**（`Junevy.EasyCamera` 命名空间）：

```csharp
// 组装（构造不做原生调用；每次 Create 都是独立的相机栈）
var host = EasyCamera.Create(b => b.EnableHikVision()
                                     .WithStreamOptions(o => o.StreamCapacity = 5));
host.Sdk.Initialize();                    // 启动 SDK（与 DI 路径相同的显式初始化）
// ……host.Service 用法与 DI 场景完全一致……
host.Dispose();                           // 幂等；释放顺序：相机 → 帧流 → SDK
```

**Prism 接入**（WPF/Forms/Maui 通用，DryIoc/Unity 均适用）——把 host 的组件以**单例实例**注册进宿主容器，之后 ViewModel 构造函数注入 `ICameraService` 照常工作：

```csharp
public partial class App : PrismApplication
{
    private EasyCameraHost cameraHost;

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        this.cameraHost = EasyCamera.Create(b => b.EnableHikVision());
        this.cameraHost.Sdk.Initialize();

        containerRegistry.RegisterInstance<ICameraSdkSystem>(this.cameraHost.Sdk);
        containerRegistry.RegisterInstance<ICameraService>(this.cameraHost.Service);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        this.cameraHost.Dispose();
        base.OnExit(e);
    }
}
```

要点：**必须 `RegisterInstance`（单例），不要注册成瞬态**——`CameraService` 背后共享 `CameraManager`/`StreamManager` 两张注册表，瞬态会出现两套互不相通的相机缓存与订阅流。若你的 Prism 底层就是 Microsoft.Extensions.DependencyInjection（官方适配包），则可直接 `services.AddEasyCamera(...)`，无需 Builder。

高级场景仍可完全手动构造：`new CameraService(new AggregateCameraProvider(new IVendorCameraProvider[]{ new HikCameraProvider(streamOptions) }), new CameraManager(), new StreamManager(), streamOptions)`（注意 `HikCameraProvider` 需拿到同一份 `StreamOptions` 才能应用 `CameraBufferCapacity`）。

## 帧生命周期（最易出错的裁决点，逐条遵守）

1. **每收到一帧必须恰好 `Dispose()` 一次**。帧是引用计数对象：流持有发布方初始引用，每投递一个订阅者 +1；引用归零时由最后一次 Dispose 释放非托管缓冲（约 5MB+/帧）。
2. **帧要在 handler 返回后继续使用 → 先 `frame.AddRef()`**（必须在 handler 内完成），用完后对应 `Dispose()`。不做 AddRef 而把帧带出 handler = use-after-free。
3. 只读消费帧数据用 `frame.Data`（懒缓存托管副本，帧释放后仍可安全读取）。`frame.PixelDataPtr` 在帧释放后立即失效，禁止保存或跨 handler 使用。
4. `frame.GetBitmap()` 返回**独立的 GDI+ 位图副本**（不随帧释放失效），由调用方 Dispose；帧已释放时返回 null。只要位图不要帧时可在 handler 内取 Bitmap、随即 Dispose 帧。
5. 有界队列 **DropOldest**：handler 处理慢只丢帧、不阻塞采集；被淘汰的帧由流立即释放。handler 内不要做重活，重活配合 AddRef 移交出去。
6. 同 `subKey` 重复订阅 = **原子替换旧订阅者**；不提供 `whenException` 时，handler 异常会**终止并自动摘除该订阅**（静默失效）——生产代码始终传 `whenException`。
7. 发布方不得修改帧数据（订阅者是只读消费者）。

## Quick Reference — ICameraService

| 操作 | 调用 | 要点 |
|---|---|---|
| 枚举 | `EnumerateCameras(CameraInterfaceType type = All)` | 失败=空序列 |
| 打开 | `CameraResult OpenCamera(ICameraInfo info, string cameraKey)` | 同 key 已连接时返回失败"has been opened"，不是幂等 |
| 订阅 | `bool SubscribeFrameStream(string cameraKey, string subKey, Func<string, IFrame, Task> processFrame, Action<Exception> whenException = null, int capacity = 0)` | capacity≤0 用 StreamCapacity；handler 双参数（cameraKey, frame） |
| 退订 | `bool UnsubscribeFrameStream(string cameraKey, string subKey)` | 帧由流负责释放；消费方 AddRef 的引用自管 |
| 取流 | `CameraResult StartGrab(key)` / `CameraResult StopGrab(key)` | 均幂等 |
| 触发 | `CameraResult SetTrigger(key, string triggerSource, bool enableTrigger)` | GenICam 名 `TriggerMode`/`TriggerSource`；改完须重新 StartGrab；关闭触发时 triggerSource 仍须传有效符号名 |
| 参数 | `bool TryGetParam<T>(key, name, out v)` / `CameraResult SetParam(key, name, v)`（int/float/bool/string 重载） | Try 版可区分"值恰为 default"与"取参失败" |
| 读回触发 | `string GetEnumParam(key, "TriggerMode")` → `"On"/"Off"` | 失败或相机不可用返回空字符串；`TriggerSource` → 符号名如 `"Line1"` |
| 关闭 | `CameraResult Close(key)` | 失败原因经 Message 区分 NotFound / ReleaseFailed；帧流不删 |
| 序列号 | `string GetSerialNumber(key)` | 已注册相机的序列号，与"在线"无关 |

`CameraResult`：成员 `IsSuccess`/`Code`/`Message`；只用 `IsSuccess` 判成败，`Code` 是厂商原生错误码（成功恒 0），`Message` 为可读诊断。

## Common Mistakes

- 忘记 `frame.Dispose()`（或 AddRef 后不配对释放）→ 每帧 5MB+ 非托管内存泄漏。
- `SetTrigger` 后不重新 `StartGrab` → 相机停在那里不出帧。
- 只注入 `ICameraService` 却从未调用 `sdk.Initialize()` → SDK 未初始化，枚举不到设备。
- 进程跑成 x86/AnyCPU → `BadImageFormatException`。
- Prism 等宿主容器里把 `ICameraService` 注册成瞬态 → 两套互不相通的相机栈；必须 `RegisterInstance` 单例。
- 对同一 cameraKey 重复 `OpenCamera` 期望幂等 → 返回失败；先 `Close` 或复用已有连接。
- 帧不来了先查三件事：是否 `StartGrab`？cameraKey 是否拼错？订阅是否因 handler 异常且无 `whenException` 被自动摘除？
- 用 `GetParam<T>` 的 default 判断失败 → 改用 `TryGetParam<T>`。
- 关闭顺序颠倒（先 Dispose SDK 再 StopGrab）→ 按"停流→退订→Close→sdk.Dispose"收尾；消费方自己 AddRef 的帧由消费方负责释放，与 Close 无关。

## 故障速查

| 症状 | 原因/处理 |
|---|---|
| `BadImageFormatException` | 宿主非 x64，改 PlatformTarget=x64 |
| 枚举为空 | 相机未上电/网段不通/GigE 带宽防火墙；看 Trace 警告里的厂商错误码 |
| 收不到帧 | 未 StartGrab；cameraKey 不匹配；订阅被异常摘除；触发模式下无触发信号 |
| `OpenCamera` 报 "has been opened" | 该 key 已有连接，直接复用或先 Close |
| 内存持续增长 | 检查帧 Dispose/AddRef 配对；检查是否向死订阅者通道发帧（已由库自愈，但仍需 whenException 感知） |
