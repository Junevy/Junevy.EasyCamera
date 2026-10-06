# NuGet打包规则

<cite>
**本文引用的文件**
- [Junevy.EasyCamera/Junevy.EasyCamera.csproj](file://Junevy.EasyCamera/Junevy.EasyCamera.csproj)
- [Junevy.EasyCamera.Tests/Packaging/PackageContentTests.cs](file://Junevy.EasyCamera.Tests/Packaging/PackageContentTests.cs)
- [Junevy.EasyCamera/Properties/AssemblyInfo.cs](file://Junevy.EasyCamera/Properties/AssemblyInfo.cs)
- [Junevy.EasyCamera/Properties/PlatformCompatibility.cs](file://Junevy.EasyCamera/Properties/PlatformCompatibility.cs)
- [Directory.Packages.props](file://Directory.Packages.props)
- [global.json](file://global.json)
- [docs/代码审查报告-2026-10-06.md](file://docs/代码审查报告-2026-10-06.md)
- [README.md](file://README.md)
- [CHANGELOG.md](file://CHANGELOG.md)
</cite>

## 目录
1. [P0 缺陷：厂商托管封装从未打进包](#p0-缺陷厂商托管封装从未打进包)
2. [当前修法](#当前修法)
3. [两个必须记住的坑](#两个必须记住的坑)
4. [版本必须递增](#版本必须递增)
5. [不要依赖 nuget.org 上的厂商包](#不要依赖-nugetorg-上的厂商包)
6. [GenerateAssemblyInfo=false 的连带影响](#generateassemblyinfofalse-的连带影响)
7. [1.1.1 包内容实测](#111-包内容实测)
8. [新增品牌时的打包清单](#新增品牌时的打包清单)

## P0 缺陷：厂商托管封装从未打进包

1.1.0 及更早版本，主工程这样引用厂商 SDK：

```xml
<Reference Include="MvCameraControl.Net">
    <HintPath>..\Libs\MvCameraControl.Net.dll</HintPath>
    <Private>true</Private>
</Reference>
<Reference Include="MVSDK_Net">
    <HintPath>..\Libs\MVSDK_Net.dll</HintPath>
    <Private>true</Private>
</Reference>
```

`Private=true`（即 `CopyLocal`）**只**让本仓库自己的输出目录拿到 DLL；`dotnet pack` 既不把文件引用放进包内，也不产生依赖声明。消费方还原后必然失败：

| 调用 | 异常 |
| --- | --- |
| `ICameraSdkSystem.Initialize()` | `AggregateException: The type initializer for 'HikCameraSdkSystem' threw an exception` |
| `EnumerateCameras()` | `FileNotFoundException: Could not load file or assembly 'MvCameraControl.Net, Version=4.5.0.2'` |

未捕获即为"程序崩溃"。触发事件是第三方程序崩溃，排障时消费方 Agent 报告"nupkg 未声明厂商 SDK 依赖"——**结论属实**。

> [!danger]
> 这类缺陷**单元测试天然测不到**：本仓库 `build` / `test` 全绿，消费方必崩。防线只能落在打包产物上，见 [[构建与发布/包内容守卫]]。

## 当前修法

在 `Junevy.EasyCamera/Junevy.EasyCamera.csproj` 中新增**无条件** `Pack` 项：

```xml
<!-- 注意：不能用 Condition="'$(TargetFramework)' == ..." 分组 -->
<ItemGroup>
    <None Include="..\Libs\MvCameraControl.Net.dll" Pack="true" PackagePath="lib/net48;lib/net8.0" Visible="false" />
    <None Include="..\Libs\MVSDK_Net.dll" Pack="true" PackagePath="lib/net48;lib/net8.0" Visible="false" />
</ItemGroup>
```

这样保持 `Libs/` 为单一来源，**不**复制 DLL 到工程目录，同时让两个目标框架的消费方都能拿到托管封装。

## 两个必须记住的坑

> [!warning]
> **坑 1：`PackagePath="lib/"`（与 TFM 无关的根目录）在包内已存在 `lib/<tfm>/` 目录时会被 NuGet 整体忽略**——不是"额外放一份"，而是"静默丢弃"。消费方依旧拿不到，且没有任何警告。必须逐 TFM 写全：`lib/net48;lib/net8.0`。

> [!warning]
> **坑 2：多目标项目打包走外层构建，此时 `$(TargetFramework)` 为空。** 按 TFM 分条件的 `ItemGroup` 恒不执行，DLL 被静默漏掉，症状与坑 1 完全一样。因此必须用无条件分组 + 分号多路径。

两条都已写进 csproj 注释与 `docs/代码审查报告-2026-10-06.md` 第四节（"消费方打包验证"）。注意：`CHANGELOG.md` 1.1.1 条目里写的"第五节"是旧章节号，该报告第五节是"验证结论"。改动前务必先读。

## 版本必须递增

NuGet 按 `id + version` 缓存，**同版本重发不会被消费方采用**。本次修复过程中就被该缓存误导过一次（还原到旧的 1.1.0 误判"修法无效"），因此修复打包类问题务必发新版本——本次发 **1.1.1**。

升级说明里必须写明"清理缓存"：

- 删 `%USERPROFILE%\.nuget\packages\junevy.easycamera*`；
- 或还原加 `--force`；
- 若本机把缓存放在自定义 `RestorePackagesPath`，须删**该目录下**的对应条目。

## 不要依赖 nuget.org 上的厂商包

nuget.org 上确实有 `MvCameraControl.Net`，但只有第三方**未验证转包**（1.1.0 / 4.4.1.x / 4.8.0.3），与本仓库 `Libs/` 中 pin 的 **4.5.0.2** 版本对不上。依赖它等于引入供应链风险。

正解是把仓库 pin 的那份**原样打进包内**，版本由本仓库控制。消费方安装厂商原生运行时（MVS）是另一件事，见 [[项目概述/运行与安装前置条件]]。

## GenerateAssemblyInfo=false 的连带影响

`Junevy.EasyCamera.csproj` 设了 `GenerateAssemblyInfo=false`（为了使用手写 `Properties/AssemblyInfo.cs`，避免与 SDK 自动生成冲突），而 `GeneratePackageOnBuild=true`（构建即打包，产物在 `bin/<Config>/`）。

这个设置有个**隐蔽的连带效应**：它会连带跳过 csproj 的 `<AssemblyAttribute>` 项。历史上的 `SupportedOSPlatform("windows")` 就写在那里，导致平台标注**静默失效**——增量构建看不出问题，全量重建时冒出 20+ 条 `CA1416` 告警。

现改为在 `Properties/PlatformCompatibility.cs` 中显式声明：

```csharp
#if NET8_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
```

两个类库（`Junevy.EasyCamera` 与 `Junevy.EasyCamera.Core`）与测试工程都已补齐，csproj 中失效的 `<AssemblyAttribute>` 项已移除。**这也是验收必须 `--no-incremental` 的原因。**

中央包管理方面：`Directory.Packages.props` 统一版本（`ManagePackageVersionsCentrally=true`），`global.json` 固定 SDK `9.0.316`。

## 1.1.1 包内容实测

`Junevy.EasyCamera/bin/Release/Junevy.EasyCamera.1.1.1.nupkg`（约 218.8 KB）的条目：

| 条目 | 大小 |
| --- | --- |
| `lib/net48/Junevy.EasyCamera.dll` | 64000 B |
| `lib/net8.0/Junevy.EasyCamera.dll` | 64000 B |
| `lib/net48/MvCameraControl.Net.dll` | 177664 B |
| `lib/net8.0/MvCameraControl.Net.dll` | 177664 B |
| `lib/net48/MVSDK_Net.dll` | 29184 B |
| `lib/net8.0/MVSDK_Net.dll` | 29184 B |

外加 `Junevy.EasyCamera.nuspec`、`[Content_Types].xml`、`_rels/.rels` 与 `package/services/metadata/...psmdcp`。

nuspec 的依赖声明（按 TFM 分组，实物核对于 2026-10-06）：两个 TFM 都依赖 `Junevy.EasyCamera.Core` **1.1.1**（同版本）与 `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.0；net48 另有 `Microsoft.Bcl.AsyncInterfaces` 8.0.0、`System.Runtime.CompilerServices.Unsafe` 4.5.3、`System.Threading.Channels` 8.0.0、`System.Threading.Tasks.Extensions` 4.5.4；net8.0 另有 `System.Drawing.Common` 8.0.0。**没有任何厂商 SDK 依赖声明**——厂商封装靠上表的 `lib/` 内嵌文件。因此：①两个包必须一起升版本；②本地 feed 验证时要同时放入 `Junevy.EasyCamera.Core.1.1.1.nupkg`，否则还原会找不到 Core。两个厂商 DLL 各存两份（按 TFM 重复打包），换包体积的量级从 60 KB 涨到 218.8 KB——这就是"消费方零手工拷贝"的代价。

对照 1.1.0 的包（Release 下约 219 KB、Debug 下约 62 KB）：**Debug 目录里可能有更早的 1.1.0 产物**，判断"某个包是否含厂商 DLL"时要看清是哪个 Config 下的哪个版本，别拿错样本。

## 新增品牌时的打包清单

1. 厂商 DLL 放进仓库根 `Libs/`。
2. `Junevy.EasyCamera.csproj` 加 `<Reference>` + `HintPath` + `<Private>true</Private>`。
3. **同一文件**新增无条件 `<None ... Pack="true" PackagePath="lib/net48;lib/net8.0" Visible="false" />`。
4. 在 `Junevy.EasyCamera.Tests/Packaging/PackageContentTests.cs` 的 `RequiredVendorAssemblies` 追加该厂商的两个 TFM 路径。
5. 递增 `<Version>`（打包规则变更必须升版本；两个类库的 `<Version>` 与两份 `AssemblyInfo.cs`、`SKILL.md` 版本提示一起改）。
6. Release 全量重建 → 确认 nupkg 条目 → `dotnet test` 确认包内容守卫通过。
7. 按 [[构建与发布/消费方端到端验证]] 做一次端到端确认。

**章节来源**
- [Junevy.EasyCamera/Junevy.EasyCamera.csproj](file://Junevy.EasyCamera/Junevy.EasyCamera.csproj)
- [Junevy.EasyCamera/Properties/PlatformCompatibility.cs](file://Junevy.EasyCamera/Properties/PlatformCompatibility.cs)
- [Directory.Packages.props](file://Directory.Packages.props)
- [global.json](file://global.json)
- [docs/代码审查报告-2026-10-06.md](file://docs/代码审查报告-2026-10-06.md)
- [CHANGELOG.md](file://CHANGELOG.md)
