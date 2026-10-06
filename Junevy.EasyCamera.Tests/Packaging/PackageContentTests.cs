using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Junevy.EasyCamera.Tests.Packaging
{
    /// <summary>
    /// NuGet 包内容守卫：厂商托管封装程序集必须真的打进包里。
    /// </summary>
    /// <remarks>
    /// 背景（2026-10-06 消费方崩溃）：主工程用
    /// <c>&lt;Reference Include="MvCameraControl.Net"&gt;&lt;HintPath&gt;..\Libs\...&lt;/HintPath&gt;&lt;/Reference&gt;</c>
    /// 引用厂商封装，<c>Private=true</c> 只让本仓库自己的输出目录拿到 DLL；
    /// <c>dotnet pack</c> 既不把文件引用放进包内，也不产生依赖声明。
    /// 后果是"本仓库 build/test 全绿、消费方还原后必崩"——单元测试完全测不到，
    /// 因此这里直接检查打包产物的条目。
    /// <para>
    /// 两条已踩过的坑也在这里钉死：PackagePath 必须逐 TFM 写全（写 "lib/" 会被 NuGet 忽略）；
   /// 打包用的 ItemGroup 不能按 $(TargetFramework) 分条件（外层构建时该变量为空，条件恒不成立）。
    /// </para>
    /// </remarks>
    [TestClass]
    public class PackageContentTests
    {
        /// <summary>必须随包分发的厂商托管封装程序集（按包内路径断言）</summary>
        private static readonly string[] RequiredVendorAssemblies =
        {
            "lib/net48/MvCameraControl.Net.dll",
            "lib/net8.0/MvCameraControl.Net.dll",
            "lib/net48/MVSDK_Net.dll",
            "lib/net8.0/MVSDK_Net.dll"
        };

        [TestMethod]
        public void Package_ShouldContainVendorManagedAssembliesForEveryTargetFramework()
        {
            var package = FindPackagedLibrary();
            if (package == null)
                Assert.Inconclusive("未找到打包产物（Junevy.EasyCamera/bin/*/Junevy.EasyCamera.*.nupkg）；请先执行 Release 构建。");

            var entries = ReadEntryNames(package);

            foreach (var required in RequiredVendorAssemblies)
            {
                Assert.IsTrue(
                    entries.Contains(required),
                    $"包内缺少 {required}。厂商托管封装未随包分发时，消费方 SDK 初始化会抛 " +
                    "TypeInitializationException、枚举相机会抛 FileNotFoundException('MvCameraControl.Net')。" +
                    "请检查 Junevy.EasyCamera.csproj 的 Pack 项（PackagePath 必须逐 TFM 写全）。");
            }
        }

        [TestMethod]
        public void Package_ShouldContainLibraryForEveryTargetFramework()
        {
            var package = FindPackagedLibrary();
            if (package == null)
                Assert.Inconclusive("未找到打包产物；请先执行 Release 构建。");

            var entries = ReadEntryNames(package);

            Assert.IsTrue(entries.Contains("lib/net48/Junevy.EasyCamera.dll"), "包内缺少 net48 资产");
            Assert.IsTrue(entries.Contains("lib/net8.0/Junevy.EasyCamera.dll"), "包内缺少 net8.0 资产");
        }

        /// <summary>
        /// 定位最新的 Junevy.EasyCamera.nupkg：从测试输出目录向上找到仓库根（以 sln 为标记），
        /// 再取主工程 bin 下最新的一包。找不到时返回 null（由调用方断言为 Inconclusive）。
        /// </summary>
        private static string FindPackagedLibrary()
        {
            var repoRoot = FindRepositoryRoot();
            if (repoRoot == null)
                return null;

            var binRoot = Path.Combine(repoRoot, "Junevy.EasyCamera", "bin");
            if (!Directory.Exists(binRoot))
                return null;

            return new DirectoryInfo(binRoot)
                .GetFiles("Junevy.EasyCamera.*.nupkg", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }

        private static string FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Junevy.EasyCamera.sln")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            return null;
        }

        private static HashSet<string> ReadEntryNames(string packagePath)
        {
            using var archive = ZipFile.OpenRead(packagePath);
            return new HashSet<string>(
                archive.Entries.Select(e => e.FullName.Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
