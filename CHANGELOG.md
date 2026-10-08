# CHANGELOG

## 2026-10-08（1.2.0：设备掉线检测与通知）

依据 `docs/superpowers/plans/2026-10-08-device-disconnect-handling.md`（审查 2026-10-08 第 2 项）。**新增公共 API**（`ICameraService` 新增事件），版本 1.2.0；不做门面层自动重连。

- **问题**：海康 `IDevice.DeviceExceptionEvent`（`DisConnect`）从未订阅。拔线后相机状态停在 `Grabbing`；门面 `StopGrab` 因"未连接"直接失败；对同 key 再次 `OpenCamera` 会在仍打开的句柄上再次 `Open()`；直接 `ICamera.Close()` 时 `StopGrabbing` 失败进入回滚，可能永远关不掉；订阅者收不到任何通知。
- **设计**：
  - `HikCamera` 新增生命周期状态 `Lost`（设备已掉线）。`IsConnected`/`IsGrabbing` 为 false，帧回调不再发布，但到达的帧仍归还 SDK 缓冲区。
  - 设备异常回调在 SDK 线程运行，**不进入 `operationGate`**：只在 `stateLock` 内做 `Open/Grabbing/Closing → Lost` 的条件迁移并记录原因；仅 `Open/Grabbing` 时事件在线程池上异步派发（`Closing` 期间的掉线只改状态，不派发），处理程序异常被吞掉，回调内任何异常不外泄。同一次连接最多通知一次。
  - 掉线后 `StopGrab` 尽力调用原生停流并返回成功（掉线原因保留在 `LastError`）；`Close` 走释放路径（`TearDownLostDevice`：解绑 → 尽力停流 → 等待在途回调 → 尽力关闭/销毁句柄 → 清空引用），返回成功；`Connect` 先释放旧句柄再由 `deviceFactory` 重建。
  - **验收修正（2026-10-09）**：`CloseCore` 判定顺序改为 Disposed → 失败；**状态 Closed → 幂等成功**（不论 `device` 是否为 null，不做原生调用，清空 `LastError`）；状态 Lost → `TearDownLostDevice()`；之后才是 `device == null` → `Camera not initialized`（基本不可达，仅作防御）。`TearDownLostDevice` 结束于 `Closed`，释放后的二次 `Close` 幂等成功。设备异常回调在关闭期间保持绑定，仅在原生关闭成功后才解绑；关闭期间的掉线迁为 `Lost`（不派发事件）；关闭失败的回滚 `RestoreAfterFailedClose` 在 `stateLock` 内发现已为 `Lost` 时改走 `TearDownLostDevice()` 并返回其结果，不回滚到 `Open/Grabbing`（判定与恢复在锁内完成，释放路径在锁外执行，因其会等待在途回调）。
  - 新增能力接口 `IConnectionMonitor`（`Disconnected` 事件，ISP，不改 `ICamera`）与事件参数 `CameraDisconnectedEventArgs`（`CameraKey`/`SerialNumber`/`Reason`/`OccurredAtUtc`，`WithCameraKey` 复制填充门面 Key）。
  - 门面 `CameraService`：新建并注册相机后，若实现 `IConnectionMonitor` 则订阅一次并转发为 `CameraDisconnected`（填充 `CameraKey`）；单个订阅者异常被吞掉，不影响其他订阅者。
  - 门面 `StopGrab(key)`：相机存在即调用 `camera.StopGrab()`（相机层幂等），不再以"未连接"拒绝；未注册的 key 仍返回失败。
  - **手动重连**：掉线后对同一 key 再次 `OpenCamera` 即重连（相机复用注册实例，沿用注册时的设备信息）；若设备 IP/枚举信息已变化，先 `Close(key)` 再用新的 `info` 打开。重连后需重新 `StartGrab`；帧流订阅保留，无需重订。
- **测试**（新增 16 项，全部在既有测试文件中，未新增测试文件；验收修正后 `HikCameraStateTests` 另有 1 项改名、4 项新增）：
  - `HikCameraStateTests`：`Disconnect_WhileGrabbing_MarksLostAndNotifiesOnce`（状态/原因/2 秒内派发/连续两次只通知一次）、`Disconnect_WhileGrabbing_FrameCallbackStopsPublishingButReturnsBuffer`、`StopGrab_AfterDisconnect_ReturnsSuccessAndAttemptsNativeStop`、`Close_AfterDisconnect_ReleasesDeviceAndReturnsSuccess`、`Connect_AfterDisconnect_RebuildsDeviceHandleAndGrabs`、`DeviceException_AfterCloseOrDispose_RaisesNoNotification`、`Disconnected_HandlerException_DoesNotBreakCloseOrReconnect`、`DeviceException_WhileDisposeWaitsForInFlightCallback_DoesNotDeadlock`（守卫：掉线回调不得进入门，否则与持门等待在途回调的 `Dispose` 死锁）。
  - `CameraManagerServiceRegressionTests`：`CameraService_CameraDisconnected_ForwardsCameraKey`、`CameraService_CameraDisconnected_SubscriberException_DoesNotStopOtherSubscribers`、`CameraService_StopGrab_DisconnectedButRegistered_ReturnsSuccess`、`CameraService_OpenCamera_AfterDisconnect_ReconnectsSameKeyWithoutNewCamera`。
  - 验收修正新增（`HikCameraStateTests`，4 项）：`Close_AfterDisconnect_CalledTwice_BothSucceedAndReleaseOnce`（掉线 → Close → Close，两次都成功、句柄只释放一次）、`Facade_ReconnectFailsWhenFactoryThrows_CloseByKeyStillRemovesCamera`（重连时工厂抛异常 → Connect 失败 → 门面 `Close(key)` 返回成功并移除相机）、`Close_WhenDisconnectedDuringStopGrab_ReleasesDeviceAndReturnsSuccess`（停流期间掉线 → Close 成功、设备已释放、无 Disconnected、之后可重连）、`Close_WhenDisconnectedDuringNativeClose_ReleasesDeviceAndReturnsSuccess`（原生关闭期间掉线并返回失败 → 同上）。改名：`Close_WhenNeverConnected_StillReportsNotInitialized` → `Close_WhenNeverConnected_IsIdempotentSuccess`（断言成功且未创建原生句柄）。
- **偏离计划与实现细节**（第 4、5 项已按验收意见修正，第 1 项已接受）：
  1. **SDK 构造函数非公开**：计划假定 `DeviceExceptionArgs(DeviceExceptionType)` 为公开构造函数，实际 `MvCameraControl.Net.dll`（4.5.0.2）中为 `internal`。测试经反射调用 SDK 自身的构造函数（`CreateDisconnectArgs`）；生产代码只读取 `MsgType`，不受影响。
  2. **状态迁移与掉线迁移互斥**：计划只要求掉线处理在 `stateLock` 内改状态，但门内的 `Open→Grabbing`、`Grabbing→Open`、`Open/Grabbing→Closing` 同样会与之竞争（否则可能把刚写入的 `Lost` 覆盖回 `Grabbing`/`Open`）。因此 `SetState` 与新增的 `TryMoveState` 均在 `stateLock` 内完成判定与写入，且诊断清空与迁移在同一临界区完成。
  3. `ConnectCore` 将 `SetLastError(null)` 提前到进入 `Open` 之前，避免清空刚由掉线处理写入的原因。
  4. **（已修正，验收意见）释放后的 Close 语义**：原计划让 `TearDownLostDevice` 置空句柄并回到 `Closed`，导致此后 `Close` 报 `Camera not initialized`，与"曾打开过则幂等成功"的契约冲突，门面 `Close(key)` 也可能因此报 `ReleaseFailed`。按验收意见采用更简单的方案（不引入"曾打开过"之类的并行标志）：`CloseCore` 对 `Closed` 状态无论 `device` 是否为空都幂等成功，`Camera not initialized` 仅作防御保留。这是**有意的语义变化**：`ICamera.Close` 对从未打开过的相机也幂等成功（`CameraManager.DisposeCamera` 对 Connect 失败的候选会先调用 `Close`，旧语义会制造假的 `ReleaseFailed`）。
  5. **（已修正，验收意见）关闭期间掉线**：计划规定 `Closing` 下忽略掉线事件，若设备恰在关闭过程中掉线、原生关闭失败，回滚后相机会长期显示为已连接。按验收意见修正：`Closing` 期间的掉线迁为 `Lost` 且不派发事件；关闭失败的回滚若发现已为 `Lost`，改走 `TearDownLostDevice()` 并返回成功；原生关闭成功则由 `Closed` 覆盖 `Lost`。
- **升级说明**：
  - `ICameraService` 新增 `CameraDisconnected` 事件；**自行实现该接口的消费方需补充该成员**（`CameraService` 已实现）。
  - 门面 `StopGrab` 对"已注册但未连接"的相机由失败改为调用相机层（成功）；依赖旧失败语义的调用方需复核。
  - `ICamera.Close` 对从未打开的相机幂等成功（此前报 `Camera not initialized`）：这是有意的语义变化。经门面 `Close(key)` 的调用无感知（门面只移除已注册相机）；直接使用厂商相机对象的代码若依赖旧失败语义需复核。
  - 掉线相机保持注册，`Close(key)` 仍是释放它的唯一入口；`OpenCamera` 对同 key 为重连，不是新建。
  - 版本递增到 1.2.0：两个类库 csproj 的 `<Version>` 与各自 `AssemblyInfo.cs` 的 `AssemblyVersion/AssemblyFileVersion`（1.2.0.0）同步。NuGet 按 `id + version` 缓存，若本机曾用同号本地构建过 1.2.0，需清理缓存。
- **验收**：`dotnet build`（sln，Release，`--no-incremental`）0 错误 0 警告；`dotnet test` net48 149/149、net8.0 149/149（`[TestMethod]` 总数 149 = 执行数）；`--no-build` 连续 5 轮全部通过。

## 2026-10-08（1.1.2：审查修复批次 A）

依据 `docs/superpowers/plans/2026-10-08-review-batch-a-fix.md`（审查 2026-10-08 第 1/3/4/5/6/7 项）。行为修正，**无破坏性 API 变更**。

- **任务 1：`HikCamera.IsConnected` 改为纯状态读取，原生检查只在守卫内**。问题：`IsConnected` 在 `operationGate` 外读 `device` 并调用原生 `IsConnected`，与 `DisposeCore` 并发会 NRE，或对已销毁句柄做原生调用；门面参数方法（不持 key 锁）会触发该竞态。修法：公开属性只读状态（`Open/Grabbing/Closing`），不读 `device`、不做原生调用；新增门内私有 `IsDeviceReady()`（状态 Open/Grabbing + 局部快照 `device.IsConnected`），`CheckReady`、`CurrentParameters`、`SetEnumParam`、`ExecuteCommand`、`TryGetParam`、`TryGetEnumParam`、`StartGrabCore` 改用它；`ConnectCore` 的"已打开"判断改为状态判断；`IBufferConfigurable.SetBufferCount` 整体入门，门内写 `bufferCount`，非 Open/Grabbing 时只记录。顺手修正 `StopGrabCore` 中两条语句挤一行的排版。测试：`HikCameraStateTests.IsConnected_IsPureStateRead_NeverCallsNativeDevice`、`IsConnectedAndParamAccess_RacingDispose_NeverThrow`（200 轮并发）。**注意**：公开 `IsConnected` 不再反映物理掉线，由计划 B（1.2.0，`Lost` 状态）补齐。
- **任务 3：`EasyCameraBuilder` 重复启用同一厂商去重**。问题：重复 `EnableHikVision()` 追加两份提供器与两个 SDK 系统，枚举结果重复，与 `Build()` 注释"去重为单实例语义"矛盾。修法：`enabledVendors` 集合（`StringComparer.Ordinal`），重复调用直接返回；新增 `internal EnabledVendorCount`。测试：`EasyCameraBuilderTests.EnableHikVision_CalledTwice_RegistersSingleVendor`。
- **任务 4：聚合提供器返回 `Unknown` 时回退侵入式探测**。问题：生产环境 provider 恒为 `AggregateCameraProvider`，无厂商具备能力时它返回 `Unknown`，`TryProbeByVendor` 把它当作有效结果返回，`ProbeByOpenClose` 永远不可达。修法：`TryProbeByVendor` 中厂商返回 `Unknown` 时返回 `null`；`AggregateCameraProvider` 的 XML 注释说明门面把 `Unknown` 视作回退信号。测试：`CameraServiceProbeTests.Probe_AggregateWithoutCapability_UnknownFallsBackToInvasiveProbe`（断言 `Idle`、`CreateCount == 1`，注册表与 `probe:SN-001` 帧流均已清理）。
- **任务 5：订阅者异常终止后的帧滞留窗口**。问题：`ConsumeAsync` 的 `finally` 先排空通道，再经 `RemoveDeadSubscriber` 移除订阅者；两步之间发布线程仍可 `TryWrite` 成功，帧无人消费，原生缓冲只能等终结器回收。修法：`finally` 第一步 `channel.Writer.TryComplete()`，之后发布方 `TryWrite` 返回 false 并自行归还引用；正常取消路径上幂等、无副作用。测试：`CameraStreamRegressionTests.DeadSubscriber_FramesPublishedAroundWorkerDeath_AreAllReleased`。
- **任务 6：`HikCamera.Close()` 在 Closed 状态幂等**。问题：状态已是 Closed 时再次 `Close` 仍调用原生 `Close`，大概率失败并进入回滚，`CameraManager.Remove` 因此报 `ReleaseFailed`，而相机实际已移除释放。修法：`CloseCore` 在 `device == null` 判断之后，状态 Closed 时直接返回成功、不做原生调用；从未 Connect 过的相机仍报 `Camera not initialized`。`CameraService.Close` 的 `ReleaseFailed` 消息改为"The camera has been removed from the registry and released, but the release reported errors: {LastError}"。测试：`HikCameraStateTests.Close_WhenAlreadyClosed_IsIdempotentWithoutNativeClose`、`Close_WhenNeverConnected_StillReportsNotInitialized`。
- **任务 7：`HikCameraSdkSystem` 检查 SDK 返回码，Release 异常不致计数错乱**。问题：`Initialize` 忽略 `SdkInitializeAction` 的返回码，初始化失败被当作成功；`Release` 中 `initialized = false` 位于 Finalize 之后，Finalize 抛异常时下次 `Release` 会二次递减全局计数。修法：返回码非 `MV_OK` 时回退引用计数并抛 `InvalidOperationException("HikVision SDK initialize failed with error code 0x…")`，`initialized` 保持 false；`Release` 中计数先递减，`initialized = false` 放进 `finally`；Finalize 返回非 `MV_OK` 时 `Trace.TraceWarning`（不抛）；锁内计数改为普通 `++/--`。测试：`HikCameraSdkSystemTests.Initialize_WhenSdkReturnsError_ThrowsAndRollsBackReference`、`Release_WhenSdkFinalizeThrows_RethrowsOnceAndKeepsCountBalanced`。
- **测试替身修正**：`HikCameraSdkSystemTests`、`EasyCameraBuilderTests` 的 SDK seam 原先返回计数值（会被当作错误码），现返回 `MV_OK`；状态测试的 `FakeDevice` 增加 `IsConnectedCalls`、`CloseCalls` 计数。
- **偏离计划的一处**：任务 6 的幂等短路置于 `device == null` 判断**之后**（计划原文写在其之前）。原因：计划同一条测试要求"未 Connect 过的相机 `Close` 仍报 `Camera not initialized`"，两者只能取其一。
- **版本递增到 1.1.2**：两个类库 csproj 的 `<Version>` 与各自 `AssemblyInfo.cs` 的 `AssemblyVersion/AssemblyFileVersion`（1.1.2.0）同步。NuGet 按 `id + version` 缓存；若本机曾用同号本地构建过 1.1.2，需清理缓存。
- **修复前后对照**（工作树未动：把 `HEAD` 源码导出到临时目录，叠加本批次测试后运行）：`IsConnected_IsPureStateRead…`、`Close_WhenAlreadyClosed…`、两个 SDK 用例、`EnableHikVision_CalledTwice…`、`Probe_AggregateWithoutCapability…` 失败；`DeadSubscriber…` 本次运行失败（概率性）；`IsConnectedAndParamAccess…` 通过（窗口极窄，是守卫而非确定性复现）；`Close_WhenNeverConnected…` 通过（行为保持）。
- **验收**：`dotnet build`（sln，Release，`--no-incremental`）0 错误 0 警告；`dotnet test` net48 133/133、net8.0 133/133（`[TestMethod]` 总数 133 = 执行数，较 1.1.1 的 124 新增 9 项）；连续 6 轮重复运行结果一致。

## 2026-10-06（1.1.1：厂商 SDK 托管封装随包分发——修复消费方必崩）

起因：第三方程序崩溃，排障结论为"nupkg 未声明厂商 SDK 依赖"。**经端到端复现属实**，但根因与修法均需修正，详见 `docs/代码审查报告-2026-10-06.md` 第五节。

- **P0：厂商托管封装程序集从未打进 NuGet 包**。主工程用 `<Reference HintPath=..\Libs\... Private=true>` 引用 `MvCameraControl.Net.dll` / `MVSDK_Net.dll`；`Private=true` 只让本仓库自己的输出目录拿到 DLL，`dotnet pack` 既不把文件引用放进包内、也不产生依赖声明。消费方还原后必然失败：`Initialize()` 抛 `AggregateException: The type initializer for 'HikCameraSdkSystem' threw an exception`，`EnumerateCameras()` 抛 `FileNotFoundException('MvCameraControl.Net, Version=4.5.0.2')`，未捕获即崩溃。
- **修法**：新增无条件 `Pack` 项把两份厂商封装打进 `lib/net48;lib/net8.0`（保持 `Libs/` 单一来源，不复制 DLL 到工程目录）。刻意**不**采用"依赖 nuget.org 上的 `MvCameraControl.Net`"——那里只有第三方未验证转包（1.1.0/4.4.1.x/4.8.0.3），与本仓库 pin 的 **4.5.0.2** 版本对不上，依赖它等于引入供应链风险。
- **两个已踩过的坑（写进 csproj 注释，避免复发）**：① `PackagePath="lib/"` 在包内已存在 `lib/<tfm>/` 时会被 NuGet 整体忽略，必须逐 TFM 写全；② 多目标项目打包走外层构建，此时 `$(TargetFramework)` 为空，按 TFM 分条件的 ItemGroup 恒不执行，必须用无条件分组 + 分号多路径。
- **防复发：新增包内容守卫测试** `Tests/Packaging/PackageContentTests.cs`——定位仓库根、取最新 nupkg，断言两个 TFM 下均含 `MvCameraControl.Net.dll` 与 `MVSDK_Net.dll`（无产物时断言为 Inconclusive，不产生假失败）。这类缺陷单元测试天然测不到（本仓库全绿、消费方必崩），故直接校验打包产物。已验证：**移除打包规则后该测试立即失败**。
- **版本递增到 1.1.1**：NuGet 按 `id + version` 缓存，消费方还原过 1.1.0 后重发同版本不会被采用（本次验证过程中即被该缓存误导过一次）。**升级方必须删除旧版本缓存或显式还原新版本。**
- **文档**：README 新增"运行与安装前置条件"（含"手工拷贝 DLL 到 exe 旁边对 .NET Core 无效"这一常见误判）；SKILL.md 的环境与故障速查同步补充；AGENTS.md 记入构建约定。
- **验收**：`dotnet build`（sln，Release，`--no-incremental`）0 错误 0 警告；`dotnet test` net48 124/124、net8.0 124/124；**端到端**：仅引用仓库真实产出的 1.1.1 nupkg、零手工拷贝 DLL，net8.0 与 net48 消费方均 `Initialize OK` + `EnumerateCameras OK`。

## 2026-10-06（1.1.0：审查修复三批 + 能力接口化 + 背压策略与丢帧可观测）

审查报告见 `docs/代码审查报告-2026-10-06.md`。本轮为**破坏性变更**（1.0.1 → 1.1.0），面向消费方的迁移清单见 `skills/using-junevy-easycamera/SKILL.md`。

### 一、隐藏 Bug 修复

- **探测测试从未参与编译（P0）**：`Junevy.EasyCamera.Tests.csproj` 采用显式 `Compile` 清单，新增的 `CameraServiceProbeTests.cs` 未加入，因此 CHANGELOG 声称的"6 项探测测试"实际一次都没跑过（仓库实有 98 个 `[TestMethod]`，只执行了 92 个）。已加入编译清单，并修掉该文件自身的 3 个编译错误（两个游离在类尾的重复 `ProbeLinkStatus` 成员 CS0111；`SerialReportingProvider`/`FailingCreateProvider` 未实现接口成员 CS0535——根因是误以为接口有默认实现，net48 不支持 DIM）。
- **`CameraService.Close` 未持 per-key 锁（P0）**：`Close` 是唯一不经过 `GetKeyLock` 的写操作，会在 `OpenCamera` 的 `Connect()` 或 `StartGrab` 的厂商调用中途把实例摘出注册表并释放，随后对已释放实例继续操作（native use-after-free）。已纳入同一把 key 锁；新增 2 个并发回归测试（Close↔OpenCamera、StartGrab↔Close），并已验证"去掉修复后这 2 个测试确实失败"。
- **海康参数/命令路径完全在互斥之外（P0）**：`SetParam`×4 / `SetEnumParam` / `ExecuteCommand` / `TryGetParam` / `TryGetEnumParam` 此前既不加锁也不经 `operationGate`，与 `Dispose` 并发时会抛 `NullReferenceException` 直接穿透到调用方（破坏"失败一律走 `CameraResult`"契约），甚至在已释放的 native 参数对象上调用。现已全部纳入统一守卫。
- **海康 `Close` 异常路径不恢复状态（P1）**：显式失败分支会回滚 `closing/callbacksEnabled`，但 `catch` 分支不会，导致相机永久停在"关闭中"——`IsConnected` 仍为 true 却一帧都不再下发（静默丢帧且无任何报错）。现所有失败路径（含原生异常）统一经 `RestoreAfterFailedClose` 回滚。

### 二、设计重构

- **生命周期状态机（模板方法）**：`HikCamera` 用单一 `CameraState`（`Closed/Open/Grabbing/Closing/Disposed`）取代 `isOpen/isGrabbing/closing/callbacksEnabled/disposed` 五个标志位，从结构上排除非法标志位组合；帧回调的发布判定退化为一次状态比较。`Connect/Close/StartGrab/StopGrab/Dispose` 与全部参数路径统一经 `ExecuteGuarded`/`TryExecuteGuarded`/`ExecuteDispose` 三个守卫收敛（互斥 + 异常翻译 + 诊断），并新增"相机已连接但厂商未暴露参数节点"必须报失败的分支（此前会报成功假象）。
- **接口隔离（能力接口）**：厂商独有能力从基础接口移出，避免"每加一个能力就破坏所有实现者"——`ILinkStatusProbeProvider`（可达性探测，从 `ICameraProvider` 移出）、`IBufferConfigurable`（采集缓冲配置）、`INamedCameraInfo`（本地改名）。`AggregateCameraProvider` 以能力探测方式分发，无厂商具备能力时返回 `Unknown`。
- **背压策略可配（策略模式）**：`StreamOptions.BackpressureMode` 提供 `DropOldest`（默认，淘汰最旧、保留最新）与 `RejectNewest`（队列满时拒收新帧、按序处理已入队帧，适合线阵测量/计数这类不能丢帧也不能乱序的场景）。**刻意不提供"丢新帧"模式**：实测 `System.Threading.Channels` 的 `BoundedChannelFullMode.DropNewest` 与 `DropOldest` 行为完全一致（都淘汰最旧帧），提供该选项只会名不副实。
- **丢帧可观测**：`ICameraStream.Statistics` 与 `ICameraService.GetStreamStatistics(cameraKey)` 暴露累计 `Published/Delivered/Dropped`（新增 `FrameStreamStatistics`）。此前背压淘汰完全静默，现场无法区分"采集慢"与"消费慢"。
- **`CameraStream` 锁纪律**：`operationLock` 只保护订阅者注册表；`AddRef`/`TryWrite`/背压淘汰/5MB 帧释放/用户 `whenException` 回调全部移到锁外执行。订阅与退订不会再被慢速释放阻塞，发布线程也不会被订阅管理阻塞。
- **取参去重**：`IParameterSource`（6 个读取原语）+ `ParameterReader`（泛型分发与失败语义）取代海康/华睿各写一遍的 `typeof` 链；含 long→int 受检转换（越界按失败处理）。新增 `double` 取值支持（海康由单精度提升）。

### 三、帧包装器并发加固

- `HikFrameWrapper` 的宽高/步长/像素格式/大小改为**构造时一次性快照**，此后全是字段读取：既消除每帧重复穿透 SDK，也消除"先判已释放再读原生内存"的 check-then-use 竞态。
- `Data`/`PixelDataPtr`/`GetBitmap` 统一在 `refLock` 内访问原生内存，与引用计数归零的物理释放互斥；`PixelDataPtr` 在已释放帧上返回 `IntPtr.Zero` 而不是悬空指针。

### 四、探测语义修正（`CameraLinkStatus` 五态，破坏性）

- `CameraLinkStatus`：`Unknown = 0`（新增，未能判定）、`Connected = 1`、`Idle = 2`、`Occupied = 3`、`Unreachable = 4`（新增，掉线/未上电/枚举不到）。此前 `Connected = 0` 是枚举默认值，未初始化变量会谎报"已连接"；此前"枚举中消失"被归入 `Occupied`，界面会把"相机没插"显示成"被占用"。
- `ProbeCameraLinkStatus`：`info` 为 null 或序列号为空 → `Unknown`（不再谎称被占用）；厂商探测抛异常（如 SDK 未初始化）→ 静默降级为侵入式回退，不再把异常抛给界面线程；厂商重新枚举不到 → `Unreachable`；侵入式回退无法区分"被占用"与"设备不存在"，按"被占用"保守表达并在文档注明。
- **探测资源清理**：侵入式回退此前只清理相机注册表，每次探测都在 `StreamManager` 里永久留下一条 `probe:{serial}` 的帧流；现改为 `finally` 中同时清理相机与帧流。
- 海康探测按 `info.InterfaceType` 缩小枚举范围（未知类型才全量枚举），显著降低单次探测成本。

### 五、公共 API 变更（破坏性，逐条列出）

- `ICamera.StopGrab()` 由 `void` 改为返回 `CameraResult`（与 `ICameraService.StopGrab` 对齐；`CameraService.StopGrab/SetTrigger` 据此精确上报失败原因与 `LastError`）。
- `ICamera` 新增 `LastError`（此前只有具体类有，门面拿不到厂商诊断）。
- `ICameraStream` 新增 `Statistics`；`ICameraService` 新增 `GetStreamStatistics`、`SubscribeFrameStream/UnsubscribeFrameStream` 参数改名 `subKey` → `subscriberKey`（与 `ICameraStream` 统一）。
- `ICameraProvider` 移除 `ProbeLinkStatus`（迁至 `ILinkStatusProbeProvider`）；同时修掉该文件里两段重复的 `<summary>` 合并残留。
- `ICamera` 实现类新增 `IParameterSource`；`HikCamera.SetBufferCount`、`HikCameraInfo/IRaypleCameraInfo.SetDefinedName` 改为能力接口的显式实现（不再出现在具体类公开面上）。
- `CameraStreamSubscriber` 由 public 收为 internal（实现细节，无外部引用）；删除其未被读取的 `Key` 与 `Worker` 成员。
- 构造函数改为依赖抽象：`CameraService(..., IStreamOptions)`、`StreamManager(IStreamOptions)`、`CameraStream(string, IStreamOptions)`、`HikCameraProvider(IStreamOptions)`、`IRaypleCameraProvider(IStreamOptions)`——传入 `StreamOptions` 的旧代码仍可编译（源码兼容）。

### 六、其它修复与清理

- **`SupportedOSPlatform("windows")` 静默失效**：两个类库都设了 `GenerateAssemblyInfo=false`（为使用手写 `Properties/AssemblyInfo.cs`），该设置会连带跳过 csproj 的 `<AssemblyAttribute>` 项——平台标注实际从未生效，全量重建时会出现 20+ 条 CA1416 告警（增量构建看不到）。改为在 `Properties/PlatformCompatibility.cs` 中以 `#if NET8_0_OR_GREATER` 显式声明，测试工程同样补齐；并移除两个 csproj 中失效的 `<AssemblyAttribute>` 项。现全量重建 0 警告。
- **IRayple（保留但加固）**：按"继续保留"处理，不删代码，但补齐与海康一致的纪律——新增 `operationGate` 串行化全部原生访问、`LastError`、释放前排空在途回调（带 5s 上限）、同一原生句柄只绑定一次帧回调（此前每次 `StartGrab` 都 `IMV_AttachGrabbing`，会导致每帧重复发布）、`StopGrab` 返回 `CameraResult`、`IRaypleFrameWrapper.AddRef` 增加已释放防护与引用计数幂等；`IRaypleCameraSdkSystem` 删除只写不读的 `isInitialized`（Irayple SDK 无全局初始化接口，`Initialize/Release` 为空操作）。
- **死代码清理**：删除两个遗留的 `packages.config`（已改 PackageReference + 中央包管理，CHANGELOG 2026-09-15 曾声称已删除）；`CameraServiceProbeTests`/`CameraManagerServiceRegressionTests` 中两处游离的重复方法；`IRaypleCameraProvider.ProbeLinkStatus` 空桩。保留项：`ILineScanCamera`（线阵扩展点，README 路线图已挂账）、`IFrame.PixelDataPtr`（文档化的逃生舱）、`EasyCamera.CreateBuilder`。
- **版本对齐**：`PackageVersion` 与 `AssemblyVersion/FileVersion` 统一为 1.1.0（此前包版本 1.0.1、程序集版本仍 1.0.0.0）。
- **测试**：新增 `CameraServiceProbeTests`（13 项，探测语义/异常降级/能力分发/资源清理/取消）、`CameraStreamBackpressureTests`（7 项，背压策略与统计）、`HikCameraStateTests`（8 项，状态机与失败路径回滚、参数路径不再抛异常），`CameraManagerServiceRegressionTests` 增补 2 项锁竞争回归；`MockCamera`/各测试桩同步新契约。
- **验收**：`dotnet build`（sln，Release，`--no-incremental`）0 错误 0 警告；`dotnet test` net48 122/122、net8.0 122/122 全部通过（基线 92 + 新增/复活 30）。

## 2026-10-06（1.0.1：相机链路三态身份与非侵入可达性探测入库）

- 新增 `Junevy.EasyCamera.Core.Abstractions.CameraLinkStatus` 三态枚举（`Connected`/`Occupied`/`Idle`，稳定身份只增不改），描述"本进程视角"下相机的可连接性；消费方（AutomationSystem 宿主的相机设置对话框与相机采集节点）据此渲染链路状态。
- `ICameraService` 新增两个成员：`IsSerialConnected(string serial)`——零侵入扫描连接注册表按序列号匹配（配套 `ICameraManager` 新增 `Snapshot()` 只读快照）；`ProbeCameraLinkStatus(ICameraInfo, CancellationToken)`——自持早退 Connected → 厂商可达性查询 → 厂商不支持时回退侵入式 `probe:{serial}` 开关探测。
- 非侵入可达性查询：海康托管包装的 `DeviceEnumerator.IsDeviceAccessible(IDeviceInfo, DeviceAccessMode)`（"判断设备是否可达"）。实现要点：不信任调用方传入的原生引用（可能过期），库内重新 `EnumDevices` 取新鲜设备信息按序列号匹配；查询模式取 `DeviceAccessMode.AccessExclusive`——与 `IDevice.Open()` 无参重载"默认以独占权限打开"一致，"可达"≈"可连接"；本进程独占持有的相机同样会令可达性查询返回 false，因此自持判定必须先行。
- `ICameraProvider` 新增 `ProbeLinkStatus` 抽象成员（返回 `null` 表示无探测能力）：`AggregateCameraProvider` 按厂商支持关系分发；`HikCameraProvider` 实现可达性查询；`IRaypleCameraProvider` 与测试桩返回 `null` 走回退。接口为破坏性变更（1.0.0 → 1.0.1），仓库内实现者与测试桩已同步。
- 测试：新增 `CameraServiceProbeTests` 6 项（自持早退不触厂商、可达/不可达路由、回退探测后无注册表残留、打开失败报 Occupied、序列号匹配）；`MockCamera` 增加 `SerialNumberToReport` 可编程属性。全量 92/92（net48 + net8.0 双目标）。
- 应用侧配套（AutomationSystem 仓库）：对话框与相机采集节点升级 1.0.1，节点配置页三态徽章与"确保已连接"复用语义；连接生命周期进程级池化，废止"对话框关闭即断开"契约。

## 2026-10-05（审查问题修复）

审查报告见 `docs/代码审查报告-2026-10-05.md`，实施计划见 `docs/superpowers/plans/2026-10-05-review-findings-fix.md`。

- **StreamManager**：补齐声明过但缺失的 disposed 防护——释放后 `GetOrCreateStream` 抛 `ObjectDisposedException`（与 Dispose 竞态时就地释放新建流，杜绝无人持有的流常驻）、`RemoveStream` 返回 false；新增回归测试。
- **HikCameraSdkSystem**：Initialize 改为真按实例幂等（重复调用只持一个全局引用，此前每次调用都递增计数导致 SDK 永不 Finalize）；未 Initialize 的实例 `Release` 不再削减他人引用；全部引用计数转移在全局锁内完成，封死"并发 Initialize 与 Finalize 交叉"窗口；新增测试 seam（`Func<int>`）与 4 个回归测试。
- **CompositeCameraSdkSystem**：`Initialize`/`Release` 增加异常隔离——单个厂商失败不中断其余厂商，全部处理完后以 `AggregateException` 汇报（与 `Dispose` 行为对齐）。
- **CameraManager/CameraService**：`ICameraManager.TryRemove` 更名为 `Remove` 并返回新枚举 `CameraRemoveStatus`（Removed/NotFound/ReleaseFailed），`Close` 可精确区分"相机不存在"与"释放失败"；`LastError` 提升到接口并在成功清理后清空，消除陈旧错误；`CameraService.Close` 移除对具体类 `CameraManager` 的类型嗅探。
- **CameraStream**：同 Key 重复订阅改为**原子替换旧订阅者**（此前静默丢弃新订阅且无任何信号，调用方误以为新 handler 生效）；`Subscribe` 改为先构造订阅者再启动 worker（实例经参数传入工厂），消除"闭包读取尚未赋值局部变量"的时序依赖；`CameraStreamSuber` 增加 `StartWorker`/`Worker`，CTS 释放兜底幂等化。
- **CameraService**：`StartGrab`/`StopGrab`/`SetTrigger` 统一使用 per-key 操作锁（此前仅 `StopGrab` 持服务级锁，互斥形同虚设）；`StartGrab` 改为幂等（已在取流时返回成功，与 `StopGrab` 一致）；成功结果 `Code` 归零（去除魔数 1），并约定成功 Code 恒为 0；`SubscribeFrameStream` 在流释放竞态下按契约返回 false，不再外泄 `ObjectDisposedException`。
- **HikCamera/IRaypleCamera**：`GetParam<int>` 增加 long→int 受检转换（`TryConvertToInt64ToInt32`），越界按"取值失败"返回 default，禁止静默回绕。
- **HikFrameWrapper**：`Data` 改为懒缓存托管副本——SDK 的 `PixelData` 可能每次访问重新拷贝（5MB+/次），缓存后每帧至多一次；已释放帧返回已缓存副本（从未访问过则返回空数组），禁止触达已释放原生内存。
- **HikCameraProvider**：设备枚举失败时输出 `Trace` 警告（含错误码），不再静默返回空集合。
- **接口契约文档**：`ICameraStream.Publish` 注释修正为与实现一致的所有权转移语义；`ICameraManager.TryRegister` 注明 false=Key 已注册；`CameraResult.Code` 增加取值约定；`SetDefinedName` 注明仅更新本地副本不下发设备；修正 OpenCamera 错误消息与包描述拼写。
- **新增 TryGetParam 扩展**：`ICamera`/`ICameraService` 增加 `TryGetParam<T>`/`TryGetEnumParam`，可区分"参数值恰为 default"与"获取失败"；原 `GetParam`/`GetEnumParam` 保留并改为薄封装，逻辑去重。
- **公共 API 命名修正（破坏性，v1.0.0 未对外发布）**：`CameraStreamSuber`→`CameraStreamSubscriber`（参数 `subberKey`→`subscriberKey`，属性 `Suber`→`Worker`）；`CameraType`→`CameraInterfaceType`（枚举成员 `ALL`→`All`，该枚举表达物理接口类型而非品牌）；`GetOnlineCameraSerialNumber`→`GetSerialNumber`（返回已注册相机序列号，与"在线"无关）；`SetTrigger(triggerWay, isAcquisition)`→`SetTrigger(triggerSource, enableTrigger)`；帧流键统一命名 `cameraKey`（原 `userDefinedName`）；`CameraManager.operateLock`→`operationLock`、`HikCamera.locker`→`stateLock`；Core 命名空间注册类更名 `CoreServiceCollectionExtensions`（扩展方法调用点不受影响）。
- **文档**：新增 `README.md`（项目现状/快速上手/推荐用法/帧资源管理约定）；`AGENTS.md` 第 4 节同步更新公共契约要点与帧分发模型澄清。
- **新增 Agent 技能**：`skills/using-junevy-easycamera/SKILL.md`——面向消费本 NuGet 包的其他 Agent 的使用说明书（环境要求、命名空间、DI 快速上手、帧生命周期规则、API 速查、常见错误与故障速查）。经两轮子代理 TDD 验证：仅凭该技能文件即可产出签名与生命周期全部正确的接入代码。
- **新增非 DI 入口（Builder）**：`EasyCamera.Create(b => b.EnableHikVision().WithStreamOptions(...))` / `EasyCameraBuilder` / `EasyCameraHost`——不依赖 Microsoft.Extensions.DependencyInjection 的一行式组装，适配 Prism 等自带容器的框架（将 `host.Sdk`/`host.Service` 以单例实例注册进宿主容器）；`host.Dispose()` 幂等并保证"相机 → 帧流 → SDK"的释放顺序；启用未实现厂商与 DI 路径一致抛 `NotImplementedException`，未启用任何厂商抛 `InvalidOperationException`。SKILL/README 同步新增 Prism 接入章节。
- 验收：`dotnet build`（sln，Release）0 错误；`dotnet test` net48 84/84、net8.0 84/84 全部通过（基线 63 + 新增 21）。

## 2026-09-15（稳定性全链路审查与修复）

- 全链路审查相机/图像生命周期与非托管资源（审查报告见 `docs/稳定性审查报告-2026-09-15.md`），确认帧主线（回调内 Clone → 归还 SDK 缓冲 → 独立克隆发布 → 订阅者消费/淘汰/取消释放）与相机关闭主线（解绑回调 → StopGrab → 等待在途回调 → Close）设计正确，并修复以下缺陷：
- **CameraStream**：订阅 worker 因 handler 异常终止（未提供 `whenException`）后仍留在订阅字典中，后续 Publish 持续向已死订阅者通道写入帧，造成帧滞留泄漏与订阅静默失效——worker 异常终止时自移除订阅者并释放资源（`RemoveDeadSubscriber`，仅移除同 Key 同实例）；`CameraStreamSuber` 增加 `Key` 属性。
- **HikFrameWrapper**：增加终结器兜底，订阅方忘记 Dispose 时由 GC 释放克隆帧的非托管缓冲，消除永久泄漏；引用计数改为 `refLock` 短临界区保护，串行化 AddRef 与最后一次 Dispose，消除 use-after-free 竞态窗口；原生释放成功后 `GC.SuppressFinalize`。
- **HikCamera**：`WaitForCallbacks` 增加有限超时（5s），SDK 回调挂死时记录 `LastError` 并继续关闭，避免 Close/Dispose 永久阻塞；移除 `callbacksDrained` 死代码（保留 `Monitor.PulseAll` 排空机制）。
- **HikCameraSdkSystem**：SDK 全局 Initialize/Finalize 改为静态引用计数管理，引用归零才 `Finalize()`，避免多实例并存时一方 Release 导致其他在用实例失效；初始化失败回退引用计数。
- **StreamManager**：增加 disposed 防护，释放后 `GetOrCreateStream` 抛 `ObjectDisposedException`，杜绝流复活后常驻字典且永不释放。
- **IFrame 接口注释修正**：`AddRef` 明确为原生缓冲引用计数及"须在 handler 返回前完成引用转移"的契约；`GetBitmap` 明确 Bitmap 所有权与已释放返回 null 约定。
- 验收：`dotnet build` net48/net8.0 均 0 错误；`dotnet test` net48 63/63、net8.0 63/63 全部通过。

## 2026-09-15（双目标改造）

- 将 `Junevy.EasyCamera.Core`、`Junevy.EasyCamera`、`Junevy.EasyCamera.Tests` 从旧式 csproj / 单目标 net48 改造为 **SDK 风格 csproj + net48;net8.0 双目标**。
- 厂商 SDK DLL（`MvCameraControl.Net.dll`、`MVSDK_Net.dll`）复制到仓库 `Libs/` 目录统一引用，原外部 HintPath（`AutomationSystem(1)`、`Nuget`）在本机已失效。
- 迁移到中央包管理（PackageReference），`Directory.Packages.props` 补充 BCL 兼容包（net48 专用）与 `System.Drawing.Common`（net8.0）、`Microsoft.NET.Test.Sdk`（测试集成此前缺失）等版本声明；删除 packages.config。
- net8.0 声明程序集级 `SupportedOSPlatform("windows")`（工业相机 SDK 场景仅 Windows/x64），消除 CA1416。
- 修复存量编译错误：`ServiceCollectionExtensions` 中 IRayple 注册与 `[Obsolete("未开发完毕", true)]` 冲突——按用户确认改为与 Basler 一致的 `NotImplementedException`（启用 IRayple 时运行时抛出）；移除引用已不存在 API（注入式 `HikCameraSdkSystem` 构造函数）的两个失效回归测试；IRayple 相关 DI 测试改写为断言抛出 `NotImplementedException`。
- 测试工程补充 `CopyLocalLockFileAssemblies=true` 与 `Newtonsoft.Json`（SDK testhost 依赖解析需要）；新增 `global.json` 固定 SDK 9.0.316 保证构建可复现。
- 验收：`dotnet build` net48/net8.0 均 0 错误；`dotnet test` net48 63/63、net8.0 63/63 全部通过。

## 2026-09-15

- 修复 HikVision SDK 初始化/释放状态机，支持多实例引用计数并允许初始化失败后重试。
- 修复 HikCamera 采集停止、回调在途计数、SDK 帧缓冲归还及销毁时序。
- 修复 CameraStream 订阅竞争、Dispose 竞态、有界队列淘汰和帧引用释放问题。
- 修复 CameraManager/CameraService 的同键打开竞争、清理结果反馈和销毁后访问问题。
- 补齐 HikVision GenTL 映射，修复测试项目配置并增加生命周期/并发回归测试。
- 本次修改范围不包含 `Junevy.EasyCamera/Vendors/IRayple` 实现。
