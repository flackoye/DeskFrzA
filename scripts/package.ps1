param([string]$Iscc)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'DeskFrz/DeskFrz.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if (-not $Iscc) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $Iscc = $compiler.Source }
    else {
        $installedCompiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
        $localCompiler = Join-Path $projectRoot 'artifacts/tools/inno/ISCC.exe'
        if (Test-Path -LiteralPath $installedCompiler -PathType Leaf) { $Iscc = $installedCompiler }
        else { $Iscc = $localCompiler }
    }
}
if (-not (Test-Path -LiteralPath $Iscc -PathType Leaf)) { throw '请安装 Inno Setup 6.5+，或通过 -Iscc 指定 ISCC.exe。' }
$stage = Join-Path $projectRoot ('artifacts/package-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'DeskFrzA-win-x64'
$release = Join-Path $projectRoot 'releases'
New-Item -ItemType Directory -Path $publish, $release -Force | Out-Null
& dotnet publish (Join-Path $projectRoot 'DeskFrz/DeskFrz.csproj') -c Release -r win-x64 --self-contained true -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publish
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $publish -Recurse
$licenses = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'DeskFrz/obj/project.assets.json') -Raw | ConvertFrom-Json
$cache = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$webViewVersion = [string]$project.Project.ItemGroup.PackageReference.Version
foreach ($name in @('LICENSE.txt', 'NOTICE.txt')) {
    $notice = Join-Path $cache "microsoft.web.webview2/$webViewVersion/$name"
    if (-not (Test-Path -LiteralPath $notice)) { throw "缺少 WebView2 依赖许可文件：$notice" }
    Copy-Item -LiteralPath $notice -Destination (Join-Path $licenses ('WebView2-' + $name))
}
$runtimeVersion = $project.SelectSingleNode('//RuntimeFrameworkVersion').InnerText
foreach ($package in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.aspnetcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $notices = @(Get-ChildItem -LiteralPath (Join-Path $cache "$package/$runtimeVersion") -File |
        Where-Object Name -Match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$')
    if ($notices.Count -eq 0) { throw "缺少 .NET 运行时许可：$package" }
    foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses ($package + '-' + $notice.Name)) }
}
Get-ChildItem -LiteralPath $publish -Filter '*.pdb' -Recurse | Remove-Item
& $Iscc '/Qp' "/DAppVersion=$version" "/DPublishDir=$publish" "/DReleaseDir=$release" (Join-Path $projectRoot 'installer/DeskFrzA.iss')
if ($LASTEXITCODE -ne 0) { throw '安装包编译失败。' }
Compress-Archive -LiteralPath $publish -DestinationPath (Join-Path $release 'DeskFrzA-Portable-win-x64.zip') -Force
Get-FileHash -LiteralPath (Join-Path $release 'DeskFrzA-Setup.exe'), (Join-Path $release 'DeskFrzA-Portable-win-x64.zip') -Algorithm SHA256 |
    ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) } |
    Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding utf8
Write-Output "已生成 $release 中的安装包、便携包和校验文件。"
