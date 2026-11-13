# 开发、发布与更新

需要 .NET 8 SDK，首次还原依赖需要访问 NuGet。前端使用原生 HTML/CSS/JavaScript，无 Node 构建依赖。

```powershell
dotnet build DeskFrz/DeskFrz.csproj
dotnet run --project DeskFrz/DeskFrz.csproj
```

核心验证（不会移动桌面图标）：

```powershell
dotnet run --project DeskFrz/DeskFrz.csproj -- --selftest --data ./artifacts/tests
dotnet run --project DeskFrz/DeskFrz.csproj -- --probe --output ./artifacts/desktop.json
node scripts/test-frontend.cjs
```

导出文件的父目录需要事先存在。`--probe` 包含项目路径和布局，应作为个人数据保留，不上传到 GitHub。

`RestoreTests` 通过内存中的 Windows 边界运行实际恢复服务，覆盖数量变化、单屏模板作用域、自动/手动恢复竞争、网格确认、撤销、失败备份、延迟落位及旧记录兼容，不操作真实桌面。前端路由检查直接执行现有 JavaScript 中的恢复入口。

需要浏览器验证时，可运行 `node scripts/test-frontend.cjs --serve`。它输出一个本地地址，用合成的 70 个图标、50 个图标历史模板和单屏自由布局提供 API，供实际点击恢复、确认和撤销；不连接 Windows 桌面接口。关闭对应 Node 进程即可结束。

也可在应用原生 WebView2 窗口中验证完整接口链：

```powershell
dotnet run --project DeskFrz/DeskFrz.csproj -- --ui-selftest --data ./artifacts/tests/ui-fresh
```

`--ui-selftest` 必须显式指定全新空目录，窗口标题含“合成桌面自检”。它使用实际前端、会话校验和恢复接口，仅将桌面读写替换为内存；预置双屏70图标现场、50图标历史和单屏自由摆放模板。可依次恢复历史、恢复自由布局、撤销并核对70个图标保留。该模式关闭自动更新，不能把此模式的结果记为真实桌面或物理插拔验证。

默认 `UiSettings.BackgroundEnabled=false`，窗口 X 完全退出；右上角开关保存设置并通过服务事件启停观察和托盘。开启时双击托盘、托盘“打开 DeskFrzA”或重复启动同一数据目录可唤回；托盘“退出”完全结束。`--serve` 不创建托盘。后台关闭时 `State()` 和 `Observe()` 均不会自动恢复，不强制设置默认单屏。

窗口生命周期检查可在上述合成桌面命令追加 `--lifecycle-test --output <结果.json>`，测试夹具明确启用后台，验证隐藏、后台接口、唤回和显式退出。追加 `--mode-test --output <结果.json>` 则从默认关闭开始，验证开启驻留、持久保存、关闭挂载后窗口 X 真正退出。人工验收见 [ACCEPTANCE.md](ACCEPTANCE.md)。

后台内存：项目显式使用 workstation GC。窗口隐藏或最小化持续两秒后，`DashboardHost` 释放 WebView2；显示时重建，网页状态从服务重新读取。开启后台时两秒桌面观察、单屏恢复、切屏前快照、持久撤销和激活接口保持运行。已启动界面重建失败时，仅后台开启才保留保护并允许从托盘重试；关闭模式按正常错误退出。页面重载复用进程内更新检查时间，不增加自动联网频率。

通知改用 `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 注册 Windows Toast，不引入 Windows App SDK 后台进程。目标平台及安装器最低要求统一为 Windows 10 1809（17763）。按规范化数据目录设置独立 AUMID；首次发通知才初始化通知服务。`DesktopChangeTracker` 在服务中按图标指纹稳定检测和去重，`ReviewKind` 把普通保存、主屏切换、恢复失败分开路由；点击还校验当前进程随机会话和模式。过期通知冷启动的 `-ToastActivated` 在读取数据或启动服务前直接退出，防止遗漏自定义数据目录而启动正式库。

有待处理变化时，托盘图标加静态橙点，右键可查看改动、发送测试通知及查看系统通知提交状态；不增加闪烁定时器。保存、恢复或回到比较基准时清除待处理标记和对应通知；关闭模式、退出时移除该会话通知。发送不唤回窗口，正文点击后才进入应用确认。系统禁用或API发送失败会记录状态，不把无异常提交当作横幅展示成功。数据目录的 `notification-status.json` 只保留最近一次通知诊断，不记录桌面快照。实际横幅、点击和系统免打扰行为由用户人工验收。

内存对照（必须是新空目录，只使用合成桌面）：

```powershell
dotnet run --project DeskFrz/DeskFrz.csproj -c Release -- --ui-selftest --data ./artifacts/tests/memory-fresh --memory-test --output ./artifacts/memory.json
```

约40秒，记录显示、隐藏和重开三轮的主程序及子进程私有内存、工作集、CPU累计时间和页面重建等待时间，随后注入过期初始化/重建失败并验证可重试。结果同时检查70图标和比较基准完整，成功后完全退出。工作集含共享页，不代表独占物理内存；三轮短时结果不能替代长时间驻留和真实屏幕切换验收。不采用反复强制GC或清空工作集来制造低占用数字。

浏览器开发模式（本地真实桌面数据）：

```powershell
dotnet run --project DeskFrz/DeskFrz.csproj -- --serve --port 53741 --data ./artifacts/dev-data
```

打开 `http://127.0.0.1:53741`；结束进程停止服务。数据目录独立，不覆盖默认记录。API 只监听回环地址，写操作使用随机会话令牌，并检查 Host、Origin 和跨站请求。

## 构建安装包与发布

安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 [Inno Setup 6.5+](https://jrsoftware.org/isdl.php)。在项目根目录运行：

```powershell
./scripts/package.ps1
# 自定义编译器路径：
./scripts/package.ps1 -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

`releases/` 将生成：

- `DeskFrzA-Setup.exe`：安装向导。
- `DeskFrzA-Portable-win-x64.zip`：便携包。
- `SHA256SUMS.txt`：下载校验。

发布脚本重新编译应用，并附带 .NET 与 WebView2 的许可文件。个人布局、测试产物和编译工具不会打包。

GitHub Actions 在提交或 PR 时构建并提供可下载的构建产物。推送 `v0.1.14` 这样的标签后，会生成一个 **Release 草稿**并附上安装版、便携版与校验文件；标签必须与 `DeskFrz.csproj` 的 `Version` 一致。检查草稿后点击 **Publish release**，README 下载入口才可用。也可以在 Releases 页面手动创建版本并上传这三个文件。

`.gitignore` 排除了构建目录、安装包、便携包、个人布局、会话文件及本地凭据；源码、图标、安装脚本和文档保留在仓库。

## 应用内更新

默认启动时及每六小时检查正式 Release；设置中可关闭自动检查，也可手动检查。发现新版本后顶部显示“有更新”。安装版支持下载、SHA256 校验及“安装并重启”，保留布局、设置和自定义数据目录；便携版提供下载页入口，由用户关闭应用后替换文件。

仅接受 `flackoye/DeskFrzA` 的公开正式版本，不自动安装草稿或预发布。每个 Release 必须附带 `DeskFrzA-Setup.exe` 和 `SHA256SUMS.txt`（打包脚本自动生成）。下载后和安装前各校验一次，损坏时可重新下载。无正式 Release、断网或 GitHub 限流时不会中断桌面操作；手动检查会显示原因。首次公开发布前，需要通过真实 Release 完成联网升级验收。

## 发布前验证

检查 [验证记录](VALIDATION.md)，继续验证不同硬件上的主屏切换、插拔及分辨率、缩放变化。首次公开更新需完成真实 Release 升级验收。

## 官方参考

- [多屏枚举](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)
- [显示器配置与身份](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)
- [微软桌面图标位置示例](https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933)
- [使用受支持接口操作桌面图标](https://devblogs.microsoft.com/oldnewthing/20211122-00/?p=105948)
