# 屏幕工作台 · MonitorSetting

屏幕工作台是一款 Windows 显示器设置工具，可以在同一个窗口里调整单屏和多屏的显示参数。程序运行期间，按 `Win + O` 即可唤出窗口。

## 下载和运行

1. 在本仓库的 **Releases** 页面打开 `v1.0.6`，从 **Assets** 下载 `MonitorSetting-1.0.6-win-x64.zip`。
2. 完整解压 ZIP，打开解压后的 `MonitorSetting` 文件夹，双击 `MonitorSetting.exe`。移动或分享时请保留完整的文件夹或 ZIP，程序需要同目录的 DLL 和资源文件。
3. 关闭窗口后，程序会留在系统托盘。要完全退出，请在托盘菜单中选择「退出」。

支持 **Windows 10 / 11 x64**，压缩包已包含 .NET 运行时。程序还需要 **Microsoft Edge WebView2 Runtime**。如果启动时提示缺少 WebView2，可运行随包附带的 `install\MicrosoftEdgeWebview2Setup.exe`，安装时需要联网。[Microsoft Edge WebView2 下载页](https://developer.microsoft.com/microsoft-edge/webview2/#download-section)

## 主要功能

- **基础显示设置**：识别显示器，调整亮度、分辨率、缩放比例和刷新率。
- **多屏管理**：切换复制或扩展等显示模式，调整屏幕方向和排列，设置主显示器。
- **色彩与 HDR**：在设备支持时调节色温、对比度和 HDR。
- **界面偏好**：支持中文、日本語、한국어和 English，可选择白色、黑色或跟随系统的外观。

## 使用提示

- 分辨率、排列、显示模式、方向、HDR 和动态刷新率等更改需要在 20 秒内确认。超时后，程序会尝试恢复之前的设置。
- 外接显示器的亮度、对比度调节取决于显示器和连接方式是否支持 DDC/CI。动态刷新率选项取决于 Windows 和显卡驱动。
- 程序运行期间会接管 `Win + O`。完全退出后，Windows 可继续使用该组合键。
- 个人偏好、日志和待恢复配置保存在 `%LOCALAPPDATA%\MonitorSetting`。随附组件的许可证和第三方声明位于解压目录的 `licenses` 文件夹。
