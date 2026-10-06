# CHANGELOG

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

## 2026-10-05（2026-10-05 审查问题修复）

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
