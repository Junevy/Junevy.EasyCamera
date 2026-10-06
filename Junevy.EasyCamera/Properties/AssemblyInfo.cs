using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// 有关程序集的一般信息由以下
// 控制。更改这些特性值可修改
// 与程序集关联的信息。
[assembly: AssemblyTitle("Junevy.EasyCamera")]
[assembly: AssemblyDescription("Industrial camera manager: vendor implementations (HikVision / IRayple) built on Junevy.EasyCamera.Core.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Junevy")]
[assembly: AssemblyProduct("Junevy.EasyCamera")]
[assembly: AssemblyCopyright("Copyright © Junevy 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// 将 ComVisible 设置为 false 会使此程序集中的类型
//对 COM 组件不可见。如果需要从 COM 访问此程序集中的类型
//请将此类型的 ComVisible 特性设置为 true。
[assembly: ComVisible(false)]

// 如果此项目向 COM 公开，则下列 GUID 用于类型库的 ID
[assembly: Guid("29abb70b-55fb-4941-abf1-a7966ffddebc")]

// 程序集的版本信息由下列四个值组成:
//
//      主版本
//      次版本
//      生成号
//      修订号
//
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

// 测试工程可见 internals：用于 HikCameraSdkSystem 测试 seam 与 HikCamera 数值转换器等无硬件单测
[assembly: InternalsVisibleTo("Junevy.EasyCamera.Tests")]
