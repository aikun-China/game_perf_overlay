# game_perf_overlay

`game_perf_overlay` 是一款适用于 Windows x64 的轻量性能悬浮窗，可在桌面上显示 CPU、内存、GPU、FPS 和时间信息。

## 功能

- 监控 CPU、内存和 GPU 使用率，并选择要显示的项目。
- 显示目标显示器的 FPS：优先监控焦点窗口，无法获取窗口 FPS 时回退到桌面 FPS。
- 可选择 FPS 监控屏幕，并设置是否显示当前被监控的窗口名称。
- 自定义悬浮窗位置、横/竖布局、字体、文字颜色、背景透明度和刷新间隔。
- 显示最近 60 秒 FPS 曲线；自动按每次启动记录 CSV。
- 在设置页导出本次运行的 CSV，并配置日志和 CSV 的保留份数。设为 `0` 表示无限保留。
- 在设置页检查 GitHub 最新版本，并查看更新说明、下载并运行新版安装包。

## 系统要求

- Windows 10 或 Windows 11，64 位。
- 需要 .NET Framework 4.x；PresentMon 组件需与主程序放在一起。
- FPS 采集能力取决于 Windows 图形组件、显卡驱动及目标程序。窗口 FPS 监控使用 PresentMon/ETW。
- 程序安装目录需要允许当前用户写入，以便保存 `logs` 和 `csv`。

## 安装和运行

从 GitHub Releases 下载 `game_perf_overlay-Setup-v版本号.exe` 并运行。安装程序会让你选择安装目录，并在该目录下建立 `game_perf_overlay` 子文件夹；可选择创建桌面快捷方式。

也可以直接运行已构建目录中的 `game_perf_overlay.exe`，但必须保留同目录的 `PresentMon-2.6.0-x64.exe`。首次启动时按向导选择 FPS 监控屏幕和悬浮窗显示器；之后从系统托盘打开设置或退出程序。

## 数据文件

安装目录下自动创建：

- `logs`：应用诊断日志，每次启动新建一份，默认最多保留 10 份。
- `csv`：性能采样 CSV，每次启动新建一份，默认无限保留。

文件名使用日期和纯数字序号，例如 `2026-10-07-1.log` 与 `2026-10-07-1.csv`。在设置页可以修改各自的保留份数；`0` 表示无限保留。用户配置保存在 `%AppData%\PerfMonitor\config.json`。

## 检查更新

设置页中的“检查更新...”会查询 [GitHub Releases](https://github.com/aikun-China/game_perf_overlay/releases)。如果发现较新的版本，会显示版本号和更新说明；选择“更新”后程序下载该版本安装包并启动安装，选择“取消”则不作更改。检查更新需要网络连接。

## 从源码构建

使用 Visual Studio / .NET Framework 4.x MSBuild 构建 x64 Release：

```bat
build_net48.bat
```

构建安装包需要安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)，然后运行：

```bat
build_release.bat
```

安装程序会生成到 `dist`。发布新版本时同步更新 `AppVersion.cs`、`game_perf_overlay.iss` 和本说明中的版本号，并在 GitHub Releases 发布对应版本的安装包。

## 许可

本项目自身的代码和文档按 Apache License 2.0 授权，详见 [`LICENSE`](./LICENSE)。

随附的 PresentMon 组件是独立的第三方软件，版权归 Intel Corporation 所有，并继续遵循 [`PresentMon-LICENSE.txt`](./PresentMon-LICENSE.txt) 中的 MIT 许可；本项目的 Apache-2.0 许可不取代或更改其许可。
