# ICamera与ICameraStream契约

<cite>
**本文引用的文件**
- [Junevy.EasyCamera.Core/Abstractions/ICamera.cs](file://Junevy.EasyCamera.Core/Abstractions/ICamera.cs)
- [Junevy.EasyCamera.Core/Abstractions/ICameraStream.cs](file://Junevy.EasyCamera.Core/Abstractions/ICameraStream.cs)
- [Junevy.EasyCamera.Core/Abstractions/IFrame.cs](file://Junevy.EasyCamera.Core/Abstractions/IFrame.cs)
- [Junevy.EasyCamera.Core/Abstractions/ILineScanCamera.cs](file://Junevy.EasyCamera.Core/Abstractions/ILineScanCamera.cs)
</cite>

## 目录
1. [ICamera 成员](#icamera-成员)
2. [ICamera 契约纪律](#icamera-契约纪律)
3. [ICameraStream 成员](#icamerastream-成员)
4. [Publish 的所有权转移](#publish-的所有权转移)
5. [Subscribe 的替换语义](#subscribe-的替换语义)
6. [IFrame 与引用计数](#iframe-与引用计数)
7. [ILineScanCamera 扩展点](#ilinescancamera-扩展点)

## ICamera 成员

`ICamera : IDisposable`，是"一台已打开设备实例"的最小抽象（厂商实现见 `Vendors/HikVision/HikCamera.cs`、`Vendors/IRayple/IRaypleCamera.cs`）。

| 成员 | 说明 |
| --- | --- |
| `bool IsConnected` | 是否已打开（纯状态读取：不读设备、不做原生调用，1.1.2 起；原生连接检查只在门内经 `IsDeviceReady()`） |
| `bool IsGrabbing` | 是否正在取流 |
| `string LastError` | 最近一次无法用返回值表达的生命周期错误（如停止取流失败）；成功完成后清空 |
| `CameraResult Connect()` | 打开 |
| `CameraResult Close()` | 关闭（幂等：已关闭或从未打开的相机返回成功，不做原生调用；1.2.0 验收修正起；掉线后的相机释放句柄并返回成功，关闭期间掉线同样如此） |
| `CameraResult StartGrab()` | 开始取流 |
| `CameraResult StopGrab()` | 停止取流；**1.1.0 起由 `void` 改为返回 `CameraResult`** |
| `string GetSerialNumber()` | 未知时返回空字符串 |
| `CameraResult SetParam(string, int/float/bool/string)` | 四个重载 |
| `CameraResult SetEnumParam(string, string)` | 值为枚举符号名 |
| `T GetParam<T>(string)` | 薄封装，失败返回 `default(T)` |
| `bool TryGetParam<T>(string, out T)` | 可区分"值恰为 default"与"失败" |
| `string GetEnumParam(string)` | 失败返回空串 |
| `bool TryGetEnumParam(string, out string)` | 同上 |
| `CameraResult ExecuteCommand(string)` | 执行命令 |

`StopGrab` 的返回值是 1.1.0 的破坏性变更：`void` 无法表达"native 停流失败"，调用方只能靠 `IsGrabbing` 反推，容易把"仍在取流"误判为"已停止"。

**章节来源**
- [Junevy.EasyCamera.Core/Abstractions/ICamera.cs](file://Junevy.EasyCamera.Core/Abstractions/ICamera.cs)

## ICamera 契约纪律

> [!warning] 失败必须通过 `CameraResult` 表达，**厂商异常不得穿透到调用方**。海康实现在 `ExecuteGuarded` / `TryExecuteGuarded` / `ExecuteDispose` 三个守卫里把 `MvException` 翻译为 `CameraResult.Fail(me.ErrorCode, me.Message)`，其它异常翻译为 `CameraResult.Fail(-1, e.Message)`，并同步写入 `LastError`。

推论（实现方必须遵守）：

- `StopGrab` 幂等，未在取流时返回成功；native 停流失败时必须**保留**取流状态，不允许把状态改成"已停止"。
- `LastError` 是"返回值表达不了的信息"的兜底通道，成功后必须清空，避免陈旧错误误导诊断。
- 任何写操作都必须与 `Dispose` 互斥；海康用单一 `operationGate`（`SemaphoreSlim(1,1)`）收敛，唯一例外是 SDK 采集回调线程。详见 [[并发与资源/并发纪律]]。
- `ICamera` 上**不放**厂商独有能力（缓冲配置、链路探测、设备改名）。新增能力走独立接口，见 [[公共契约/能力接口与扩展点]]。

**章节来源**
- [Junevy.EasyCamera.Core/Abstractions/ICamera.cs](file://Junevy.EasyCamera.Core/Abstractions/ICamera.cs)
- [Junevy.EasyCamera/Vendors/HikVision/HikCamera.cs](file://Junevy.EasyCamera/Vendors/HikVision/HikCamera.cs)

## ICameraStream 成员

`ICameraStream : IDisposable`（实现 `Junevy.EasyCamera/Common/CameraStream.cs`）。

| 成员 | 说明 |
| --- | --- |
| `int SubscriberCount` | 当前有效订阅数 |
| `FrameStreamStatistics Statistics` | 累计 `Published`/`Delivered`/`Dropped` |
| `void Publish(IFrame frame)` | 发布帧，转移所有权 |
| `void Subscribe(string subscriberKey, int capacity, Func<string, IFrame, Task> handler, Action<Exception> whenException = null)` | 注册/替换订阅者 |
| `bool Unsubscribe(string subscriberKey)` | 移除订阅者；不存在返回 `false` |

实现要点：`Publish` 每次都 `Interlocked.Increment(published)`；流已释放或无订阅者时 `dropped++` 并立即释放帧的初始引用。

**章节来源**
- [Junevy.EasyCamera.Core/Abstractions/ICameraStream.cs](file://Junevy.EasyCamera.Core/Abstractions/ICameraStream.cs)
- [Junevy.EasyCamera/Common/CameraStream.cs](file://Junevy.EasyCamera/Common/CameraStream.cs)

## Publish 的所有权转移

`Publish(frame)` 的语义是**所有权转移**，不是借用：

1. 调用方（海康回调）把帧的**初始引用**交给流；`Publish` 返回后不得再访问该帧。
2. 流对每个订阅者 `AddRef()` 后 `TryWrite`，最后由流自己释放初始引用。
3. `frame.AddRef()` 抛异常（例如帧已被释放）时，流捕获异常并跳过该订阅者，继续处理其余订阅者——发布线程不会被单个订阅者拖垮。此时**没有取得引用，所以无需归还，也不计入 `Dropped`**；只有 `AddRef` 成功而 `TryWrite` 意外抛异常时，才归还刚取得的引用并计入 `Dropped`。

`handler` 签名是 `Func<string, IFrame, Task>`，两个参数分别是 `cameraKey` 与 `frame`。handler 内若要把帧传给其它线程/组件，必须在 handler 返回前完成 `AddRef`。

> [!danger] 不要在 `Publish` 之后仍持有帧引用做异步处理。海康回调里 `frame` 字段在 `Publish` 成功后立刻置 `null`（所有权已移交数据流），继续使用会与订阅者的释放并发，读到已释放的非托管内存。内存细节见 [[并发与资源/帧所有权与内存]]。

不提供 `whenException` 时，handler 抛出的异常会终止该订阅的 worker；实现会在 `finally` 中把订阅者从流里摘除（`RemoveDeadSubscriber`，且只在字典中仍是同一实例时才移除），避免继续向"已死订阅者"的通道写帧导致帧滞留泄漏。订阅静默消失是唯一信号——重要订阅务必提供 `whenException`。

> [!bug] 例外：handler 抛 `OperationCanceledException`/`TaskCanceledException` 且未提供 `whenException` 时，不会自摘除——worker 退出但订阅者仍在注册表里（`SubscriberCount` 不减、通道里的帧被持有）。实测与成因见 [[架构设计/帧数据流与生命周期]]。

**章节来源**
- [Junevy.EasyCamera/Common/CameraStream.cs](file://Junevy.EasyCamera/Common/CameraStream.cs)
- [Junevy.EasyCamera/Vendors/HikVision/HikCamera.cs](file://Junevy.EasyCamera/Vendors/HikVision/HikCamera.cs)

## Subscribe 的替换语义

- 同 `subscriberKey` **重复订阅是原子替换**：新订阅者先构造并启动 worker，再在锁内 `TryRemove` 旧订阅者并写入新订阅者，旧订阅者的 `Dispose` 在锁外执行。
- `capacity < 1` 被抬为 `1`（流层兜底）；服务层 `SubscribeFrameStream` 则先把 `capacity <= 0` 回落为 `IStreamOptions.StreamCapacity`（默认 5）。
- 流已释放时 `Subscribe` 抛 `ObjectDisposedException`；`subscriberKey` 为空抛 `ArgumentException`，`handler` 为 `null` 抛 `ArgumentNullException`。
- `SingleReader = true`、`SingleWriter = false`、`AllowSynchronousContinuations = false`；背压策略映射见 [[并发与资源/背压与丢帧统计]]。

**章节来源**
- [Junevy.EasyCamera/Common/CameraStream.cs](file://Junevy.EasyCamera/Common/CameraStream.cs)
- [Junevy.EasyCamera/Common/CameraStreamSubscriber.cs](file://Junevy.EasyCamera/Common/CameraStreamSubscriber.cs)

## IFrame 与引用计数

`IFrame : IDisposable`。

| 成员 | 说明 |
| --- | --- |
| `IntPtr PixelDataPtr` | 非托管指针；帧释放后返回 `IntPtr.Zero` |
| `byte[] Data` | 托管副本，首次访问时懒缓存 |
| `int Stride` | 行步长（字节） |
| `uint Width` / `uint Height` | 宽高 |
| `ImagePixelFormat PixelType` | 像素格式 |
| `ulong ImageSize` | 图像字节数 |
| `void AddRef()` | 增加引用计数 |
| `Bitmap GetBitmap()` | 独立位图副本；帧释放后返回 `null` |

规则：

- `AddRef` 与 `Dispose` 配对；引用计数归零时物理释放非托管缓冲，且只释放一次。
- `AddRef` 必须在持有帧的回调（订阅 `handler`）返回前完成。
- 订阅者是**只读消费者**：拿到的所有订阅者共享同一份克隆缓冲，不要在 handler 里修改像素内容。

**章节来源**
- [Junevy.EasyCamera.Core/Abstractions/IFrame.cs](file://Junevy.EasyCamera.Core/Abstractions/IFrame.cs)
- [Junevy.EasyCamera/Vendors/HikVision/HikFrameWrapper.cs](file://Junevy.EasyCamera/Vendors/HikVision/HikFrameWrapper.cs)

## ILineScanCamera 扩展点

`ILineScanCamera : ICamera` 目前是**空的标记接口**，仓库内没有任何实现。它是给线阵相机预留的类型判别位：消费方可写 `if (camera is ILineScanCamera)` 判断是否走行触发/行高累加逻辑，而不必依赖具体厂商类型。

新增厂商若实现线阵，直接实现 `ILineScanCamera`；库本身不会因为该接口做任何特殊分支。