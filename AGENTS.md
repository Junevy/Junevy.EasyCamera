# 项目开发约定

## 1. 项目介绍
本项目是一个封装了海康、IRayple（华睿）工业相机SDK的类库，用于：便于在第三方项目开发中操作工业相机、通过接口无缝更换相机硬件、通过Channel管理相机帧等功能。

## 2. 注意事项
- 考虑到工业相机的SDK大多数底层都由C++实现，可能存在许多的非托管资源，例如：图像数据（IntPtr）等，在开发时务必小心非托管资源泄露、忘记释放。
- 图像可能占用内存较大（5MB+），为了性能考虑，因尽量做到少分配图像与非托管资源。
- Clone图像时，应尽量深拷贝，避免工业相机的缓存队列爆满而丢失帧。

## 3. 其他约定
- 对于不明确的功能，必须提问。
- 对于可扩展的功能，必须询问是否扩展。
- 如果有更好的实现方式或当前实现方式不符合设计规范、存在风险，必须提醒并询问，并检索是否有更好的方式，然后询问是否按照更优方式实现。
- 每次代码修改完毕后，必须摘要内容，记录到项目根目录的CHANGELOG.md中。
- 每次更新项目后，需要更项目介绍（第4项，如不存在则补充）到项目根目录下的AGENTS.md中，便于交接其他Agent。

## 4. 项目介绍

当前解决方案包含三个主要项目：

- `Junevy.EasyCamera.Core`：公共抽象、相机服务/管理器/帧流契约及基础模型。
- `Junevy.EasyCamera`：厂商适配和公共实现；HikVision 使用 `MvCameraControl.Net`，IRayple 使用其原生 SDK。
- `Junevy.EasyCamera.Tests`：公共层、服务层和 HikVision 生命周期/并发回归测试，测试宿主固定为 x64/net48。

HikVision 与公共层的资源所有权约束：SDK 回调帧必须在回调内归还，发布给订阅者的帧必须是独立克隆；相机关闭/销毁前会停止取流、解绑回调并等待在途回调结束。`CameraStream` 使用非阻塞有界队列，队列淘汰的帧必须释放。IRayple 实现不属于本轮修复范围。

验收命令：

```powershell
dotnet build .\Junevy.EasyCamera\Junevy.EasyCamera.csproj -c Release --no-restore -v:minimal
dotnet test .\Junevy.EasyCamera.Tests\Junevy.EasyCamera.Tests.csproj -c Release --no-restore --logger "console;verbosity=minimal"
```
