# IRayple现状

<cite>
**本文引用的文件**
- [Junevy.EasyCamera/Vendors/IRayple/IRaypleCamera.cs](file://Junevy.EasyCamera/Vendors/IRayple/IRaypleCamera.cs)
- [Junevy.EasyCamera/Vendors/IRayple/IRaypleFrameWrapper.cs](file://Junevy.EasyCamera/Vendors/IRayple/IRaypleFrameWrapper.cs)
- [Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraProvider.cs](file://Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraProvider.cs)
- [Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraInfo.cs](file://Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraInfo.cs)
- [Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraSdkSystem.cs](file://Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraSdkSystem.cs)
- [Junevy.EasyCamera/Extensions/ServiceCollectionExtensions.cs](file://Junevy.EasyCamera/Extensions/ServiceCollectionExtensions.cs)
- [Junevy.EasyCamera/Common/EasyCameraBuilder.cs](file://Junevy.EasyCamera/Common/EasyCameraBuilder.cs)
- [Junevy.EasyCamera/Junevy.EasyCamera.csproj](file://Junevy.EasyCamera/Junevy.EasyCamera.csproj)
- [README.md](file://README.md)
- [CHANGELOG.md](file://CHANGELOG.md)
</cite>

## 目录
1. [现状一句话](#现状一句话)
2. [五个类型](#五个类型)
3. [运行时不可达](#运行时不可达)
4. [已按海康纪律加固的部分](#已按海康纪律加固的部分)
5. [与海康的关键差异](#与海康的关键差异)
6. [遗留差距（待办）](#遗留差距待办)
7. [副作用：把 MVSDK_Net.dll 变成硬依赖](#副作用把-mvsdk_netdll-变成硬依赖)

## 现状一句话

华睿（IRayple）代码**保留但未完成**：全部类型标记 `[Obsolete("未开发完毕", true)]`（第二参数 `true` 表示标记本身即编译错误），DI 与 Builder 两条入口启用即抛 `NotImplementedException`，运行时完全不可达；1.1.0 只**部分**按海康纪律加固（生命周期路径的 `operationGate`、回调排空、同句柄只 attach 一次、`LastError`）。**参数/命令路径仍在 `operationGate` 之外、生命周期仍是多个 `int` 标志位、元数据未快照**，这三项与海康的差距见下文"遗留差距"——接手收尾时不能当作"并发已经安全"。

## 五个类型

| 类型 | 接口 | 备注 |
| --- | --- | --- |
| `IRaypleCamera` | `ICamera` + `IParameterSource` + `IBufferConfigurable` | 基于 `MVSDK_Net` 的 `MyCamera` |
| `IRaypleFrameWrapper` | `IFrame` | 构造时即复制像素为托管数组 |
| `IRaypleCameraProvider` | `IVendorCameraProvider` | 无任何能力接口 |
| `IRaypleCameraInfo` | `ICameraInfo` + `INamedCameraInfo` | 多一个 `CameraKey` |
| `IRaypleCameraSdkSystem` | `ICameraSdkSystem` | `Initialize` / `Release` 为空操作 |

## 运行时不可达

三处一致：

- `Junevy.EasyCamera/Extensions/ServiceCollectionExtensions.cs`：`RegisterIRayple()` 直接 `throw new NotImplementedException("IRayple camera support is not yet implemented.")`，与 Basler 一致。
- `Junevy.EasyCamera/Common/EasyCameraBuilder.cs`：`EnableIRayple()` 同样抛 `NotImplementedException`。
- 所有厂商类型本身带 `[Obsolete("未开发完毕", true)]`，外部项目连引用这些类型都编译不过。

`README.md` 状态表把华睿记为"未完成"，并注明启用即抛 `NotImplementedException`。

## 已按海康纪律加固的部分

1. `SemaphoreSlim operationGate = new(1, 1)` 串行化 `Connect` / `Close` / `StartGrab` / `StopGrab` / `Dispose`。
2. `string LastError` 诊断出口。
3. 释放前 `WaitForCallbacks()` 排空在途回调，`CallbackDrainTimeout = TimeSpan.FromSeconds(5)`。
4. **同一原生句柄只 `IMV_AttachGrabbing` 一次**（`callbackAttached` 标记）。此前每次 `StartGrab` 都重新绑定，导致每帧被多次发布；`ReleaseHandle` 销毁句柄时重置该标记。
5. `StopGrab()` 返回 `CameraResult`（与 `ICamera` 对齐），未在取流时直接返回成功（幂等）。
6. `IRaypleFrameWrapper.AddRef` 在已释放帧上抛 `ObjectDisposedException`；引用计数幂等，重复 `Dispose` 不压到负数。
7. `CheckWriteable` 先判 `camera == null` 再调 `IMV_FeatureIsAvailable` / `IMV_FeatureIsWriteable`，避免 NRE。

## 与海康的关键差异

| 维度 | 海康 `HikCamera` | 华睿 `IRaypleCamera` |
| --- | --- | --- |
| 生命周期状态 | 单一枚举 `CameraState`（5 态） | 仍是多个 `int` 标志位：`isOpen` / `isGrabbing` / `disposed`（配合 `Interlocked`） |
| 帧深拷贝时机 | 回调内 `frameOut.Clone()`，包装器持有非托管克隆 | 包装器构造时 `Marshal.Copy` 成托管 `byte[]` |
| 帧包装器终结器 | 有（`~HikFrameWrapper` 兜底非托管缓冲） | 无（无非托管缓冲可漏） |
| 元数据快照 | 构造时快照宽高/步长/格式 | 不快照，`Width` / `Height` / `PixelType` 每次读原生结构 |
| `GetBitmap` | 交 `native.Image.ToBitmap()` | 自绘，仅支持 `Mono8` 与 24bpp `RGB8`/`BGR8`，其余返回 `null` |
| `float` 参数写入 | `SetFloatValue` | 走 `IMV_SetDoubleFeatureValue`（SDK 只有 double 节点） |
| SDK 全局初始化 | 静态引用计数管理 `Initialize/Finalize` | 无全局接口，`Initialize` / `Release` 空操作 |

SDK 空操作这一点是**刻意**的：`IRaypleCameraSdkSystem` 存在的唯一目的是让 `CompositeCameraSdkSystem` 对所有厂商走同一条生命周期路径，因此它被移除了只写不读的 `isInitialized` 字段，`Initialize` / `Release` 保持空实现，`Dispose` 只置一个 `disposed` 标记。

`IRaypleCameraInfo.Native` 是 `IMV_DeviceInfo` **结构体**（不是类），因此**不能判 null**；`InterfaceType` 由 `nInterfaceType` 映射（`interfaceTypeGige`/`interfaceTypeUsb3`/`interfaceTypeCL`/`interfaceTypePCIe`）。`INamedCameraInfo.SetDefinedName` 与海康一致：只改本地副本，长度 ≤64。`IRaypleCameraProvider.Enumerate` 用 `Marshal.PtrToStructure` 逐个解析设备数组，单条解析失败返回 `null` 并跳过，不中断整个枚举。

## 遗留差距（待办）

| 项 | 现状 | 收尾方向 |
| --- | --- | --- |
| 参数写入路径未纳入 `operationGate` | `SetParam`×4 / `SetEnumParam` / `ExecuteCommand` / `TryGetParam` / `TryGetEnumParam` 直接调 `this.camera.*`，与 `Dispose` 并发有 NRE 风险 | 照海康收敛到统一守卫，异常翻译成 `CameraResult` |
| 无 `ILinkStatusProbeProvider` 实现 | provider 只实现 `IVendorCameraProvider`（`IBufferConfigurable` 在相机侧、`INamedCameraInfo` 在 info 侧已实现）。经聚合层后，对 IRayple 相机"没有厂商能探测"（`IProbeAvailability.CanProbe=false`），门面会回退到侵入式 `probe:{serial}` 探测（2026-10-06 修复前这里是 `Unknown` 且不回退，见 [[公共契约/能力接口与扩展点]]）。目前 IRayple 本身不可启用，该路径仅在补齐后生效 | SDK 若提供可达性查询则实现之（注意不信任传入的原生引用）；不实现则沿用侵入式回退（会短暂 Open/Close） |
| 单一状态枚举未落地 | 三个 `int` 标志位组合 | 与海康对齐，规避"标志位组合非法导致静默丢帧" |
| 元数据未快照 | 每次属性访问都读原生结构 | 构造时快照，消除 check-then-use 竞态 |
| 无测试覆盖 | `Junevy.EasyCamera.Tests` 中没有 `Vendors/IRayple*` 文件 | 需先给相机加构造 seam（类似海康的 `deviceFactory`）才能无硬件测试 |

> [!tip]
> `IRaypleCameraProvider` 早期带一个 `ProbeLinkStatus` 空桩，1.1.0 成员迁到 `ILinkStatusProbeProvider` 后**已移除**。这是正确方向：基础接口上不留空实现桩。

## 副作用：把 MVSDK_Net.dll 变成硬依赖

由于代码保留在发布包内，`Junevy.EasyCamera.csproj` 仍引用并打包 `MVSDK_Net.dll`——即使华睿运行时完全不可达，消费方仍会拿到这份 DLL。这是"继续保留"决策的已知代价，也是 [[构建与发布/包内容守卫]] 必须同时断言 `lib/*/MVSDK_Net.dll` 的原因。

**章节来源**
- [Junevy.EasyCamera/Vendors/IRayple/IRaypleCamera.cs](file://Junevy.EasyCamera/Vendors/IRayple/IRaypleCamera.cs)
- [Junevy.EasyCamera/Vendors/IRayple/IRaypleFrameWrapper.cs](file://Junevy.EasyCamera/Vendors/IRayple/IRaypleFrameWrapper.cs)
- [Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraInfo.cs](file://Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraInfo.cs)
- [Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraSdkSystem.cs](file://Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraSdkSystem.cs)
- [Junevy.EasyCamera/Extensions/ServiceCollectionExtensions.cs](file://Junevy.EasyCamera/Extensions/ServiceCollectionExtensions.cs)
- [Junevy.EasyCamera/Junevy.EasyCamera.csproj](file://Junevy.EasyCamera/Junevy.EasyCamera.csproj)
- [README.md](file://README.md)
- [CHANGELOG.md](file://CHANGELOG.md)
