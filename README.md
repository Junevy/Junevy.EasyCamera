# Junevy.EasyCamera

工业相机统一操作类库：以一套抽象封装不同品牌工业相机 SDK，便于第三方项目快速接入相机能力、通过接口无缝更换相机硬件、通过 Channel 管理相机帧流。

## 当前状况

| 品牌 | 状态 | 说明 |
|---|---|---|
| 海康 HikVision | ✅ 可用 | 基于 `MvCameraControl.Net`，含完整生命周期/并发回归测试 |
| 华睿 IRayple | 🚧 未完成 | 类型标记 `[Obsolete("未开发完毕", true)]`，DI 启用即抛 `NotImplementedException` |
| Basler | 📋 预留 | DI 启用即抛 `NotImplementedException`，待接入 |
| Cognex | 📋 规划中 | 预留扩展点 |

- 双目标框架：`net48` + `net8.0`（net8.0 下声明 Windows 平台）；运行时须 **x64**（厂商 SDK 为 AMD64 专用）。
- 解决方案结构：`Junevy.EasyCamera.Core`（抽象与契约）/ `Junevy.EasyCamera`（DI 与厂商适配）/ `Junevy.EasyCamera.Tests`（x64 测试宿主）。
- 厂商 SDK DLL 位于仓库 `Libs/`，构建自包含；.NET SDK 版本由 `global.json`（9.0.316）固定，包版本由 `Directory.Packages.props` 中央管理。
- 详见 [AGENTS.md](AGENTS.md)（项目开发约定）与 [docs/](docs/)（审查/计划文档）。

## 构建

```powershell
dotnet build .\Junevy.EasyCamera\Junevy.EasyCamera.csproj -c Release -v:minimal
dotnet test .\Junevy.EasyCamera.Tests\Junevy.EasyCamera.Tests.csproj -c Release --logger "console;verbosity=minimal"
```

## 推荐使用方式（依赖注入）

```csharp
// 1. 注册（Host/ServiceProvider 场景）
services.AddEasyCamera(
    options => options.EnableHikVision = true,
    stream => stream.StreamCapacity = 5);   // 每个订阅者的有界帧缓存（帧数）

// 2. 解析并初始化 SDK（进程级一次；内部按实例引用计数，Finalize 在引用归零时执行）
var sdk = provider.GetRequiredService<ICameraSdkSystem>();
sdk.Initialize();
try
{
    var cameraService = provider.GetRequiredService<ICameraService>();

    // 3. 枚举并打开相机
    var info = cameraService.EnumerateCameras().First();
    cameraService.OpenCamera(info, cameraKey: "cam-1");

    // 4. 订阅帧流（capacity <= 0 时使用 StreamOptions.StreamCapacity）
    cameraService.SubscribeFrameStream("cam-1", "my-subscriber",
        (cameraKey, frame) =>
        {
            using (frame)                       // 帧必须 Dispose：引用计数归零即释放非托管缓冲
            {
                var data = frame.Data;          // 托管副本（安全）
                // 处理图像……需要跨线程保帧时先 frame.AddRef()，用完对应 Dispose()
            }
            return Task.CompletedTask;
        },
        whenException: ex => { /* 不提供时 handler 异常将终止该订阅并自动摘除 */ });

    // 5. 开始/停止取流、参数与触发
    cameraService.StartGrab("cam-1");
    cameraService.SetParam("cam-1", "ExposureTime", 5000f);

    // 取参推荐 TryGetParam：可区分"值恰为 default"与"获取失败"
    if (cameraService.TryGetParam<int>("cam-1", "Width", out var width)) { /* ... */ }

    cameraService.StopGrab("cam-1");
    cameraService.SetTrigger("cam-1", "Line1", enableTrigger: true); // 设置后需重新 StartGrab
    cameraService.StartGrab("cam-1");

    // 6. 关闭相机（帧流保留，重开同名 key 后订阅自动继续生效）
    cameraService.Close("cam-1");
}
finally
{
    sdk.Dispose();
}
```

## 简易使用（Builder，不依赖 DI）

```csharp
using Junevy.EasyCamera;

using var host = EasyCamera.Create(b => b.EnableHikVision()
                                             .WithStreamOptions(o => o.StreamCapacity = 5));
host.Sdk.Initialize();
// host.Service 用法与 DI 方式的 3–6 步相同
host.Dispose();   // 幂等；释放顺序：相机 → 帧流 → SDK
```

### Prism 等自带容器框架

把 `host.Sdk` / `host.Service` 以**单例实例**注册进宿主容器（`IContainerRegistry.RegisterInstance`，切勿注册成瞬态），之后 ViewModel 构造函数注入 `ICameraService` 照常工作；完整模式见 [skills/using-junevy-easycamera/SKILL.md](skills/using-junevy-easycamera/SKILL.md)。若你的 Prism 底层本就是 Microsoft.Extensions.DependencyInjection（官方适配包），直接 `services.AddEasyCamera(...)` 即可。

## 帧与资源管理约定（重要）

- **每帧仅在相机回调中克隆一次**：SDK 回调帧在回调内归还采集队列，订阅者拿到的帧是对 SDK 缓冲的独立克隆。
- **分发为引用计数浅共享**：`Publish` 对每个订阅者只做 `AddRef + TryWrite` 同一实例，不逐订阅者深拷贝；订阅者为只读消费者，禁止修改帧数据。
- 帧引用计数初始为 1（发布方初始引用，由流持有并释放），每入队一个订阅者 +1；**引用归零时由最后一个释放者回收非托管缓冲**，且仅回收一次。
- 需要异步保帧：在 handler 返回前调用 `frame.AddRef()`，使用完成后对应 `Dispose()`。
- 优先使用 `frame.Data`（懒缓存托管副本，已释放帧仍可安全读取）；`frame.PixelDataPtr` 在帧释放后即失效。
- 订阅 Channel 为有界队列（DropOldest）：消费慢时旧帧被淘汰并立即释放，不阻塞采集、不压垮上游。
- 同 Key 重复订阅会**原子替换**旧订阅者；未提供 `whenException` 时，handler 异常会终止该订阅并自动摘除（帧仍会释放）。

## 路线图

- Basler（Pylon）适配、IRayple 补齐（同步模型、回调排空、帧引用防护）
- Cognex 适配调研
- 线阵相机扩展接口 `ILineScanCamera` 落地（行触发、行计数）
