# Latest binary release
You can download the latest binary release [here](https://github.com/13xforever/vkdiag/releases/latest). You want the `vkdiag.exe` link there.

# About
VkDiag is a rudimentary vulkan diagnostics tool for Windows.

This tool was designed to check for common Windows GPU driver issues and common layer compatibility issues with RPCS3 emulator.

It was developed at the time when Windows ICD loader was fairly new and went through a bout of changes, and was fairly hard to diagnose.

It checks general system information, GPU driver registration info, and [vulkan driver registration](https://vulkan.lunarg.com/doc/sdk/1.3.250.0/windows/LoaderInterfaceArchitecture.html) (including vulkan layers).

Please note that Linux has separate driver load mechanism, and is not supported by this tool. You may use verification and troubleshooting sections of [Arch Wiki article](https://wiki.archlinux.org/title/Vulkan) instead. 

# Building
Project is targeting .NET 10.

You will need to install [dotnet sdk](https://dotnet.microsoft.com/en-us/download) or [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/) to use with some IDE (VS Code, JetBrains Raider, Visual Studio, etc).

If you have `dotnet` available in console, you can use `dotnet build` command to build the project.