// net8.0 下声明程序集级 Windows 平台依赖：System.Drawing 与厂商 SDK 均仅支持 Windows/x64。
// 必须放在源码里而不是 csproj 的 <AssemblyAttribute>：两个类库都设置了
// GenerateAssemblyInfo=false（为使用手写的 Properties\AssemblyInfo.cs），
// 该设置会连带跳过 <AssemblyAttribute> 项，导致平台标注静默失效、CA1416 告警重新出现。
#if NET8_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
