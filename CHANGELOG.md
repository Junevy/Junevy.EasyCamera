# CHANGELOG

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
