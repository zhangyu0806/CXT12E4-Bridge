# 在 macOS / Linux 检查 Windows 构建

`build-cross.py` 使用官方 .NET SDK 的 Roslyn 编译器和微软的 .NET Framework 4.8 引用程序集，生成真正的 Windows PE 程序。它不会启动 Bridge、安装开机项、写 HID 或刷固件。Windows 本机仍以仓库根目录的 `build.ps1` 和安装脚本为准。

需要 Python 3.8 或更新版本，以及 [.NET SDK 8.0.416](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)。SDK 可以解压到临时目录，无须更改系统环境。例如已将 SDK 解压到 `/tmp/cxt-dotnet` 后：

```sh
python3 install/windows-home/build-cross.py --dotnet /tmp/cxt-dotnet/dotnet
python3 install/windows-home/build-cross.py --dotnet /tmp/cxt-dotnet/dotnet --windowed
```

脚本固定使用 `Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3，并校验下载包的 SHA-256。编译强制 C# 5，保持与 Windows 系统自带 C# 编译器的语法兼容。下载缓存、EXE 和构建清单都位于 Git 忽略的 `outputs` 内。`--output-dir` 和 `--cache-dir` 可更换位置，`--sdk-version` 可显式选择其他已安装 SDK；构建清单会记录实际版本及全部源码哈希。

跨编译通过仅说明 Windows 源码和引用能够生成程序集。它不能验证 WinForms、UI Automation、XInput、鼠标事件、蓝牙连接或窗口焦点。不要在 macOS 上运行这个 EXE，也不要把构建成功描述成 Windows 实机验收。

同目录的 `test-portable.py` 可以补充运行同源的控制器状态、映射和配置校验测试：

```sh
python3 install/windows-home/test-portable.py --dotnet /tmp/cxt-dotnet/dotnet
```

这个临时测试项目不修改业务源码，不调用 Windows 输入 API。因为 .NET 8 没有 Framework 的 `JavaScriptSerializer`，测试项目用 `System.Text.Json` 适配同名 API。因此它覆盖状态逻辑及文件操作，但不能替代 Windows 原生 JSON 解析器验证；输出会明确标记这个区别。测试项目和适配器运行后自动删除。

生成说明页所需的同源默认映射，可追加 `--export-defaults outputs/windows-profile-defaults.json`，JSON 结构与 Windows 的 `--dump-default-profiles` 一致。

准备完整 Windows 安装包：

```sh
python3 install/windows-home/build-cross.py --dotnet /tmp/cxt-dotnet/dotnet --windowed --output-dir outputs/windows-cross-windowed
python3 install/windows-home/package-cross.py
```

输出为 `outputs/CXT12E4-Windows-cross.zip`。打包器检查 EXE、编译时源码和当前源码的哈希一致；只包含明确列出的源码／安装器，以及 Git 跟踪的集成脚本和说明，不包含本机配置、缓存、凭据或固件。包内 `START-HERE.md` 和清单明确标记交叉编译限制。Windows 安装器仍先原生编译、自测，再更新已安装 Bridge。

`.github/workflows/windows-bridge.yml` 在 Windows runner 上执行原生编译、`--self-test` 和安装器 `-PrepareOnly` 检查，并输出包含源码、说明、安装器和二进制的下载包。该流程不会启动常驻 Bridge 或连接设备。只有 GitHub job 确实运行并通过，才能记为 Windows 原生自动检查通过；排队、额度限制或未触发都不算通过。

参考：[微软引用程序集说明](https://learn.microsoft.com/en-us/dotnet/framework/migration-guide/reference-assemblies)、[官方 macOS SDK 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/macos)。
