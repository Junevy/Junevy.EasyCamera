# Junevy.EasyCamera HikVision 公共层 Bug 修复计划

## 目标与范围

修复当前项目中已确认的 HikVision 实现及公共层生命周期、并发、帧所有权和可验证性问题。IRayple 目录及其实现不在本次范围内，不因本计划修改或验收。

工作区当前已有未提交/暂存改动；执行过程中必须保留这些改动，不得使用 `git reset --hard`、`git checkout --` 或覆盖式回滚。

## 基线

- 记录执行前 `git status --short` 和 `git diff --stat`。
- 先确认主项目当前构建结果，再确认测试项目当前失败原因；修复后用同样命令复验。

## 实施任务

### 1. HikCameraSdkSystem 生命周期

- 修正 `Initialize`/`Release`/`Dispose` 状态转换，使首次初始化成功后最终一定能释放 SDK。
- 只有原生初始化成功后才发布“已初始化”状态；初始化失败时允许安全重试。
- 处理多个容器/多个实例共享进程级 SDK 的情况，避免一个实例释放影响其他仍在使用的实例；若采用引用计数，确保计数增减和异常路径对称。
- 保持现有公共接口兼容，不把 vendor 静态调用泄漏到公共抽象。

验收：增加可测试 seam 或等价单元测试，覆盖初始化成功/失败重试、重复 Dispose、多个使用者和释放调用次数。

### 2. HikCamera 采集、回调与销毁时序

- 为 Connect/Close/StartGrab/StopGrab/Dispose 建立明确的状态迁移和互斥规则。
- StopGrab 原生调用失败时不得把对象伪装成“未采集”；关闭流程必须能处理失败并保留诊断信息。
- 回调开始后登记在途数量；关闭时先禁止新回调、解绑/停止采集，再等待在途回调完成，最后释放设备。
- 无论对象是否已进入 Dispose 状态，只要 SDK 回调已经取得 frame buffer，都必须按 SDK 约定归还；不得因竞态跳过 `FreeImageBuffer`。
- 保持回调中深拷贝后再发布，确保发布到 Stream 的帧不依赖 SDK 缓冲区。

验收：覆盖重复连接/关闭、StopGrab 失败、Dispose 与回调并发、回调异常以及释放顺序；不得出现未归还缓冲区或已销毁对象访问。

### 3. CameraStream 帧所有权与订阅并发

- 增加明确的 disposed 状态；Dispose 后 Subscribe/Publish 按既定失败或丢弃语义执行且不创建后台 worker。
- 同一 subscriber key 的并发 Subscribe 必须只有一个生效；失败的候选订阅必须立即释放 channel、CTS 和 worker。
- 保持发布路径非阻塞；明确有界队列满时的策略（默认采用可观测的丢弃策略），并对被丢弃帧正确 `Dispose`。
- 修正 handler 或帧 Dispose 异常时的 null-safe 异常回调，避免异常处理本身产生 `NullReferenceException`。
- 恢复与接口文档一致的容量边界；不擅自把合法的小容量改成固定下限。
- 审核 AddRef/Dispose 失败路径，保证每次成功 AddRef 都有且只有一次对应释放。

验收：覆盖并发同名订阅、Dispose/Subscribe 竞态、队列满、无异常回调、handler 抛异常、发布无订阅和多订阅引用计数平衡。

### 4. CameraManager / CameraService 原子性与资源清理

- OpenCamera 对同一 key 使用原子化的 Get-or-create；竞争失败的候选 camera 必须关闭并 Dispose，不能连接后泄漏。
- Close/Remove 的成功结果必须反映真实 Close/Dispose 结果；清理异常要保留并记录或返回，不得静默伪成功。
- 不在 Manager 全局锁内执行可能阻塞或回调用户代码的原生 Close/Dispose；锁只保护字典和状态快照。
- Manager Dispose 后不得再返回已销毁对象，并清空注册表；重复 Dispose 必须幂等。
- 明确 CameraStream 与 camera 实例的生命周期，避免关闭后复用包含旧代帧的队列，或在确认安全时提供代际隔离。

验收：覆盖同 key 并发 Open、Open 失败回滚、Close 失败、Manager Dispose 后访问、重复清理和旧帧隔离。

### 5. HikVision 映射、测试项目与文档

- 补齐 `MvGenTLGigEDevice` 等 HikVision transport 到 `MvCameraControl.Net` 层类型的映射；Unknown 不得意外扩大为 ALL，除非这是 SDK 明确要求。
- 修复测试项目的 SDK/NuGet 引用，使测试能被实际发现和执行；不通过“空测试退出 0”作为验收。
- 添加覆盖上述回归场景的测试，必要时为 native SDK 引入最小 fake/adapter seam。
- 按项目约定更新根目录 `CHANGELOG.md`，并补充/更新根目录 `AGENTS.md` 第 4 节项目介绍，记录本次公共层与 HikVision 的边界。

## 验证命令

实现完成后至少执行：

```powershell
dotnet build .\Junevy.EasyCamera\Junevy.EasyCamera.csproj -c Release --no-restore -v:minimal
dotnet test .\Junevy.EasyCamera.Tests\Junevy.EasyCamera.Tests.csproj -c Release --no-restore --logger "console;verbosity=minimal"
```

若解决方案级构建仍受现有外部 SDK 或历史工程文件影响，必须区分并报告：主项目结果、测试项目结果、未修复的环境/依赖阻塞；不得把未执行测试描述为通过。

## 完成标准

- 只改动本计划范围内文件，IRayple 实现保持不变。
- 所有新增测试实际被发现并执行。
- 关键异常路径有可诊断结果，资源和引用计数在并发场景下平衡。
- 最终验收报告列出每项计划任务对应的 diff、测试命令和实际输出。
