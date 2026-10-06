// 测试宿主同样只在 Windows/x64 上运行（厂商 SDK 为 AMD64 专用），
// 且引用了标注为 windows-only 的类库，因此测试程序集也必须声明平台依赖，
// 否则消费 windows-only API 的测试代码会集体触发 CA1416。
#if NET8_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
