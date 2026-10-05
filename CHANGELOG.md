# CHANGELOG

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
