# CHANGELOG

## 2026-09-15

- 修复 HikVision SDK 初始化/释放状态机，支持多实例引用计数并允许初始化失败后重试。
- 修复 HikCamera 采集停止、回调在途计数、SDK 帧缓冲归还及销毁时序。
- 修复 CameraStream 订阅竞争、Dispose 竞态、有界队列淘汰和帧引用释放问题。
- 修复 CameraManager/CameraService 的同键打开竞争、清理结果反馈和销毁后访问问题。
- 补齐 HikVision GenTL 映射，修复测试项目配置并增加生命周期/并发回归测试。
- 本次修改范围不包含 `Junevy.EasyCamera/Vendors/IRayple` 实现。
