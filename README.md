1558323088: 09-27 16:58:06
EasyDownload

https://img.shields.io/github/license/yourusername/EasyDownload

https://img.shields.io/badge/Windows-11-blue.svg

EasyDownload 是一个基于 .NET 8 和 WinUI 3 构建的现代化、高性能下载工具。我们刚刚完成了从旧版架构向 Windows App SDK (WinUI 3) 的全面迁移，带来了更流畅的原生 Windows 体验和更低的资源占用。

✨ 新版本亮点 (v1.1.0)

原生 WinUI 3 界面：采用最新的 Windows App SDK，完美支持 Windows 11 的圆角、Mica 背景和深色模式。

极致性能：告别卡顿，利用 DirectX 加速的 UI 渲染，即使是大文件下载也能保持界面丝滑。

现代化架构：重构了底层逻辑，代码更清晰，内存管理更优秀。

🚀 快速开始 (Quick Start)

前提条件

在运行本项目之前，请确保你的开发环境或目标机器满足以下要求：

操作系统：Windows 10 版本 1809 (Build 17763) 或更高版本 / Windows 11。

运行时依赖 (重要！)：

由于本项目采用 框架依赖 (Framework-dependent) 发布，目标机器必须安装 Windows App SDK Runtime  (版本 1.6 或更高)。

注：如果你使用的是自包含 (Self-contained) 版本，则无需额外安装此运行时。

构建与运行

如果你是开发者，想要自己编译项目：

克隆仓库

bash

git clone https://github.com/yourusername/EasyDownload.git

cd EasyDownload

还原依赖

bash

dotnet restore

编译并运行

bash

dotnet build

dotnet run --no-launch-profile

或者使用 Visual Studio 2022 打开  EasyDownload.sln  并直接运行。
⚠️ 常见问题：为什么双击 exe 没反应？

很多用户反馈“Debug 能跑，但发布的 exe 双击没反应”。这通常是因为缺少 WinUI 3 的依赖。请检查：

是否只复制了单个 exe 文件？

错误做法：只把  EasyDownload.exe  复制到桌面双击。

正确做法：发布目录下的所有文件（包括  Microsoft.UI.Xaml.dll ,  Microsoft.WindowsAppRuntime.dll  等）必须和 exe 放在一起，双击  EasyDownload.exe  才能正常运行。

是否缺少 Windows App SDK Runtime？

如果你下载的是“框架依赖版”，请务必先安装 Windows App SDK Runtime .

是否开启了单文件发布？

WinUI 3 目前对“单文件发布 (PublishSingleFile)”支持不佳，容易导致静默崩溃。请确保发布配置中关闭了单文件选项。

🛠️ 开发环境 (Development Environment)

IDE: Visual Studio 2022 (建议 17.8 或更高版本)

SDK: .NET 8.0 SDK

框架: Windows App SDK 1.6+

语言: C# 12.0

🤝 贡献 (Contribution)

欢迎任何形式的贡献！如果你发现了 Bug 或有好的功能建议，请随时提交 Issue 或 Pull Request。

Fork 本仓库

创建你的分支 ( git checkout -b feature/AmazingFeature )

提交你的修改 ( git commit -m 'Add some AmazingFeature' )

推送到分支 ( git push origin feature/AmazingFeature )

开启一个 Pull Request
