屏幕工作台 1.0.6 · Windows x64 Release

开始使用
1. 将整个压缩包解压到一个文件夹，双击 MonitorSetting.exe。
2. 软件运行后，按 Win+O 可显示并激活设置窗口。
3. 关闭窗口会留在系统托盘中，Win+O 仍可调出；完全退出请使用托盘菜单中的“退出”。
4. 再次双击程序会唤出已经运行的窗口。

运行环境
- Windows 10/11 x64。已包含 .NET 运行时，不需要安装开发工具。
- 需要 Microsoft Edge WebView2 Runtime。若电脑未安装，请按启动提示安装，或运行附带的 install\MicrosoftEdgeWebview2Setup.exe（首次安装需要联网）。也可使用微软官方下载页：
  https://developer.microsoft.com/microsoft-edge/webview2/#download-section
- 常见显示器设置在本机完成。外接显示器的亮度、对比度等调节需要设备及连接方式支持 DDC/CI。

快捷键与显示设置
- Win+O 在软件运行期间用于调出本软件，完全退出后不再占用该组合键。
- Windows 原有的 Win+O 屏幕方向锁定功能会在软件占用快捷键期间被替代。
- 分辨率、排列、显示模式、方向、HDR 和动态刷新率等更改需要在 20 秒内确认；超时会尝试恢复。
- 不支持的设备功能会标明。动态刷新率仅显示 Windows 驱动报告并通过配置验证的选项。
- 可选择中文、日本語、한국어、English，以及白色、黑色或跟随系统背景。

分享与数据
- 分享整个 Release 压缩包；不要只发送 MonitorSetting.exe，也不要删除附带的 DLL、web、runtimes 或 licenses 文件夹。
- 个人偏好、日志和待恢复配置保存在当前用户的 %LOCALAPPDATA%\MonitorSetting 中，不在分享包内。
- licenses 文件夹保存随附组件的许可证与第三方声明。

Quick start
Extract the complete archive and run MonitorSetting.exe. While the application is
running, press Win+O to show its window. Closing the window keeps the application
in the notification area; use its tray menu to quit completely.

Requires Windows 10/11 x64 and Microsoft Edge WebView2 Runtime. The .NET runtime
is included. Share the complete folder or ZIP, not the executable alone.
