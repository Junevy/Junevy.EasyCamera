# 项目开发约定

## 1. 项目介绍
本项目是一个封装了海康、IRayple（华睿）工业相机SDK的类库，用于：便于在第三方项目开发中操作工业相机、通过接口无缝更换相机硬件、通过Channel管理相机帧等功能。

## 2. 注意事项
- 考虑到工业相机的SDK大多数底层都由C++实现，可能存在许多的非托管资源，例如：图像数据（IntPtr）等，在开发时务必小心非托管资源泄露、忘记释放。
- 图像可能占用内存较大（5MB+），为了性能考虑，因尽量做到少分配图像与非托管资源。
- Clone图像时，应尽量深拷贝，避免工业相机的缓存队列爆满而丢失帧。

## 3. 其他约定
- 设计功能或API变更，必须检索Skills目录中当前项目的Skill是否需要更新。
- 对于不明确的功能，必须提问。
- 对于可扩展的功能，必须询问是否扩展。
- 如果有更好的实现方式或当前实现方式不符合设计规范、存在风险，必须提醒并询问，并检索是否有更好的方式，然后询问是否按照更优方式实现。
- 每次代码修改完毕后，必须摘要内容，记录到项目根目录的CHANGELOG.md中。
- 每次更新项目后，需要更项目介绍（第4项，如不存在则补充）到项目根目录下的AGENTS.md中，便于交接其他Agent。

## 4. 项目介绍

当前解决方案包含三个主要项目，均采用 SDK 风格 csproj，并**双目标 net48 + net8.0**：

- `Junevy.EasyCamera.Core`：公共抽象、相机服务/管理器/帧流契约及基础模型。
- `Junevy.EasyCamera`：厂商适配和公共实现；HikVision 使用 `MvCameraControl.Net`，IRayple 使用其原生 SDK。
- `Junevy.EasyCamera.Tests`：公共层、服务层和 HikVision 生命周期/并发回归测试，测试宿主固定为 x64。

构建与环境约定：

- 厂商 SDK DLL 位于仓库根目录 `Libs/`（`MvCameraControl.Net.dll`、`MVSDK_Net.dll`），所有工程统一从该目录引用，构建自包含。
- **厂商托管封装必须随 NuGet 包分发**（2026-10-06 消费方崩溃后确立）：`<Reference HintPath>` 只影响本仓库输出目录，`dotnet pack` 既不打包文件引用也不产生依赖声明，消费方必然 `FileNotFoundException`。修法是 `Junevy.EasyCamera.csproj` 中无条件 `Pack` 项 + `PackagePath="lib/net48;lib/net8.0"`，**禁止**写成 `"lib/"`（会被 NuGet 忽略），**禁止**按 `$(TargetFramework)` 分条件（外层构建时该变量为空）。新增品牌照此追加。
- **包版本必须递增**：NuGet 按 `id + version` 缓存，同版本重发不会被消费方采用。修复打包类问题务必升版本并在升级说明里写明"清理缓存"。
- `Tests/Packaging/PackageContentTests.cs` 是打包内容的回归守卫：改动打包规则后必须确认它仍然通过（移除规则时它必须失败）。
- 第三方包版本由根目录 `Directory.Packages.props`（中央包管理）统一维护；**本库版本写在各 csproj 的 `<Version>`**，`AssemblyVersion/FileVersion` 写在手写的 `Properties/AssemblyInfo.cs`，三者必须同步；net48 通过条件引用补充 BCL 兼容包，net8.0 使用 `System.Drawing.Common`。
- 两个类库在 net8.0 下声明程序集级 `SupportedOSPlatform("windows")`（工业相机 SDK 场景仅 Windows/x64），声明位置见下方并发纪律最后一条。
- 根目录 `global.json` 固定 SDK 9.0.316，保证构建/测试可复现。
- IRayple 厂商标记为 `[Obsolete("未开发完毕", true)]`：`ServiceCollectionExtensions` 不注册其服务，启用 `EnableIRayple` 时与 Basler 一致抛出 `NotImplementedException`。
- **厂商原生驱动（如 `MvCameraControl.dll`）不在包内**，属消费方环境前置条件（安装厂商运行时并确保 PATH 含其 `Runtime\Win64_x64`）；文档须与"托管封装随包分发"这两件事分开表述。

HikVision 与公共层的资源所有权约束：SDK 回调帧必须在回调内归还；发布给订阅者的帧必须是对 SDK 缓冲的**独立克隆——每帧仅在相机回调中克隆一次**，多订阅者通过引用计数**浅共享**同一克隆（不逐订阅者深拷贝，订阅者为只读消费者）；克隆缓冲由**最后一个归零的引用持有者**物理释放（不得固定为发布者，否则其余浅引用悬空）。相机关闭/销毁前会停止取流、解绑回调并等待在途回调结束（5s 上限）。`CameraStream` 使用非阻塞有界队列，队列淘汰的帧必须释放。

并发纪律（2026-10-06 审查后确立，新增厂商必须遵守）：

- **一切原生访问必须经厂商相机的 `operationGate`**（海康 `ExecuteGuarded`/`TryExecuteGuarded`/`ExecuteDispose` 三个守卫收敛：互斥 + 异常翻译成 `CameraResult` + `LastError` 诊断）。唯一例外是 SDK 采集回调线程，它只读状态并归还缓冲区。
- **生命周期用单一状态枚举表达**（海康 `CameraState`：`Closed/Open/Grabbing/Closing/Disposed/Lost`），不得再引入并行布尔标志位；`Publish` 判定只看状态。任何失败路径（含原生异常）必须回滚到进入前的状态，否则会出现"连接还在但一帧不发"的静默丢帧。
- **门面按 cameraKey 串行**：`CameraService` 的 `OpenCamera/Close/StartGrab/StopGrab/SetTrigger` 全部走同一把 `GetKeyLock(cameraKey)`；新增写操作必须并入，否则会重现 use-after-free 竞态。
- **`CameraStream.operationLock` 只保护订阅者注册表**：`AddRef`/`TryWrite`/背压淘汰/帧释放/用户异常回调一律在锁外执行。
- **`SupportedOSPlatform("windows")` 必须写在 `Properties/PlatformCompatibility.cs`**（`#if NET8_0_OR_GREATER`）：两个类库都设了 `GenerateAssemblyInfo=false`，csproj 的 `<AssemblyAttribute>` 项会被连带跳过，平台标注静默失效。
- **`IsConnected` 是纯状态读取**（1.1.2）：`state ∈ {Open, Grabbing, Closing}`，不读 `device`、不做原生调用，可在任意线程（含与 `Dispose` 并发时）调用。原生连接检查只能在 `operationGate` 内经 `IsDeviceReady()`（状态 Open/Grabbing + 局部快照 `device.IsConnected`）完成；`CheckReady`、`CurrentParameters`、参数与命令路径、`StartGrabCore` 均走它。
- **`DeviceExceptionEvent` 回调与帧回调一样运行在 SDK 线程，不得进入 `operationGate`**（1.2.0）：只在 `stateLock` 内做 `Open/Grabbing/Closing → Lost` 的条件迁移并记录原因；仅 `Open/Grabbing` 时事件才在线程池上异步派发（`Closing` 期间的掉线只改状态），吞掉处理程序异常；关闭期间设备异常回调保持绑定，原生关闭成功后才解绑；关闭失败的回滚（`RestoreAfterFailedClose`）在锁内发现已为 `Lost` 时改走释放路径，不回滚到 `Open/Grabbing`；门内的状态迁移（`SetState`/`TryMoveState`）同样在 `stateLock` 内完成，与之互斥。

公共契约要点（2026-10-06 审查修复后，v1.1.0）：

- **能力接口（ISP）**：厂商独有能力一律用独立接口表达，不得再加进基础接口——`ILinkStatusProbeProvider`（可达性探测）、`IBufferConfigurable`（采集缓冲配置）、`INamedCameraInfo`（本地改名）、`IConnectionMonitor`（掉线通知，1.2.0）。`ICameraProvider` 只保留所有厂商都必须支持的枚举/创建/分发。加新能力时新增接口，而不是新增成员。
- `CameraInterfaceType`（原 `CameraType`）表示设备物理接口类型（GigE/USB/CameraLink/GenTL），与品牌无关；枚举成员为 `All`（非 `ALL`）。
- `CameraLinkStatus` 五态且**默认值必须是 `Unknown`**：`Unknown=0`（未能判定）、`Connected=1`、`Idle=2`、`Occupied=3`（在线但被独占）、`Unreachable=4`（枚举不到/掉线）。禁止把任何"确定状态"排到 0。
- 帧流订阅者为 `CameraStreamSubscriber`（原 `CameraStreamSuber`），订阅参数名 `subscriberKey`（服务层与流层已统一）；同 Key 重复订阅为**原子替换**语义。
- 背压策略 `IStreamOptions.BackpressureMode`：`DropOldest`（默认）/ `RejectNewest`。**不提供"丢新帧"**——实测 .NET 的 `BoundedChannelFullMode.DropNewest` 与 `DropOldest` 行为一致，提供即名不副实。
- 丢帧可观测：`ICameraStream.Statistics` / `ICameraService.GetStreamStatistics(key)` 返回 `Published/Delivered/Dropped`（`FrameStreamStatistics`）。
- `ICamera.StopGrab()` 返回 `CameraResult`；`ICamera.LastError` 是厂商诊断的**唯一**出口（门面必须把它透传到 `CameraResult.Message`）。
- 取参：`TryGetParam<T>`/`TryGetEnumParam` 可区分"值恰为 default"与"获取失败"；泛型分发由 `ParameterReader` 统一，厂商只实现 `IParameterSource` 的 6 个原语（含 `double`）。`HikFrameWrapper.Data` 为懒缓存托管副本，宽高/步长/像素格式在构造时快照。
- `ICameraManager.Remove` 返回 `CameraRemoveStatus`（Removed/NotFound/ReleaseFailed），`LastError` 已提升到接口并在成功清理后清空。
- 非 DI 场景入口：`EasyCamera.Create(b => b.EnableHikVision()...)` 返回 `EasyCameraHost`（持有 `Sdk`/`Service`/`Options`，`Dispose` 幂等并按"相机→帧流→SDK"顺序释放）；Prism 等宿主容器以单例实例注册 `host.Sdk`/`host.Service`，勿注册为瞬态。
- SDK Initialize/Finalize 由 `HikCameraSdkSystem` 按实例引用计数管理（含 internal 测试 seam，经 `InternalsVisibleTo` 供无硬件单测使用）。
- 项目入口文档为根目录 `README.md`（现状/快速上手/推荐用法/链路状态）。
- 面向消费方 Agent 的包使用说明书位于 `skills/using-junevy-easycamera/SKILL.md`（可复制到任意 Agent 运行时的技能目录使用）；修改公共 API 后必须同步更新该文件。
- **IRayple 按"继续保留"处理**：类型仍标记 `[Obsolete("未开发完毕", true)]`、`EnableIRayple` 仍抛 `NotImplementedException`，代码保留但必须与海康遵守同一套并发纪律（已补齐 `operationGate`、回调排空、attach 幂等、`LastError`）。
- **`Close` 在 Closed 状态幂等**（1.1.2；1.2.0 扩展到从未打开的相机）：海康相机处于 Closed 状态（已关闭，或从未打开过）时再次 `Close` 直接成功，不做原生调用、不依赖 `device` 是否为 null，避免二次原生关闭失败误入回滚导致 `CameraManager.Remove` 报 `ReleaseFailed`。`CameraManager.DisposeCamera` 会对 Connect 失败的候选先调 `Close`，因此从未 `Connect` 过的相机同样返回成功（`ICamera.Close` 幂等语义，1.2.0 的有意变化）；`device == null` 时的 `Camera not initialized` 仅作防御保留。Disposed 状态仍报失败。
- **厂商 `Unknown` 是回退信号**（1.1.2）：`CameraService` 把 `ILinkStatusProbeProvider` 返回的 `CameraLinkStatus.Unknown` 视作"无法判定"，回退侵入式探测（`probe:{serial}`），不把它当作结论返回。`AggregateCameraProvider` 在无厂商具备能力时即返回 `Unknown`，因此生产路径依赖这一回退。
- **SDK 初始化返回码必须检查**（1.1.2）：`HikCameraSdkSystem.Initialize` 的原生返回码非 `MV_OK` 时回退引用计数并抛 `InvalidOperationException`，实例不持有引用。`Release` 中计数先于 Finalize 递减，`initialized` 在 `finally` 中清除，Finalize 抛异常不会二次递减；Finalize 返回非 `MV_OK` 只 `Trace.TraceWarning`，不抛。

测试工程约定：测试项目 `EnableDefaultCompileItems=false` + 显式 `Compile` 清单——**新增测试文件必须手工加入 csproj，否则不会被编译、更不会执行**（2026-10-06 曾因此让 6 个测试"绿色地缺席"）。验收时应核对"仓库内 `[TestMethod]` 总数 == 执行数"。包内容守卫（`PackageContentTests`）在找不到 nupkg 时断言为 Inconclusive，不产生假失败。

验收命令：

```powershell
dotnet build .\Junevy.EasyCamera.sln -c Release -v:minimal --no-incremental
dotnet test .\Junevy.EasyCamera.Tests\Junevy.EasyCamera.Tests.csproj -c Release --logger "console;verbosity=minimal"
```

验收基线（2026-10-09，1.2.0 验收修正后）：全量重建 0 错误 0 警告；net48 与 net8.0 各 149/149 通过（仓库 `[TestMethod]` 总数 149 = 执行数；1.1.2 时为 133/133；2026-10-06 时为 124/124）。

## 5. 知识库

本库的 Obsidian 知识库位于仓库内 `Junevy.EasyCamera.Wiki/`（随本仓库提交），入口 `知识库首页.md`，正文在 `zh/content/<主题>/`。用普通文件工具按路径访问（Read/Grep/Glob、shell），不要依赖 filesystem MCP：`@modelcontextprotocol/server-filesystem` 在客户端支持 roots 时会把允许目录替换成当前工作目录。

- **按需检索**：不在会话开始时通读首页或章节。先用 Grep 搜整个库目录（类型/方法名或主题词）定位笔记，再只读命中段落；定位不到时才看 `知识库首页.md` 的"按场景找笔记"。改并发/资源代码前必读 `并发与资源/并发纪律`，改动对照 `Agent协作/文档同步清单与任务配方`。
- **改完后，同一会话内回写**：公共 API/契约、并发纪律、打包规则、厂商适配或设计决策变化时，更新对应笔记（优先改已有笔记；新增笔记挂进 `知识库首页.md` 分类索引），并更新首页"版本与时效"中的版本号。与本文件第 3 节"同步 Skill / CHANGELOG / AGENTS.md"一起完成。
- **不自行提交**：写完运行 `git status --short -- Junevy.EasyCamera.Wiki` 与 `git diff --stat -- Junevy.EasyCamera.Wiki`，把结果放进汇报，由用户决定提交；代为提交时提交信息写明对应的代码改动或版本号。
- 权威顺序：代码 > 本文件 > `CHANGELOG.md` > `docs/` 审查报告 > 知识库笔记正文。
- 消费方（如 AutomationSystem）的会话只读本知识库；它们发现的不一致会在汇报中指出，由本仓库会话核实后修正。
