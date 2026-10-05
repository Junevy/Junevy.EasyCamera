# 2026-10-05 审查问题修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 `docs/代码审查报告-2026-10-05.md` 中的全部隐藏 Bug、设计问题与命名问题，并补齐 README、CHANGELOG、AGENTS.md。

**Architecture:** 分四批推进：批次 A（Task 1–9）为非破坏性正确性修复，每项带回归测试；批次 B（Task 10–11）为文档与文本修正；批次 C（Task 12–13）为需用户确认的可扩展 API 与破坏性命名重命名；批次 D（Task 14–15）为 README 与交接文档。任务间依赖：Task 2 建立 `InternalsVisibleTo` 供 Task 8 使用；Task 5 重构 `CameraStreamSuber` 结构供 Task 13 重命名；Task 6/4 引入的 `CameraRemoveStatus`/锁结构被 Task 13 复用。

**Tech Stack:** .NET SDK 风格 csproj（net48 + net8.0 双目标）、MSTest 3.6.4、System.Threading.Channels、海康 MvCameraControl.Net / 华睿 MVSDK_Net（Libs/ 自包含）。

**Spec:** `docs/代码审查报告-2026-10-05.md`（本计划所有条目均引用该报告编号，如 一.1）。另须遵守 `AGENTS.md` 全部约定（非托管资源红线、CHANGELOG 记录、AGENTS.md 第 4 项更新）。

## 待用户确认的决策点（默认值已写入计划，审阅时可改）

| 编号 | 决策 | 计划默认值 | 备选 |
|---|---|---|---|
| D1 | 同 key 重复订阅语义（一.5） | **原子替换旧订阅者**（测试消息本意即"替换"） | 报错 / 返回 false |
| D2 | 是否新增 `TryGetParam<T>`（二.1） | **新增**（加法扩展，不破坏调用方） | 不新增 |
| D3 | 公共 API 命名重命名（三.1–三.4） | **本版直接改**（v1.0.0 尚未对外发布） | `[Obsolete]` 旧名过渡 / 暂缓 |
| D4 | `StartGrab` 是否改幂等（一.9） | **改为幂等**（与 StopGrab 一致） | 保持报错 |
| D5 | IRayple 同步/回调排空债务（一.9） | **暂缓**（保持 Obsolete，完成时一并开发） | 现在补齐 |

## Global Constraints

- 目标框架 `net48;net8.0`，`LangVersion 13`，`PlatformTarget x64`，`global.json`（SDK 9.0.316）不动。
- 不新增 NuGet 包；`Directory.Packages.props` 不动；`Libs/` 厂商 DLL 引用不动。
- IRayple 全部类型保持 `[Obsolete("未开发完毕", true)]`，DI 启用时抛 `NotImplementedException`；本计划不改 IRayple 的同步模型（D5 暂缓）。
- 资源红线（AGENTS.md）：SDK 回调帧必须在回调内归还；发布给订阅者的帧必须是对 SDK 缓冲的**独立克隆——每帧仅在相机回调中克隆一次**，多订阅者通过引用计数**浅共享**同一克隆（不逐订阅者深拷贝，订阅者为只读消费者）；队列淘汰的帧必须释放；克隆缓冲由**最后一个归零的引用持有者**负责物理释放（不得固定为发布者，否则其余浅引用悬空）。
- 每个任务完成标准：`dotnet build Junevy.EasyCamera.sln -c Release` 0 错误 + `dotnet test` 全绿（基线 63 用例 + 新增）→ 在 `CHANGELOG.md` 顶部条目追加一行该任务摘要 → `git commit`。
- 公共 API 变更仅限 Task 12/13 明确列出项；其余任务保持源码兼容。
- 提交信息沿用仓库现状风格：`Fix:` / `Docs:` / `Refactor:` 前缀。

## Review Focus（最可能咬人的五类输入，均已落测试）

1. 已释放 `StreamManager` 后重开相机 → 必须抛 `ObjectDisposedException` 而非复活流（Task 1 测试）。
2. SDK `Initialize`/`Release` 并发交叉 → 全部 Dispose 后 init 计数 == finalize 计数（Task 2 测试）。
3. 未 `Initialize` 的实例调用 `Release` → 不得削减他人引用导致 SDK 提前 Finalize（Task 2 测试）。
4. 同 key 重复/并发订阅 → 按 D1 语义原子生效且旧订阅者资源被清理（Task 5 测试）。
5. 同 key 并发 `OpenCamera`/`StartGrab`/`StopGrab`/`SetTrigger` → 每 key 串行、单实例（既有并发测试保留 + Task 6 收口）。

---

## 批次 A：正确性修复（非破坏性）

### Task 1: StreamManager 的 disposed 防护（审查 一.1）

**Files:**
- Modify: `Junevy.EasyCamera/Common/StreamManager.cs:48-54,84-104`
- Create: `Junevy.EasyCamera.Tests/Common/StreamManagerTests.cs`
- Modify: `Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj`（Compile 列表追加新测试文件）

**Interfaces:**
- Produces: `StreamManager.GetOrCreateStream` 在 Dispose 后抛 `ObjectDisposedException`；`RemoveStream` 在 Dispose 后返回 false。签名不变。

- [ ] **Step 1: 新建测试文件并注册到测试 csproj**

```csharp
// Junevy.EasyCamera.Tests/Common/StreamManagerTests.cs
using Junevy.EasyCamera.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Junevy.EasyCamera.Tests.Common
{
    [TestClass]
    public class StreamManagerTests
    {
        [TestMethod]
        public void GetOrCreateStream_AfterDispose_ShouldThrowObjectDisposed()
        {
            var manager = new StreamManager();
            manager.Dispose();

            Assert.ThrowsException<ObjectDisposedException>(() => manager.GetOrCreateStream("SN001"));
        }

        [TestMethod]
        public void RemoveStream_AfterDispose_ShouldReturnFalse()
        {
            var manager = new StreamManager();
            manager.GetOrCreateStream("SN001");
            manager.Dispose();

            Assert.IsFalse(manager.RemoveStream("SN001"));
        }

        [TestMethod]
        public void GetOrCreateStream_RacingDispose_ShouldNotLeakStream()
        {
            // Dispose 与 GetOrCreate 竞态时，新 created 的流必须被就地释放而不是滞留字典外
            var manager = new StreamManager();

            Assert.ThrowsException<ObjectDisposedException>(() =>
            {
                manager.Dispose();
                manager.GetOrCreateStream("SN001");
            });
        }
    }
}
```

在 `Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj` 的 `<Compile Include="Common\CameraManagerServiceRegressionTests.cs" />` 之后追加：

```xml
    <Compile Include="Common\StreamManagerTests.cs" />
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj -c Release --filter StreamManagerTests`
Expected: `GetOrCreateStream_AfterDispose_ShouldThrowObjectDisposed` FAIL（当前不抛异常）

- [ ] **Step 3: 实现（含 GetOrAdd 与 Dispose 竞态的二次校验）**

```csharp
// StreamManager.cs — GetOrCreateStream 替换为：
public ICameraStream GetOrCreateStream(string userDefinedName)
{
    if (string.IsNullOrEmpty(userDefinedName))
        throw new ArgumentNullException(nameof(userDefinedName));

    var stream = streams.GetOrAdd(userDefinedName, _ => new CameraStream(userDefinedName));

    // 与 Dispose 竞态时，字典外的新建流必须就地释放，避免无人持有的流常驻
    if (Volatile.Read(ref this.disposed) == 1)
    {
        stream.Dispose();
        throw new ObjectDisposedException(nameof(StreamManager));
    }

    return stream;
}
```

`RemoveStream` 在 `if (string.IsNullOrEmpty(...))` 之后追加：

```csharp
    if (Volatile.Read(ref this.disposed) == 1)
        return false;
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj -c Release`
Expected: 全部 PASS

- [ ] **Step 5: CHANGELOG 追加一行 + 提交**

```bash
git add Junevy.EasyCamera/Common/StreamManager.cs Junevy.EasyCamera.Tests/Common/StreamManagerTests.cs Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj CHANGELOG.md
git commit -m "Fix: StreamManager 释放后拒绝 GetOrCreateStream/RemoveStream（补齐 M3 声明修复）"
```

### Task 2: HikCameraSdkSystem 真幂等 + Initialize/Release 竞态封死（审查 一.2、一.3）

**Files:**
- Modify: `Junevy.EasyCamera/Vendors/HikVision/HikCameraSdkSystem.cs`（整体重写）
- Modify: `Junevy.EasyCamera/Properties/AssemblyInfo.cs`（追加 InternalsVisibleTo）
- Create: `Junevy.EasyCamera.Tests/Vendors/HikCameraSdkSystemTests.cs`
- Modify: `Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj`（Compile 列表追加）

**Interfaces:**
- Produces: `HikCameraSdkSystem` 公共签名不变；新增 internal 测试 seam `SdkInitializeAction` / `SdkFinalizeAction`（`System.Action`，默认指向 `SDKSystem.Initialize` / `SDKSystem.Finalize`）。`InternalsVisibleTo("Junevy.EasyCamera.Tests")` 同时供 Task 8 使用。

- [ ] **Step 1: AssemblyInfo 追加 InternalsVisibleTo**

```csharp
// Junevy.EasyCamera/Properties/AssemblyInfo.cs 末尾追加
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Junevy.EasyCamera.Tests")]
```

- [ ] **Step 2: 新建测试文件并注册到 csproj**

```csharp
// Junevy.EasyCamera.Tests/Vendors/HikCameraSdkSystemTests.cs
using Junevy.EasyCamera.Vendors.HikVision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Tests.Vendors.HikVision
{
    [TestClass]
    public class HikCameraSdkSystemTests
    {
        private int initCalls;
        private int finalizeCalls;
        private Action originalInitialize;
        private Action originalFinalize;

        [TestInitialize]
        public void ResetSeam()
        {
            this.initCalls = 0;
            this.finalizeCalls = 0;
            this.originalInitialize = HikCameraSdkSystem.SdkInitializeAction;
            this.originalFinalize = HikCameraSdkSystem.SdkFinalizeAction;
            HikCameraSdkSystem.SdkInitializeAction = () => Interlocked.Increment(ref this.initCalls);
            HikCameraSdkSystem.SdkFinalizeAction = () => Interlocked.Increment(ref this.finalizeCalls);
        }

        [TestCleanup]
        public void RestoreSeam()
        {
            // 恢复为真实 SDK 委托（仅赋值不调用），避免影响其他测试类
            HikCameraSdkSystem.SdkInitializeAction = this.originalInitialize;
            HikCameraSdkSystem.SdkFinalizeAction = this.originalFinalize;
        }

        [TestMethod]
        public void Initialize_Twice_HoldsSingleReference()
        {
            var system = new HikCameraSdkSystem();
            system.Initialize();
            system.Initialize();
            system.Dispose();

            Assert.AreEqual(1, this.initCalls, "同一实例重复 Initialize 必须只持有一个全局引用");
            Assert.AreEqual(1, this.finalizeCalls);
        }

        [TestMethod]
        public void Release_WithoutInitialize_DoesNotStealOtherInstanceReference()
        {
            var first = new HikCameraSdkSystem();
            var second = new HikCameraSdkSystem();

            first.Initialize();
            second.Release();   // 未初始化实例不得削减他人引用
            second.Dispose();

            Assert.AreEqual(1, this.initCalls);
            Assert.AreEqual(0, this.finalizeCalls, "SDK 仍被 first 持有，不得被 Finalize");

            first.Dispose();
            Assert.AreEqual(1, this.finalizeCalls);
        }

        [TestMethod]
        public void Initialize_AfterDispose_ShouldBeNoOp()
        {
            var system = new HikCameraSdkSystem();
            system.Dispose();
            system.Initialize();

            Assert.AreEqual(0, this.initCalls);
        }

        [TestMethod]
        public async Task ConcurrentInitializeAndRelease_EveryInitEventuallyFinalized()
        {
            for (var round = 0; round < 200; round++)
            {
                var first = new HikCameraSdkSystem();
                var second = new HikCameraSdkSystem();

                first.Initialize();
                await Task.WhenAll(
                    Task.Run(second.Initialize),
                    Task.Run(first.Release));

                first.Dispose();
                second.Dispose();
            }

            Assert.AreEqual(this.initCalls, this.finalizeCalls,
                "全部实例 Dispose 后，每次 SDK 初始化必须恰好对应一次 Finalize");
        }
    }
}
```

csproj Compile 列表追加：

```xml
    <Compile Include="Vendors\HikCameraSdkSystemTests.cs" />
```

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj -c Release --filter HikCameraSdkSystemTests`
Expected: FAIL（`SdkInitializeAction` 不存在导致编译错误，即"失败"）

- [ ] **Step 4: 重写 HikCameraSdkSystem**

```csharp
// Junevy.EasyCamera/Vendors/HikVision/HikCameraSdkSystem.cs
using Junevy.EasyCamera.Core.Abstractions;
using MvCameraControl;
using System;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康相机SDK系统。
    /// SDK 的 Initialize/Finalize 是进程级全局操作，本类以全局引用计数管理：
    /// 每个实例无论 Initialize 几次都只持有一个引用（真正的按实例幂等），
    /// 引用计数归零时才真正 Finalize。全部计数转移在全局锁内完成，
    /// 消除"并发 Initialize 读到 0、Finalize 随后执行"的交叉窗口。
    /// </summary>
    public class HikCameraSdkSystem : ICameraSdkSystem
    {
        /// <summary>
        /// 全局初始化引用计数，归零时才真正释放SDK
        /// </summary>
        private static int initRefCount;

        /// <summary>串行化全局引用计数与 Initialize/Finalize 的转移</summary>
        private static readonly object refCountLock = new();

        /// <summary>本实例生命周期锁（初始化/释放/销毁互斥）</summary>
        private readonly object instanceLock = new();

        /// <summary>本实例是否已持有全局引用（按实例幂等的依据）</summary>
        private bool initialized;

        /// <summary>本实例是否已销毁</summary>
        private bool disposed;

        /// <summary>
        /// 测试 seam：默认指向真实 SDK。仅测试可通过 InternalsVisibleTo 替换，
        /// 避免回归测试依赖相机运行时。
        /// </summary>
        internal static Action SdkInitializeAction = SDKSystem.Initialize;

        /// <summary>测试 seam，见 <see cref="SdkInitializeAction" />。</summary>
        internal static Action SdkFinalizeAction = SDKSystem.Finalize;

        /// <summary>
        /// 初始化相机SDK（按实例幂等：重复调用只持有一个全局引用）
        /// </summary>
        public void Initialize()
        {
            if (Volatile.Read(ref this.disposed))
                return;

            lock (this.instanceLock)
            {
                if (this.disposed || this.initialized)
                    return;

                lock (refCountLock)
                {
                    if (Interlocked.Increment(ref initRefCount) == 1)
                    {
                        try
                        {
                            SdkInitializeAction();
                        }
                        catch
                        {
                            // 初始化失败时回退引用计数，避免留下无法释放的悬挂引用
                            Interlocked.Decrement(ref initRefCount);
                            throw;
                        }
                    }
                }

                this.initialized = true;
            }
        }

        /// <summary>
        /// 释放本实例持有的SDK引用；未 Initialize 的实例不削减任何引用。
        /// 全局引用计数归零时才真正 Finalize SDK（幂等，可重复调用）
        /// </summary>
        public void Release()
        {
            lock (this.instanceLock)
            {
                if (!this.initialized)
                    return;

                lock (refCountLock)
                {
                    if (Interlocked.Decrement(ref initRefCount) == 0)
                        SdkFinalizeAction();
                }

                this.initialized = false;
            }
        }

        /// <summary>
        /// 释放相机SDK资源（幂等，可重复调用）
        /// </summary>
        public void Dispose()
        {
            lock (this.instanceLock)
            {
                if (this.disposed)
                    return;

                this.disposed = true;
            }

            this.Release();
        }
    }
}
```

锁序说明（写入代码注释）：`instanceLock → refCountLock` 固定顺序，多实例间无反向持锁路径，无死锁；Finalize 只在 `refCountLock` 内且计数真实归零时执行，并发 Initialize 必须先取得 `refCountLock`，窗口封死。

- [ ] **Step 5: 运行测试确认通过（全量）**

Run: `dotnet test Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj -c Release`
Expected: 全部 PASS

- [ ] **Step 6: CHANGELOG 追加 + 提交**

```bash
git add Junevy.EasyCamera/Vendors/HikVision/HikCameraSdkSystem.cs Junevy.EasyCamera/Properties/AssemblyInfo.cs Junevy.EasyCamera.Tests/Vendors/HikCameraSdkSystemTests.cs Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj CHANGELOG.md
git commit -m "Fix: HikCameraSdkSystem 按实例幂等并锁内转移引用计数，封死 Initialize/Release 交叉竞态"
```

### Task 3: CompositeCameraSdkSystem 初始化/释放异常隔离（审查 一.7）

**Files:**
- Modify: `Junevy.EasyCamera/Common/CompositeCameraSdkSystem.cs:29-44`
- Modify: `Junevy.EasyCamera.Tests/Common/CompositeCameraSdkSystemTests.cs`

**Interfaces:**
- Produces: `Initialize`/`Release` 遍历全部子系统后，若有失败则抛 `System.AggregateException`（不中断其余子系统）；行为对调用方可感知。

- [ ] **Step 1: 追加失败测试**

```csharp
// CompositeCameraSdkSystemTests.cs 追加
[TestMethod]
public void Initialize_WhenOneThrows_ShouldStillInitializeOthers()
{
    var throwing = new FakeSdkSystem("throwing") { ThrowOnInitialize = true };
    var healthy = new FakeSdkSystem("healthy");

    var composite = new CompositeCameraSdkSystem(new ICameraSdkSystem[] { throwing, healthy });

    Assert.ThrowsException<AggregateException>(() => composite.Initialize());
    Assert.IsTrue(healthy.CallLog.Contains("healthy:init"), "单个SDK初始化失败不应中断其余SDK");
}

[TestMethod]
public void Release_WhenOneThrows_ShouldStillReleaseOthers()
{
    var throwing = new FakeSdkSystem("throwing") { ThrowOnRelease = true };
    var healthy = new FakeSdkSystem("healthy");

    var composite = new CompositeCameraSdkSystem(new ICameraSdkSystem[] { throwing, healthy });
    composite.Initialize();
    Assert.ThrowsException<AggregateException>(() => composite.Release());
    Assert.IsTrue(healthy.CallLog.Contains("healthy:release"), "单个SDK释放失败不应中断其余SDK");
}
```

`FakeSdkSystem` 增加成员：

```csharp
public bool ThrowOnInitialize { get; set; }
public bool ThrowOnRelease { get; set; }

public void Initialize()
{
    if (this.ThrowOnInitialize)
        throw new InvalidOperationException("initialize failed");
    this.calls.Add(this.Name + ":init");
}

public void Release()
{
    if (this.ThrowOnRelease)
        throw new InvalidOperationException("release failed");
    this.calls.Add(this.Name + ":release");
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test ... --filter CompositeCameraSdkSystemTests`
Expected: 新增两用例 FAIL

- [ ] **Step 3: 实现**

```csharp
public void Initialize()
{
    var errors = new List<Exception>();
    foreach (var system in this.systems)
    {
        try
        {
            system.Initialize();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    if (errors.Count > 0)
        throw new AggregateException("One or more camera SDK systems failed to initialize.", errors);
}

public void Release()
{
    var errors = new List<Exception>();
    foreach (var system in this.systems)
    {
        try
        {
            system.Release();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    if (errors.Count > 0)
        throw new AggregateException("One or more camera SDK systems failed to release.", errors);
}
```

- [ ] **Step 4: 全量测试 + CHANGELOG + 提交**

```bash
dotnet test Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj -c Release
git add -A && git commit -m "Fix: CompositeCameraSdkSystem 初始化/释放失败隔离为 AggregateException，不中断其余厂商"
```

### Task 4: CameraManager 移除状态可区分 + LastError 不陈旧 + Close 去具体类型嗅探（审查 一.4）

**Files:**
- Create: `Junevy.EasyCamera.Core/Common/CameraRemoveStatus.cs`
- Modify: `Junevy.EasyCamera.Core/Common/ICameraManager.cs`
- Modify: `Junevy.EasyCamera/Common/CameraManager.cs`
- Modify: `Junevy.EasyCamera/Common/CameraService.cs`（Close 与 OpenCamera 两处 TryRemove 调用）
- Modify: `Junevy.EasyCamera.Tests/Common/CameraManagerServiceRegressionTests.cs`

**Interfaces:**
- Produces: 新枚举 `Junevy.EasyCamera.Core.Common.CameraRemoveStatus { Removed = 0, NotFound = 1, ReleaseFailed = 2 }`；`ICameraManager.Remove(string cameraKey)` 替代 `TryRemove`（唯一实现方 CameraManager，内部契约调整）；`CameraService.Close` 按状态返回精确错误；`CameraManager.LastError` 在成功清理后清空。

- [ ] **Step 1: 更新/新增测试**

```csharp
// CameraManagerServiceRegressionTests.cs：
// 原 CameraManager_TryRemove_CloseFailure_ReturnsFalseButDisposesAndRemoves 改写为：
[TestMethod]
public void CameraManager_Remove_CloseFailure_ReturnsReleaseFailedButDisposesAndRemoves()
{
    var manager = new CameraManager();
    var camera = new TrackingCamera { CloseResult = CameraResult.Fail(-9, "close failed") };
    manager.TryRegister("SN001", camera);

    var status = manager.Remove("SN001");

    Assert.AreEqual(CameraRemoveStatus.ReleaseFailed, status);
    Assert.IsTrue(camera.IsDisposed);
    Assert.IsFalse(manager.TryGet("SN001", out _));
    StringAssert.Contains(manager.LastError ?? string.Empty, "close failed");
}

[TestMethod]
public void CameraManager_Remove_MissingKey_ReturnsNotFound()
{
    var manager = new CameraManager();

    Assert.AreEqual(CameraRemoveStatus.NotFound, manager.Remove("missing"));
}

[TestMethod]
public void CameraManager_Remove_AfterFailedRemove_SucceedingRemoveClearsLastError()
{
    var manager = new CameraManager();
    var badCamera = new TrackingCamera { CloseResult = CameraResult.Fail(-9, "close failed") };
    manager.TryRegister("bad", badCamera);
    manager.Remove("bad");

    var goodCamera = new TrackingCamera();
    manager.TryRegister("good", goodCamera);

    Assert.AreEqual(CameraRemoveStatus.Removed, manager.Remove("good"));
    Assert.IsNull(manager.LastError, "成功清理后 LastError 必须清空，避免陈旧错误误导后续 Close");
}

[TestMethod]
public void CameraService_Close_UnknownKey_ReportsNotFound()
{
    var provider = new TrackingProvider();
    var manager = new CameraManager();
    using var streams = new StreamManager();
    var service = new CameraService(provider, manager, streams);

    var result = service.Close("missing-key");

    Assert.IsFalse(result.IsSuccess);
    StringAssert.Contains(result.Message, "not open or found");
}
```

- [ ] **Step 2: 运行确认失败（编译错误即失败）**

- [ ] **Step 3: 实现**

```csharp
// Junevy.EasyCamera.Core/Common/CameraRemoveStatus.cs
namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机从缓存移除的结果状态
    /// </summary>
    public enum CameraRemoveStatus
    {
        /// <summary>已移除并成功释放</summary>
        Removed = 0,

        /// <summary>相机Key不存在（或管理器已释放）</summary>
        NotFound = 1,

        /// <summary>已移除，但关闭或释放失败（诊断见 ICameraManager 实现的 LastError）</summary>
        ReleaseFailed = 2
    }
}
```

`ICameraManager`：`bool TryRemove(string cameraKey)` 替换为：

```csharp
/// <summary>
/// 移除并释放相机实例
/// </summary>
/// <param name="cameraKey">相机序列号</param>
/// <returns>移除结果状态；<see cref="CameraRemoveStatus.ReleaseFailed"/> 时诊断见实现的 LastError</returns>
/// <exception cref="ArgumentNullException"><c>cameraKey</c> 为 <c>null</c></exception>
CameraRemoveStatus Remove(string cameraKey);
```

`CameraManager`：

```csharp
public CameraRemoveStatus Remove(string cameraKey)
{
    if (string.IsNullOrEmpty(cameraKey))
        throw new ArgumentNullException(nameof(cameraKey));

    ICamera camera;
    lock (this.operateLock)
    {
        if (this.disposed || !this.cameras.TryRemove(cameraKey, out camera))
            return CameraRemoveStatus.NotFound;
    }

    return this.DisposeCamera(camera) ? CameraRemoveStatus.Removed : CameraRemoveStatus.ReleaseFailed;
}
```

`DisposeCamera` 成功分支（`if (!success)` 块之前）追加：

```csharp
    if (success)
    {
        lock (this.operateLock)
            this.lastError = null;
    }
```

`CameraService.Close` 替换为（消除 `is CameraManager` 嗅探）：

```csharp
public CameraResult Close(string cameraKey)
{
    if (string.IsNullOrEmpty(cameraKey))
        return CameraResult.Fail(-1, "Camera key is empty");

    return this.cameraManager.Remove(cameraKey) switch
    {
        CameraRemoveStatus.Removed => CameraResult.Success(0),
        CameraRemoveStatus.NotFound => CameraResult.Fail(-1, ErrorMsg),
        _ => CameraResult.Fail(-1, this.cameraManager.LastError ?? "Dispose camera error")
    };
}
```

`CameraService.OpenCamera` 中两处 `cameraManager.TryRemove(cameraKey);` 改为 `this.cameraManager.Remove(cameraKey);`（忽略返回值）。

- [ ] **Step 4: 全量测试 + CHANGELOG + 提交**

```bash
git add -A && git commit -m "Fix: ICameraManager.Remove 区分 NotFound/ReleaseFailed，Close 精确报错并移除具体类型嗅探"
```

### Task 5: CameraStream 订阅重构——worker 启动时序 + 重复订阅替换语义（审查 一.5、一.6；决策 D1）

**Files:**
- Modify: `Junevy.EasyCamera/Common/CameraStreamSuber.cs`（整体重写：Worker 延迟挂接）
- Modify: `Junevy.EasyCamera/Common/CameraStream.cs`（Subscribe 重写）
- Modify: `Junevy.EasyCamera.Tests/Abstractions/CameraStreamTests.cs`
- Modify: `Junevy.EasyCamera.Tests/Abstractions/CameraStreamRegressionTests.cs`（现有重复订阅测试改名与断言）

**Interfaces:**
- Produces: `CameraStreamSuber` 构造签名变为 `(string key, Channel<IFrame> channel, CancellationTokenSource cts)`，新增 `StartWorker(Func<CameraStreamSuber, Task> workerFactory)` 与 `Task Worker`；`ICameraStream.Subscribe` 签名不变，语义变为**同 key 原子替换**（D1 默认值）。

- [ ] **Step 1: 重写 CameraStreamSuber**

```csharp
// Junevy.EasyCamera/Common/CameraStreamSuber.cs
using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Common
{
    /// <summary>
    /// 相机图像帧数据流订阅者，用于封装通道、取消令牌和工作线程的生命周期。
    /// 工作线程由 <see cref="StartWorker" /> 在构造后立即启动，
    /// 实例引用以参数形式传入工厂，消除"闭包读取尚未赋值的局部变量"的时序依赖。
    /// </summary>
    public sealed class CameraStreamSuber
    {
        private int disposed;
        private int ctsDisposed;
        private Task worker;

        public CameraStreamSuber(string key, Channel<IFrame> channel, CancellationTokenSource cts)
        {
            this.Key = key ?? throw new ArgumentNullException(nameof(key));
            this.Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.Cts = cts ?? throw new ArgumentNullException(nameof(cts));
        }

        /// <summary>
        /// 订阅者标识，与注册到流的 Key 一致
        /// </summary>
        public string Key { get; }

        public Channel<IFrame> Channel { get; }

        public CancellationTokenSource Cts { get; }

        /// <summary>
        /// 消费工作线程任务，由 <see cref="StartWorker" /> 启动后可用
        /// </summary>
        public Task Worker => this.worker;

        /// <summary>
        /// 启动消费工作线程。须在构造后立即调用恰好一次；
        /// 工厂以参数接收本实例，杜绝闭包时序依赖。
        /// </summary>
        public void StartWorker(Func<CameraStreamSuber, Task> workerFactory)
        {
            if (workerFactory == null)
                throw new ArgumentNullException(nameof(workerFactory));

            var task = Task.Run(() => workerFactory(this));
            Volatile.Write(ref this.worker, task);

            // 竞争失败路径可能已先 Dispose：此处兜底补挂 CTS 延迟释放
            if (Volatile.Read(ref this.disposed) == 1)
                this.AttachCtsCleanup();
        }

        /// <summary>
        /// 非阻塞地写入一帧。通道已完成或订阅已释放时返回 false。
        /// </summary>
        public bool TryWrite(IFrame frame)
        {
            if (Volatile.Read(ref this.disposed) == 1)
                return false;

            try
            {
                return this.Channel.Writer.TryWrite(frame);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>
        /// 取消订阅：完成通道写入、取消工作线程，并在工作线程结束后释放 CTS。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 1)
                return;

            try
            {
                this.Channel.Writer.TryComplete();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                this.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (Volatile.Read(ref this.worker) == null)
            {
                // StartWorker 尚未执行（仅竞争失败窗口可能）：其内部会兜底释放 CTS
                return;
            }

            this.AttachCtsCleanup();
        }

        /// <summary>
        /// CTS 延迟到工作线程退出后再释放，避免工作线程仍在使用 Token 时
        /// 因 CTS 被释放而抛出 ObjectDisposedException。幂等。
        /// </summary>
        private void AttachCtsCleanup()
        {
            if (Interlocked.Exchange(ref this.ctsDisposed, 1) == 1)
                return;

            var worker = Volatile.Read(ref this.worker);
            if (worker == null || worker.IsCompleted)
            {
                this.Cts.Dispose();
                return;
            }

            worker.ContinueWith(
                _ => this.Cts.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
```

- [ ] **Step 2: 重写 CameraStream.Subscribe（替换语义，D1）**

```csharp
public void Subscribe(
    string subberKey,
    int capacity,
    Func<string, IFrame, Task> handler,
    Action<Exception> whenException = null)
{
    if (string.IsNullOrEmpty(subberKey))
        throw new ArgumentException("The subscriber key is null or empty.", nameof(subberKey));

    if (handler == null)
        throw new ArgumentNullException(nameof(handler));

    if (capacity < 1)
        capacity = 1;

    lock (this.operationLock)
    {
        if (this.disposed == 1)
            throw new ObjectDisposedException(nameof(CameraStream));
    }

    var channel = Channel.CreateBounded<IFrame>(
        new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        },
        frame => DisposeFrame(frame, whenException));
    var cts = new CancellationTokenSource();

    // 先构造订阅者并启动 worker（实例经参数传入，无闭包时序依赖），再进入注册竞争
    var candidate = new CameraStreamSuber(subberKey, channel, cts);
    candidate.StartWorker(self => this.ConsumeAsync(subberKey, self, channel, cts, handler, whenException));

    CameraStreamSuber replaced = null;
    bool accepted;
    lock (this.operationLock)
    {
        if (this.disposed == 1)
        {
            accepted = false;
        }
        else
        {
            // 替换语义：同 Key 重新订阅时原子替换旧订阅者，旧订阅者在锁外清理
            this.subscribers.TryRemove(subberKey, out replaced);
            this.subscribers[subberKey] = candidate;
            accepted = true;
        }
    }

    if (!accepted)
    {
        candidate.Dispose();
        throw new ObjectDisposedException(nameof(CameraStream));
    }

    replaced?.Dispose();
}
```

（`ConsumeAsync`/`Publish`/`Unsubscribe`/`Dispose`/`RemoveDeadSubscriber`/`DisposeFrame`/`NotifyException` 不变。）

- [ ] **Step 3: 更新测试**

`CameraStreamTests.Subscribe_DuplicateKey_ShouldKeepSingleSubscriber` 整体替换为：

```csharp
[TestMethod]
public async Task Subscribe_DuplicateKey_ShouldReplaceSubscriberAtomically()
{
    using var stream = new CameraStream("SN001");
    var oldHandlerStarted = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    var oldHandlerRelease = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    var received = new TaskCompletionSource<IFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

    // 旧订阅：handler 挂起占住 worker，模拟"旧订阅仍在消费"
    stream.Subscribe("suber", 1, (_, _) =>
    {
        oldHandlerStarted.TrySetResult(null);
        return oldHandlerRelease.Task;
    });
    stream.Publish(new MockFrame());
    await oldHandlerStarted.Task;

    // 重复订阅：必须原子替换
    stream.Subscribe("suber", 1, (_, frame) =>
    {
        received.TrySetResult(frame);
        return Task.CompletedTask;
    });

    Assert.AreEqual(1, stream.SubscriberCount, "相同订阅Key必须被替换而不是重复注册");

    oldHandlerRelease.TrySetResult(null);
    var frame2 = new MockFrame();
    stream.Publish(frame2);

    var completed = await Task.WhenAny(received.Task, Task.Delay(5000));
    Assert.AreSame(received.Task, completed, "替换后的订阅者必须接管后续帧投递");
    Assert.AreSame(frame2, received.Task.Result);

    received.Task.Result.Dispose();
    stream.Unsubscribe("suber");
}
```

`CameraStreamRegressionTests.ConcurrentSubscribe_SameKey_LeavesOnlyOneSubscription` 保持不变（替换语义下同样恰好 1 个存活、回调 1 次）。

- [ ] **Step 4: 全量测试 + CHANGELOG + 提交**

```bash
dotnet test Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj -c Release
git add -A && git commit -m "Fix: 订阅 worker 启动时序消除闭包竞态；同 key 重复订阅改为原子替换语义"
```

### Task 6: CameraService 每 key 锁对称 + StartGrab 幂等 + 成功码归零（审查 一.9、二.3、二.4；决策 D4）

**Files:**
- Modify: `Junevy.EasyCamera/Common/CameraService.cs`
- Modify: `Junevy.EasyCamera.Tests/Common/CameraManagerServiceRegressionTests.cs`

**Interfaces:**
- Produces: 私有字段 `cameraOpenLocks` 更名 `cameraKeyLocks`，OpenCamera/StartGrab/StopGrab/SetTrigger 统一 per-key 锁；`StartGrab` 对已取流相机返回 `Success(0)`；`StopGrab`/`Close` 成功码由 1 改为 0；删除服务级 `locker` 字段。

- [ ] **Step 1: 追加测试**

```csharp
// CameraManagerServiceRegressionTests.cs 追加
[TestMethod]
public void CameraService_StartGrab_AlreadyGrabbing_ReturnsSuccess()
{
    var provider = new TrackingProvider();
    var manager = new CameraManager();
    using var streams = new StreamManager();
    var service = new CameraService(provider, manager, streams);

    Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key").IsSuccess);
    Assert.IsTrue(service.StartGrab("key").IsSuccess);

    var second = service.StartGrab("key");

    Assert.IsTrue(second.IsSuccess, "StartGrab 必须幂等，与 StopGrab 风格一致");
}

[TestMethod]
public void CameraService_StopGrab_SuccessCodeIsZero()
{
    var provider = new TrackingProvider();
    var manager = new CameraManager();
    using var streams = new StreamManager();
    var service = new CameraService(provider, manager, streams);

    Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key").IsSuccess);
    Assert.IsTrue(service.StartGrab("key").IsSuccess);

    var result = service.StopGrab("key");

    Assert.IsTrue(result.IsSuccess);
    Assert.AreEqual(0, result.Code, "成功结果 Code 约定为 0，不得使用魔数 1");
}
```

- [ ] **Step 2: 实现**

`CameraService` 字段区：

```csharp
// 相机 key 级操作锁。锁对象按 key 只增不减：key 数量量级 ≈ 相机数（小且稳定），
// 可接受；按引用计数删除会在 Close/Open 竞态窗口重开两个锁对象，得不偿失。
private readonly ConcurrentDictionary<string, object> cameraKeyLocks = new();
```

删除 `private readonly object locker = new();`；`OpenCamera` 中 `cameraOpenLocks` 改名 `cameraKeyLocks`。新增私有方法：

```csharp
private object GetKeyLock(string cameraKey) => this.cameraKeyLocks.GetOrAdd(cameraKey, _ => new object());
```

`StartGrab`：

```csharp
public CameraResult StartGrab(string cameraKey)
{
    if (string.IsNullOrEmpty(cameraKey))
        return CameraResult.Fail(-1, ErrorMsg);

    lock (this.GetKeyLock(cameraKey))
    {
        if (!cameraManager.TryGet(cameraKey, out var camera) || !camera.IsConnected)
            return CameraResult.Fail(-1, ErrorMsg);

        // 幂等：已在取流时直接成功，与 StopGrab 的幂等风格一致
        if (camera.IsGrabbing)
            return CameraResult.Success(0);

        try
        {
            return camera.StartGrab();
        }
        catch (Exception e)
        {
            camera?.StopGrab();
            return CameraResult.Fail(-2, e.Message);
        }
    }
}
```

`StopGrab`：`lock(locker)` → `lock (this.GetKeyLock(cameraKey))`，成功返回 `CameraResult.Success(0)`。`SetTrigger`：方法体（从 TryGet 到最终 return）整体包进 `lock (this.GetKeyLock(cameraKey))`。

- [ ] **Step 3: 全量测试 + CHANGELOG + 提交**

```bash
git add -A && git commit -m "Fix: CameraService 取流/触发操作统一 per-key 锁；StartGrab 幂等；成功 Code 归零"
```

### Task 7: SubscribeFrameStream 吞掉流已释放异常（审查 一.9）

**Files:**
- Modify: `Junevy.EasyCamera/Common/CameraService.cs:114-126`
- Modify: `Junevy.EasyCamera.Tests/Common/CameraManagerServiceRegressionTests.cs`

**Interfaces:**
- Produces: `SubscribeFrameStream` 在流已释放时返回 false（不外泄 `ObjectDisposedException`）。

- [ ] **Step 1: 追加测试**

```csharp
[TestMethod]
public void CameraService_SubscribeFrameStream_AfterStreamManagerDispose_ReturnsFalse()
{
    var provider = new TrackingProvider();
    var manager = new CameraManager();
    var streams = new StreamManager();
    var service = new CameraService(provider, manager, streams);

    Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key").IsSuccess);
    streams.Dispose();

    Assert.IsFalse(service.SubscribeFrameStream("key", "sub", (_, _) => Task.CompletedTask));
}
```

- [ ] **Step 2: 实现**

```csharp
if (capacity <= 0)
    capacity = this.streamOptions.StreamCapacity;

try
{
    stream.Subscribe(subKey, capacity, processFrame, whenException);
    return true;
}
catch (ObjectDisposedException)
{
    // 流随服务释放的竞态窗口：按"订阅失败"表达，不外泄异常
    return false;
}
```

- [ ] **Step 3: 全量测试 + CHANGELOG + 提交**

```bash
git add -A && git commit -m "Fix: SubscribeFrameStream 在流已释放竞态下返回 false 而非外泄异常"
```

### Task 8: GetParam&lt;int&gt; 溢出防护（审查 一.8）

**Files:**
- Modify: `Junevy.EasyCamera/Vendors/HikVision/HikCamera.cs:594-599`
- Modify: `Junevy.EasyCamera/Vendors/IRayple/IRaypleCamera.cs:389-395`
- Modify: `Junevy.EasyCamera.Tests/Vendors/HikVisionRegressionTests.cs`

**Interfaces:**
- Produces: `HikCamera` 新增 `internal static bool TryConvertToInt64ToInt32(long value, out int result)`（Task 2 已开启 InternalsVisibleTo）；`GetParam<int>` 越界时返回 `default`（契约"失败返回 default"不变，语义为"取值失败"）。

- [ ] **Step 1: 追加测试（直接测 internal 转换器，避免构造完整 IParameters 假件）**

```csharp
// HikVisionRegressionTests.cs 追加
[TestMethod]
public void HikCamera_Int32Conversion_OutOfRangeReturnsFailure()
{
    Assert.IsTrue(HikCamera.TryConvertToInt64ToInt32(12345L, out var value));
    Assert.AreEqual(12345, value);

    Assert.IsFalse(HikCamera.TryConvertToInt64ToInt32((long)int.MaxValue + 1, out var overflowHigh));
    Assert.AreEqual(0, overflowHigh);

    Assert.IsFalse(HikCamera.TryConvertToInt64ToInt32((long)int.MinValue - 1, out var overflowLow));
    Assert.AreEqual(0, overflowLow);
}
```

- [ ] **Step 2: 实现**

```csharp
// HikCamera.cs 新增
/// <summary>
/// long → int 的受检转换：越界按"取值失败"处理，禁止静默回绕
/// </summary>
internal static bool TryConvertToInt64ToInt32(long value, out int result)
{
    if (value < int.MinValue || value > int.MaxValue)
    {
        result = 0;
        return false;
    }

    result = (int)value;
    return true;
}
```

`HikCamera.GetParam<T>` 的 `type == typeof(int)` 分支：

```csharp
if (type == typeof(int))
{
    if (this.device.Parameters.GetIntValue(paramName, out IIntValue intValue) == MvError.MV_OK
        && TryConvertToInt64ToInt32(intValue.CurValue, out var intValue32))
        return (T)(object)intValue32;
    return default;
}
```

`IRaypleCamera.GetParam<T>` 的 `type == typeof(int)` 分支：

```csharp
if (type == typeof(int))
{
    long value = 0;
    if (this.camera.IMV_GetIntFeatureValue(paramName, ref value) == IMV_OK
        && TryConvertToInt64ToInt32(value, out var value32))
        return (T)(object)value32;
    return default;
}
```

（`IRaypleCamera` 直接复制同一 `internal static` 方法；该类型已 Obsolete 不对外承诺。）

- [ ] **Step 3: 全量测试 + CHANGELOG + 提交**

```bash
git add -A && git commit -m "Fix: GetParam<int> 越界按取值失败处理，禁止 long→int 静默回绕"
```

### Task 9: HikFrameWrapper.Data 懒缓存 + 释放后安全访问（审查 一.9）

**Files:**
- Modify: `Junevy.EasyCamera/Vendors/HikVision/HikFrameWrapper.cs:54-57`
- Modify: `Junevy.EasyCamera.Tests/Vendors/HikVisionRegressionTests.cs`

**Interfaces:**
- Produces: `Data` 首次访问缓存 SDK 拷贝（无论 SDK 的 `PixelData` 是否每次重新拷贝，均降为一次性成本）；已释放帧返回已缓存数组，若从未访问过则返回空数组（不返回 null、不触发已释放原生内存读取）。

- [ ] **Step 1: 追加测试（FakeImage.PixelData 每次返回新数组，恰好可验证缓存）**

```csharp
// HikVisionRegressionTests.cs 追加
[TestMethod]
public void HikFrameWrapper_Data_ReturnsSameInstanceOnRepeatedAccess()
{
    var frame = new HikFrameWrapper(new FakeFrameOut());

    var first = frame.Data;
    var second = frame.Data;

    Assert.AreSame(first, second, "Data 必须缓存，禁止每次访问都触发 SDK 拷贝（5MB+/次）");
}

[TestMethod]
public void HikFrameWrapper_Data_AfterDispose_ReturnsCachedOrEmpty()
{
    var accessed = new HikFrameWrapper(new FakeFrameOut());
    var cached = accessed.Data;
    accessed.Dispose();
    Assert.AreSame(cached, accessed.Data, "已释放帧的已缓存托管副本必须仍可安全读取");

    var neverAccessed = new HikFrameWrapper(new FakeFrameOut());
    neverAccessed.Dispose();
    Assert.IsNotNull(neverAccessed.Data);
    Assert.AreEqual(0, neverAccessed.Data.Length, "未缓存且已释放的帧返回空数组，禁止触达已释放原生内存");
}
```

- [ ] **Step 2: 实现**

```csharp
// HikFrameWrapper.cs
/// <summary>
/// 托管像素副本缓存：SDK 的 PixelData 可能每次访问重新拷贝，
/// 缓存为一次性成本；释放后托管副本仍可安全读取（原生缓冲不受影响）
/// </summary>
private byte[] pixelData;

/// <summary>
/// 图像数据数组，托管内存（首次访问时缓存；
/// 帧已释放且从未访问过时返回空数组）
/// </summary>
public byte[] Data
{
    get
    {
        var cached = this.pixelData;
        if (cached != null)
            return cached;

        if (Volatile.Read(ref this.disposed) == 1)
            return Array.Empty<byte>();

        var data = this.native.Image.PixelData;
        return Interlocked.CompareExchange(ref this.pixelData, data, null) ?? data;
    }
}
```

- [ ] **Step 3: 全量测试 + CHANGELOG + 提交**

```bash
git add -A && git commit -m "Perf: HikFrameWrapper.Data 懒缓存托管副本并保证释放后安全访问"
```

### Task 10: HikProvider 枚举失败诊断（审查 一.9）

**Files:**
- Modify: `Junevy.EasyCamera/Vendors/HikVision/HikCameraProvider.cs:85-89`

**Interfaces:**
- Produces: 枚举失败时输出 `System.Diagnostics.Trace` 警告（无新依赖）。

- [ ] **Step 1: 实现**

文件头追加 `using System.Diagnostics;`，枚举失败分支：

```csharp
if (result != MvError.MV_OK || deviceInfos == null)
{
    // 枚举失败目前只能以空集合表达；输出诊断便于现场排查设备缺失问题
    Trace.TraceWarning($"HikCameraProvider.Enumerate failed with code {result}.");
    yield break;
}
```

- [ ] **Step 2: 构建 + CHANGELOG + 提交**

```bash
git add -A && git commit -m "Fix: Hik 枚举失败输出 Trace 诊断，不再静默返回空集合"
```

## 批次 B：文档与文本修正

### Task 11: 接口契约文档 + 文本修正（审查 二.2、二.4、二.6、三.5）

**Files:**
- Modify: `Junevy.EasyCamera.Core/Abstractions/ICameraStream.cs`（Publish 注释）
- Modify: `Junevy.EasyCamera.Core/Common/ICameraManager.cs`（TryRegister false 语义）
- Modify: `Junevy.EasyCamera.Core/Abstractions/CameraResult.cs`（Code 约定）
- Modify: `Junevy.EasyCamera/Vendors/HikVision/HikCameraInfo.cs:73-90`（SetDefinedName 注释）
- Modify: `Junevy.EasyCamera/Vendors/IRayple/IRaypleCameraInfo.cs:78-95`（SetDefinedName 注释）
- Modify: `Junevy.EasyCamera/Common/CameraService.cs`（"camerakey" 消息）
- Modify: `Junevy.EasyCamera/Junevy.EasyCamera.csproj:19-24`（描述拼写）

**Interfaces:** 无签名变化，纯注释/字符串修正。

- [ ] **Step 1: ICameraStream.Publish 注释改为与实现一致的所有权契约**

```csharp
/// <summary>
/// 发布一帧图像。
/// 发布方将帧的初始引用转移给流：流为每个成功入队的订阅者增加一个引用，
/// 并负责在消费、淘汰、取消或流释放时释放；Publish 返回后发布方不得再访问该帧。
/// </summary>
/// <param name="frame">一帧图像，所有权随调用转移给流</param>
```

- [ ] **Step 2: ICameraManager.TryRegister 注释补充**

```
/// <returns>注册成功返回 <c>true</c>；<c>false</c> 表示该 cameraKey 已注册（保留现有实例，调用方应改用 TryGet）</returns>
```

- [ ] **Step 3: CameraResult 类注释补充 Code 约定**

```
/// <remarks>
/// <see cref="Code"/> 约定：成功时为厂商原生成功码（通常为 0，本库自身产生且无原生码时恒为 0）；
/// 失败时为厂商原生错误码，无原生码时为 -1。调用方只应以 <see cref="IsSuccess"/> 判定成败，
/// <see cref="Code"/> 用于诊断与厂商错误对照。
/// </remarks>
```

- [ ] **Step 4: SetDefinedName 注释澄清（两个 Info 类）**

```
/// <summary>
/// 更新本信息对象的用户自定义名称（仅本地副本，不下发相机设备）。
/// 设备侧命名请通过相机参数接口设置（如海康 DeviceUserID）。
/// </summary>
```

- [ ] **Step 5: 文本修正**

`CameraService.OpenCamera`：`"The camera info or camerakey is null"` → `"The camera info or camera key is null"`。

`Junevy.EasyCamera.csproj` Description 替换为：

```xml
	  <Description>
		  An easy-to-use industrial camera library (HikVision, IRayple).
		  The library adopted a modern design:
		    1) Based on the factory pattern and dependency injection.
			2) Asynchronous frame streaming.
	  </Description>
```

- [ ] **Step 6: 全量测试（确认无行为变化）+ CHANGELOG + 提交**

```bash
git add -A && git commit -m "Docs: 修正帧所有权/Code 约定/TryRegister 语义注释与包描述拼写"
```

## 批次 C：可扩展 API 与命名重命名（破坏性，决策 D2/D3）

### Task 12: TryGetParam 扩展（审查 二.1；决策 D2，默认新增）

**Files:**
- Modify: `Junevy.EasyCamera.Core/Abstractions/ICamera.cs`（新增方法）
- Modify: `Junevy.EasyCamera/Vendors/HikVision/HikCamera.cs`
- Modify: `Junevy.EasyCamera/Vendors/IRayple/IRaypleCamera.cs`
- Modify: `Junevy.EasyCamera.Core/Common/ICameraService.cs` + `Junevy.EasyCamera/Common/CameraService.cs`
- Modify: `Junevy.EasyCamera.Tests/Mocks/MockCamera.cs`、`Junevy.EasyCamera.Tests/Common/CameraManagerServiceRegressionTests.cs`

**Interfaces:**
- Produces:
  - `ICamera`：`bool TryGetParam<T>(string paramName, out T value)`；`bool TryGetEnumParam(string paramName, out string value)`
  - `ICameraService`：`bool TryGetParam<T>(string cameraKey, string paramName, out T value)`
  - 现有 `GetParam<T>`/`GetEnumParam` 保留并改为薄封装（`TryGetParam ? value : default`），逻辑不再重复。

- [ ] **Step 1: 追加测试**

```csharp
// CameraManagerServiceRegressionTests.cs：TrackingCamera 增加
public bool TryGetParamResult { get; set; } = true;
public int TryGetParamValue { get; set; } = 42;

public bool TryGetParam<T>(string paramName, out T value)
{
    if (this.TryGetParamResult && typeof(T) == typeof(int)) { value = (T)(object)this.TryGetParamValue; return true; }
    value = default;
    return false;
}
public bool TryGetEnumParam(string paramName, out string value) { value = null; return false; }

// 新测试
[TestMethod]
public void CameraService_TryGetParam_UnavailableCamera_ReturnsFalse()
{
    var provider = new TrackingProvider();
    var manager = new CameraManager();
    using var streams = new StreamManager();
    var service = new CameraService(provider, manager, streams);

    Assert.IsFalse(service.TryGetParam<int>("missing", "Width", out var value));
    Assert.AreEqual(0, value);
}

[TestMethod]
public void CameraService_TryGetParam_ReportsFailureInsteadOfDefault()
{
    var provider = new TrackingProvider { TryGetParamResult = false };
    var manager = new CameraManager();
    using var streams = new StreamManager();
    var service = new CameraService(provider, manager, streams);

    Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key").IsSuccess);

    Assert.IsFalse(service.TryGetParam<int>("key", "Width", out _), "取参失败必须与 default 值可区分");
}
```

`MockCamera` 实现两个新接口成员（`return false, value = default` 形式）。

- [ ] **Step 2: 实现（HikCamera 为例，IRaypleCamera 同构复制）**

```csharp
// ICamera.cs 契约注释
/// <summary>
/// 尝试获取参数。与 <see cref="GetParam{T}(string)" /> 不同，
/// 本方法可区分"参数值恰为 default"与"获取失败"。
/// </summary>
/// <returns>获取成功返回 <c>true</c>；相机不可用、参数名无效、类型不支持或读取失败返回 <c>false</c></returns>
bool TryGetParam<T>(string paramName, out T value);

/// <summary>尝试获取枚举参数符号名，语义同上</summary>
bool TryGetEnumParam(string paramName, out string value);
```

```csharp
// HikCamera.cs：GetParam<T> 主体迁移到 TryGetParam<T>，原方法变薄封装
public bool TryGetParam<T>(string paramName, out T value)
{
    value = default;
    if (!this.IsConnected || string.IsNullOrEmpty(paramName))
        return false;

    try
    {
        var type = typeof(T);

        if (type == typeof(int))
        {
            if (this.device.Parameters.GetIntValue(paramName, out IIntValue intValue) == MvError.MV_OK
                && TryConvertToInt64ToInt32(intValue.CurValue, out var value32))
            {
                value = (T)(object)value32;
                return true;
            }
            return false;
        }

        if (type == typeof(long))
        {
            if (this.device.Parameters.GetIntValue(paramName, out IIntValue intValue64) == MvError.MV_OK)
            {
                value = (T)(object)intValue64.CurValue;
                return true;
            }
            return false;
        }

        if (type == typeof(float))
        {
            if (this.device.Parameters.GetFloatValue(paramName, out IFloatValue floatValue) == MvError.MV_OK)
            {
                value = (T)(object)floatValue.CurValue;
                return true;
            }
            return false;
        }

        if (type == typeof(string))
        {
            if (this.device.Parameters.GetStringValue(paramName, out IStringValue stringValue) == MvError.MV_OK)
            {
                value = (T)(object)(stringValue.CurValue ?? string.Empty);
                return true;
            }
            return false;
        }

        if (type == typeof(bool))
        {
            if (this.device.Parameters.GetBoolValue(paramName, out bool boolValue) == MvError.MV_OK)
            {
                value = (T)(object)boolValue;
                return true;
            }
            return false;
        }

        return false;
    }
    catch
    {
        return false;
    }
}

public T GetParam<T>(string paramName)
    => this.TryGetParam(paramName, out var value) ? value : default;

public bool TryGetEnumParam(string paramName, out string value)
{
    value = null;
    if (!this.IsConnected || string.IsNullOrEmpty(paramName))
        return false;

    try
    {
        if (this.device.Parameters.GetEnumValue(paramName, out IEnumValue enumValue) == MvError.MV_OK)
        {
            value = enumValue.CurEnumEntry?.Symbolic ?? string.Empty;
            return true;
        }
        return false;
    }
    catch
    {
        return false;
    }
}

public string GetEnumParam(string paramName)
    => this.TryGetEnumParam(paramName, out var value) ? value : string.Empty;
```

`CameraService`：

```csharp
public bool TryGetParam<T>(string cameraKey, string paramName, out T value)
{
    value = default;
    if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
        return false;

    return camera.TryGetParam(paramName, out value);
}

public T GetParam<T>(string cameraKey, string paramName)
    => this.TryGetParam(cameraKey, paramName, out var value) ? value : default;
```

（`GetEnumParam`/`TryGetEnumParam` 在 service 层同理薄封装。）

- [ ] **Step 3: 全量测试 + CHANGELOG + 提交**

```bash
git add -A && git commit -m "Feat: 新增 TryGetParam/TryGetEnumParam，区分取参失败与 default 值（加法扩展）"
```

### Task 13: 公共 API 命名重命名（审查 三.1–三.4；决策 D3，默认直接改）

**Files:**
- Rename: `Junevy.EasyCamera/Common/CameraStreamSuber.cs` → `CameraStreamSubscriber.cs`
- Modify: `Junevy.EasyCamera.Core/Abstractions/ICameraStream.cs`、`Junevy.EasyCamera.Core/Abstractions/CameraType.cs`（重命名文件为 `CameraInterfaceType.cs`）、`Junevy.EasyCamera.Core/Common/ICameraService.cs`、`Junevy.EasyCamera.Core/Common/IStreamManager.cs`
- Modify: `Junevy.EasyCamera/Core/Common/ICameraManager.cs` 无（Task 4 已定名）；`Junevy.EasyCamera/Core/Common/IStreamManager.cs` 参数名
- Modify: 全部引用点（CameraStream.cs、CameraService.cs、StreamManager.cs、HikCameraProvider.cs、IRaypleCameraProvider.cs、IRaypleCameraInfo.cs、测试文件）
- Modify: `Junevy.EasyCamera.Tests/Vendors/HikVisionRegressionTests.cs`（RecordingStream.Subscribe 参数名）

**Interfaces（重命名映射表，逐一机械替换）：**

| 旧 | 新 | 说明 |
|---|---|---|
| `CameraStreamSuber` | `CameraStreamSubscriber` | 类名与文件名（Task 5 后结构不变） |
| `ICameraStream.Subscribe/Unsubscribe` 参数 `subberKey` | `subscriberKey` | 参数名 |
| `CameraStream.Suber`（Task 5 已更名 `Worker`，确认无残留） | — | 校验项 |
| `CameraType` | `CameraInterfaceType` | 类型名；文件改名 `CameraInterfaceType.cs` |
| `CameraType.ALL` | `CameraInterfaceType.All` | 枚举成员 |
| `ICameraService.GetOnlineCameraSerialNumber` | `GetSerialNumber` | 方法名 |
| `ICameraService.SetTrigger(triggerWay, isAcquisition)` | `SetTrigger(string triggerSource, bool enableTrigger)` | 参数名与注释同步 |
| `IStreamManager`/`CameraStream` 参数与字段 `userDefinedName` | `cameraKey` | 流以 cameraKey 创建，统一概念 |
| `CameraManager.operateLock` | `operationLock` | 锁字段统一（HikCamera 的 `locker` → `stateLock`，`operationGate` 保留） |
| `Junevy.EasyCamera.Core.Extensions.ServiceCollectionExtensions` | `CoreServiceCollectionExtensions` | 类名（命名空间不变，扩展方法调用点不受影响） |

- [ ] **Step 1: 执行重命名（建议顺序：先 Core 接口与枚举 → 实现 → 测试），每完成一组即构建**

Run: `dotnet build Junevy.EasyCamera.sln -c Release`（每组重命名后运行，Expected: 0 错误）

- [ ] **Step 2: 全量测试（含引用旧名的既有测试全部更新后）**

Run: `dotnet test Junevy.EasyCamera.Tests/Junevy.EasyCamera.Tests.csproj -c Release`
Expected: 全部 PASS（含 `HikCameraProvider_TransportMapping_CoversGenTlAndDoesNotExpandUnknown` 中 `CameraType` → `CameraInterfaceType` 的反射更新）

- [ ] **Step 3: CHANGELOG + 提交（一条提交包含全部重命名，便于整体 review/回滚）**

```bash
git add -A && git commit -m "Refactor: 公共 API 命名修正（CameraStreamSubscriber/CameraInterfaceType/GetSerialNumber/SetTrigger 参数/cameraKey 统一）"
```

## 批次 D：README 与交接文档

### Task 14: 创建 README.md

**Files:**
- Create: `README.md`（仓库根目录）

**Interfaces:** 无代码。内容中的 API 名称以 Task 13 完成后的形态书写（若 D3 被否决则沿用旧名，执行时按实际 API 校对）。

- [ ] **Step 1: 写入以下内容**

````markdown
# Junevy.EasyCamera

工业相机统一操作类库：以一套抽象封装不同品牌工业相机 SDK，便于第三方项目快速接入相机能力、
通过接口无缝更换相机硬件、通过 Channel 管理相机帧流。

## 当前状况

| 品牌 | 状态 | 说明 |
|---|---|---|
| 海康 HikVision | ✅ 可用 | 基于 `MvCameraControl.Net`，含完整生命周期/并发回归测试 |
| 华睿 IRayple | 🚧 未完成 | 类型标记 `[Obsolete("未开发完毕", true)]`，DI 启用即抛 `NotImplementedException` |
| Basler | 📋 预留 | DI 启用即抛 `NotImplementedException`，待接入 |
| Cognex | 📋 规划中 | 预留扩展点 |

- 双目标框架：`net48` + `net8.0`（net8.0 下声明 Windows 平台）；运行时须 x64（厂商 SDK 为 AMD64 专用）。
- 解决方案结构：`Junevy.EasyCamera.Core`（抽象与契约）/ `Junevy.EasyCamera`（DI 与厂商适配）/ `Junevy.EasyCamera.Tests`。
- 厂商 SDK DLL 位于仓库 `Libs/`，构建自包含；SDK 版本由 `global.json`（.NET SDK 9.0.316）与中央包管理约束。
- 详见 [AGENTS.md](AGENTS.md)（项目约定）与 [docs/](docs/)（审查/计划文档）。

## 构建

```powershell
dotnet build .\Junevy.EasyCamera\Junevy.EasyCamera.csproj -c Release -v:minimal
dotnet test .\Junevy.EasyCamera.Tests\Junevy.EasyCamera.Tests.csproj -c Release --logger "console;verbosity=minimal"
```

## 推荐使用方式（DI）

```csharp
// 1. 注册（Host/ServiceProvider 场景）
services.AddEasyCamera(options => options.EnableHikVision = true,
    stream => stream.StreamCapacity = 5);   // 每个订阅者的有界帧缓存

// 2. 解析并初始化 SDK（进程级一次）
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
        whenException: ex => { /* 不提供时 handler 异常将终止该订阅 */ });

    // 5. 开始/停止取流、参数与触发
    cameraService.StartGrab("cam-1");
    cameraService.SetParam("cam-1", "ExposureTime", 5000f);
    cameraService.StopGrab("cam-1");
    cameraService.SetTrigger("cam-1", "Line1", enableTrigger: true); // 设置后需重新 StartGrab
    cameraService.StartGrab("cam-1");

    // 6. 关闭相机（帧流保留，重开同名 key 后订阅自动继续生效）
    cameraService.Close("cam-1");
}
finally
{
    sdk.Dispose();   // 引用计数归零时执行 SDK Finalize
}
```

## 简易使用（手动构造，不依赖 DI）

```csharp
using var streams = new StreamManager();
var service = new CameraService(
    new AggregateCameraProvider(new IVendorCameraProvider[] { new HikCameraProvider() }),
    new CameraManager(),
    streams,
    new StreamOptions());
// 后续步骤与 DI 方式的 3–6 步相同
```

## 帧与资源管理约定（重要）

- SDK 回调帧在回调内归还，订阅者拿到的帧一律是**独立克隆**；帧为引用计数模型：
  发布方初始 1 个引用由流持有并释放，订阅者每收到一帧持有 1 个引用，必须 `Dispose()`。
- 需要异步保帧：在 handler 返回前调用 `frame.AddRef()`，使用完成后对应 `Dispose()`。
- 优先使用 `frame.Data`（托管副本）；`frame.PixelDataPtr` 在帧释放后即失效。
- 订阅 Channel 为有界队列（DropOldest）：消费慢时旧帧被淘汰并立即释放，不阻塞采集。
- 未提供 `whenException` 时，handler 异常会终止该订阅（帧仍会释放、订阅自动摘除）。

## 路线图

- Basler（Pylon）适配、IRayple 补齐（同步模型、回调排空、帧引用防护）
- Cognex 适配调研
- 线阵相机扩展接口 `ILineScanCamera` 落地（行触发、行计数）
````

- [ ] **Step 2: 校对 README 中每个 API 名称与 Task 13 后的实际代码一致（编译期无校验，需人工核对），修正差异**

- [ ] **Step 3: 提交**

```bash
git add README.md && git commit -m "Docs: 新增 README（项目现状、快速上手、推荐用法）"
```

### Task 15: AGENTS.md 项目介绍更新 + 全量验收

**Files:**
- Modify: `AGENTS.md`（第 4 节"项目介绍"）
- Modify: `CHANGELOG.md`（收尾校对）

- [ ] **Step 1: AGENTS.md 第 4 节追加/更新以下要点**

- 公共契约要点：`CameraInterfaceType`（原 CameraType）表示物理接口类型；帧流订阅者为 `CameraStreamSubscriber`；服务层取参用 `TryGetParam<T>` 可区分失败；`ICameraManager.Remove` 返回 `CameraRemoveStatus`。
- 资源红线补充：`HikFrameWrapper.Data` 为懒缓存托管副本；SDK Initialize/Finalize 由 `HikCameraSdkSystem` 按实例引用计数管理（含测试 seam）。
- 帧分发模型澄清：每帧仅在相机回调内克隆一次，订阅分发为引用计数浅共享（`Publish` 对订阅者仅 `AddRef + TryWrite` 同一实例）；克隆缓冲由最后一个归零的引用持有者物理释放。
- 增补一行：`README.md` 为项目入口文档（现状/快速上手/推荐用法）。

- [ ] **Step 2: 最终验收**

```powershell
dotnet build .\Junevy.EasyCamera.sln -c Release -v:minimal
dotnet test .\Junevy.EasyCamera.Tests\Junevy.EasyCamera.Tests.csproj -c Release --logger "console;verbosity=minimal"
```

Expected: 构建 0 错误；测试全绿（基线 63 + 新增约 15+）。

- [ ] **Step 3: CHANGELOG 收尾**

在 `CHANGELOG.md` 顶部新增条目（标题日期用实际执行日期，形如 `## 2026-10-06（2026-10-05 审查问题修复）`），逐条核对 Task 1–14 的摘要行齐全、表述一致，并注明"README 新增"。

- [ ] **Step 4: 提交**

```bash
git add -A && git commit -m "Docs: 更新 AGENTS.md 项目介绍与 CHANGELOG 收尾"
```

---

## 明确不做（本计划范围外）

- IRayple 的同步模型、回调排空、AddRef 防护（D5 暂缓；其开发清单已记录在审查报告 一.9）。
- `ILineScanCamera` 扩展（保留空标记接口，待线阵需求落地）。
- `ICameraManager` 改名 `ICameraRegistry`（收益低于迁移成本，仅补充语义注释）。
- IRayple 遗留代码的 CA1416 警告清理（待 IRayple 开发时一并处理）。
- 厂商 SDK 行为变更（`MvCameraControl.Net.dll` / `MVSDK_Net.dll` 为外部依赖）。
