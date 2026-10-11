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
```

导出文件的父目录需要事先存在。`--probe` 包含项目路径和布局，应作为个人数据保留，不上传到 GitHub。

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
