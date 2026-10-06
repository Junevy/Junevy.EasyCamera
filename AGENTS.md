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
- 包版本由根目录 `Directory.Packages.props`（中央包管理）统一维护；net48 通过条件引用补充 BCL 兼容包，net8.0 使用 `System.Drawing.Common`。
- 两个类库在 net8.0 下声明程序集级 `SupportedOSPlatform("windows")`（工业相机 SDK 场景仅 Windows/x64），声明位置见上方并发纪律最后一条。
- 根目录 `global.json` 固定 SDK 9.0.316，保证构建/测试可复现。
- IRayple 厂商标记为 `[Obsolete("未开发完毕", true)]`：`ServiceCollectionExtensions` 不注册其服务，启用 `EnableIRayple` 时与 Basler 一致抛出 `NotImplementedException`。

HikVision 与公共层的资源所有权约束：SDK 回调帧必须在回调内归还；发布给订阅者的帧必须是对 SDK 缓冲的**独立克隆——每帧仅在相机回调中克隆一次**，多订阅者通过引用计数**浅共享**同一克隆（不逐订阅者深拷贝，订阅者为只读消费者）；克隆缓冲由**最后一个归零的引用持有者**物理释放（不得固定为发布者，否则其余浅引用悬空）。相机关闭/销毁前会停止取流、解绑回调并等待在途回调结束（5s 上限）。`CameraStream` 使用非阻塞有界队列，队列淘汰的帧必须释放。

并发纪律（2026-10-06 审查后确立，新增厂商必须遵守）：

- **一切原生访问必须经厂商相机的 `operationGate`**（海康 `ExecuteGuarded`/`TryExecuteGuarded`/`ExecuteDispose` 三个守卫收敛：互斥 + 异常翻译成 `CameraResult` + `LastError` 诊断）。唯一例外是 SDK 采集回调线程，它只读状态并归还缓冲区。
- **生命周期用单一状态枚举表达**（海康 `CameraState`：`Closed/Open/Grabbing/Closing/Disposed`），不得再引入并行布尔标志位；`Publish` 判定只看状态。任何失败路径（含原生异常）必须回滚到进入前的状态，否则会出现"连接还在但一帧不发"的静默丢帧。
- **门面按 cameraKey 串行**：`CameraService` 的 `OpenCamera/Close/StartGrab/StopGrab/SetTrigger` 全部走同一把 `GetKeyLock(cameraKey)`；新增写操作必须并入，否则会重现 use-after-free 竞态。
- **`CameraStream.operationLock` 只保护订阅者注册表**：`AddRef`/`TryWrite`/背压淘汰/帧释放/用户异常回调一律在锁外执行。
- **`SupportedOSPlatform("windows")` 必须写在 `Properties/PlatformCompatibility.cs`**（`#if NET8_0_OR_GREATER`）：两个类库都设了 `GenerateAssemblyInfo=false`，csproj 的 `<AssemblyAttribute>` 项会被连带跳过，平台标注静默失效。

公共契约要点（2026-10-06 审查修复后，v1.1.0）：

- **能力接口（ISP）**：厂商独有能力一律用独立接口表达，不得再加进基础接口——`ILinkStatusProbeProvider`（可达性探测）、`IBufferConfigurable`（采集缓冲配置）、`INamedCameraInfo`（本地改名）。`ICameraProvider` 只保留所有厂商都必须支持的枚举/创建/分发。加新能力时新增接口，而不是新增成员。
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

测试工程约定：测试项目 `EnableDefaultCompileItems=false` + 显式 `Compile` 清单——**新增测试文件必须手工加入 csproj，否则不会被编译、更不会执行**（2026-10-06 曾因此让 6 个测试"绿色地缺席"）。验收时应核对"仓库内 `[TestMethod]` 总数 == 执行数"。

验收命令：

```powershell
dotnet build .\Junevy.EasyCamera.sln -c Release -v:minimal --no-incremental
dotnet test .\Junevy.EasyCamera.Tests\Junevy.EasyCamera.Tests.csproj -c Release --logger "console;verbosity=minimal"
```

验收基线（2026-10-06）：全量重建 0 错误 0 警告；net48 与 net8.0 各 122/122 通过。
