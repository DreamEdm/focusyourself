<#
.SYNOPSIS
  生成 GitHub Release 用的两个发布包，输出到 release\：
    FocusYourself-<版本>-win-x64-setup.exe     安装包（Inno Setup）
    FocusYourself-<版本>-win-x64-portable.zip  便携版，解压到任意文件夹即可运行
  以及 SHA256SUMS.txt。两者都是自带 .NET 运行时的单文件程序，用户不需要另外安装 .NET。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build-release.ps1
  powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -Version 1.1.0
#>
param(
    # 默认取 src\FocusYourself.App\FocusYourself.App.csproj 里的 <Version>
    [string]$Version,
    # ISCC.exe 路径；默认在 PATH 和 Inno Setup 6 的常见安装位置查找
    [string]$Iscc,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\FocusYourself.App\FocusYourself.App.csproj'
$releaseDir = Join-Path $root 'release'
$stageDir = Join-Path $releaseDir '_staging\FocusYourself'

if (-not $Version) {
    $Version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup |
        ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+') { throw "版本号无效：$Version" }
Write-Host "FocusYourself $Version" -ForegroundColor Cyan

# ---- 1. 发布（自包含、单文件；extension\ 文件夹会放在 exe 旁边）----
# 只清空内容不删目录本身，目录在资源管理器/终端里打开着也没关系
if (Test-Path $releaseDir) { Get-ChildItem $releaseDir -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:Version=$Version `
    -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）" }

# ---- 2. 便携版 zip（根目录是 FocusYourself\ 文件夹）----
$zip = Join-Path $releaseDir "FocusYourself-$Version-win-x64-portable.zip"
# 不用 Compress-Archive / ZipFile.CreateFromDirectory：Windows PowerShell 5.1 下条目路径是反斜杠，别的解压工具会出错
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$stageParent = Split-Path $stageDir -Parent
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stageDir -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stageParent.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
Write-Host "已生成 $zip" -ForegroundColor Green

# ---- 3. 安装包 ----
if (-not $SkipInstaller) {
    if (-not $Iscc) {
        $candidates = @(
            (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $Iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    }
    if (-not $Iscc) { throw '找不到 Inno Setup 6 的 ISCC.exe。安装：winget install JRSoftware.InnoSetup，或用 -SkipInstaller 只生成便携版。' }

    & $Iscc /Q "/DAppVersion=$Version" "/DSourceDir=$stageDir" "/DOutputDir=$releaseDir" (Join-Path $root 'installer\FocusYourself.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC 失败（$LASTEXITCODE）" }
    Write-Host "已生成 $(Join-Path $releaseDir "FocusYourself-$Version-win-x64-setup.exe")" -ForegroundColor Green
}

# ---- 4. 清理临时目录，写校验和 ----
Remove-Item (Join-Path $releaseDir '_staging') -Recurse -Force
$sums = Get-ChildItem $releaseDir -File | Where-Object Extension -in '.exe', '.zip' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
# 用 LF 换行，Linux / macOS 上 sha256sum -c 才能直接校验
[IO.File]::WriteAllText((Join-Path $releaseDir 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"))

Get-ChildItem $releaseDir | Format-Table Name, @{ n = 'Size (MB)'; e = { '{0:N1}' -f ($_.Length / 1MB) } } -AutoSize
