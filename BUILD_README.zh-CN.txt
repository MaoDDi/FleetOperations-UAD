本次导出为 0.0.8.1-export-r1。测试源码供接收者自行运行，本次未运行任何测试；旧套件数量不是本版通过证明。

Fleet Operations 源码离线构建说明

本目录的脚本只编译、运行纯控制台测试；不会安装 MOD、复制到 Mods、启动或控制游戏。
构建依赖从您提供的 GameDirectory 只读引用，不需要本项目原工作区路径，也不加载原仓库的 csproj 或 evidence 目录。

一、准备

1. Windows PowerShell 5.1 或 PowerShell 7。
2. 已安装的 .NET 6 SDK（含 net6.0 targeting/reference packs）；只有运行时不够。
   可用 dotnet --list-sdks 查看。脚本不联网、下载或安装 SDK/包；缺少依赖会直接失败。
   微软官方下载说明：https://dotnet.microsoft.com/en-us/download/dotnet/6.0
   如同时安装多个 SDK，脚本在输出目录用 global.json 固定已有的最高 .NET 6 SDK。
3. 编译 DLL 时，指定您已安装兼容 MelonLoader、并已正常生成 Il2CppAssemblies 的独立游戏副本。
   必须已有 GameAssembly.dll、MelonLoader/net6/{MelonLoader,0Harmony,Il2CppInterop.Runtime}.dll，
   以及 MelonLoader/Il2CppAssemblies 下 Assembly-CSharp.dll、Il2Cppmscorlib.dll、UnityEngine.*.dll。
   纯测试不需要游戏目录。

二、源码目录布局

  Build-Source.ps1
  Test-Source.ps1
  BUILD_README.zh-CN.txt
  src/                     生产 .cs 与七份同目录 *.Tests.cs
  src/lab-infrastructure/FleetOpsLabIsolation.cs
  src/FleetOpsPackagePaths.cs
  tests/GunImpact.GeometryTests.cs
  tests/PortablePaths.Tests.cs

最后一份测试来自原研究目录的 Program.cs，导出时仅重命名复制源码，不携带其外部路径 csproj。
原项目文件可作为参考保留，但两个脚本均自行生成独立项目，不依赖它们的相对路径/Directory.Build.props。

三、构建

在解压目录打开 PowerShell：

  .\Build-Source.ps1 -GameDirectory 'D:\Games\UAD-TestCopy'

若 dotnet 不在 PATH，可指向您已有的 SDK：

  .\Build-Source.ps1 -GameDirectory 'D:\Games\UAD-TestCopy' -DotnetPath 'D:\SDK\dotnet\dotnet.exe'

默认生成：

  out/bin/FleetOperations/FleetOperations.dll
  out/bin/FleetOpsLabIsolation/FleetOpsLabIsolation.dll
  out/build-result.json
  out/logs/                SDK、还原与构建日志

四、八套纯测试与独立 portable-path 测试

  .\Test-Source.ps1
  .\Test-Source.ps1 -DotnetPath 'D:\SDK\dotnet\dotnet.exe'

测试包含：FleetOpsCore、独立鱼雷订单、GunVolley ledger、炮管起点几何、
native-side 包络、火炮散布、散布圈绘制几何、GunImpact 几何/生命周期。
当前源码预期八套共166项；脚本读取每套实际汇总，不用固定数字替代执行。
PortablePaths.Tests 另作为第九套独立执行、计数，不并入原166项。
默认日志与 JSON 汇总位于 test-out/logs 和 test-out/test-result.json。

五、自定义路径与写入范围

支持 -SourceDirectory、-OutputDirectory、-DotnetPath、-Configuration Debug/Release；
测试另支持 -TestDirectory。含空格/中文路径请加引号。路径由脚本所在位置解析，当前工作目录不限。
OutputDirectory 不可位于输入源码、测试或游戏目录内。
生成项目、离线 NuGet 配置、dotnet home、packages、obj、bin 和日志均在 OutputDirectory 内。
NuGet 源清空，只使用本机已有 SDK/framework；不访问下载源。失败时保留日志，不自动删文件。

六、构建通过的边界

测试通过仅证明纯状态与数学行为；编译通过不代表原生、视觉或实战验收。
export-r1 源码的运行路径与存档隔离由共享 FleetOpsPackagePaths.cs 和包内安装/启动脚本处理，
Build-Source 只负责把该共享源码同时编译进两个 DLL，不改写保护或生成实际安装标记。
请按源码包主说明安装到独立副本；不要把“可从任意位置编译”理解为“可直接用于正式游戏存档
或兼容所有游戏版本”。本包不分发游戏原生生成 DLL；编译前须由您自己的 MelonLoader 正常生成。
