<#
.SYNOPSIS
    HugoQuickStart 一键构建 + 发布 + 生成安装包（正式方案：HugoQuickStart_publish 目录）
.DESCRIPTION
    发布根目录为工程内目录 HugoQuickStart_publish，目录结构：
      publish\  - 自包含(win-x64)发布输出（含完整 .NET 运行时）
      install\  - Inno Setup 脚本 HugoQuickStart_setup.iss 及语言文件
      dist\     - 生成的安装包 HugoQuickStart_setup.exe
    依次执行：
      1) Release 构建
      2) 自包含发布到 publish\（win-x64 目录形式）
      3) ISCC 编译 install\HugoQuickStart_setup.iss -> dist\HugoQuickStart_setup.exe
.EXAMPLE
    .\build-installer.ps1             # 完整流程
    .\build-installer.ps1 -NoPublish  # 跳过发布步，直接用现有 publish\ 打安装包
#>
param(
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path          # ...\HugoQuickStart
$pubRoot = Join-Path $root 'HugoQuickStart_publish'                 # 正式发布根目录（工程内）
$proj    = Join-Path $root 'HugoQuickStart\HugoQuickStart.csproj'
$pubDir  = Join-Path $pubRoot 'publish'
$iss     = Join-Path $pubRoot 'install\HugoQuickStart_setup.iss'
$bak     = Join-Path $env:TEMP 'HugoQuickStart.publish.config.json'

Write-Host "== HugoQuickStart 构建/发布/安装包 (正式方案: HugoQuickStart_publish) ==" -ForegroundColor Cyan

if (-not (Test-Path $pubRoot)) { throw "发布根目录不存在: $pubRoot" }
if (-not (Test-Path $iss))      { throw "未找到打包脚本: $iss" }

# --- 1. 定位 Inno Setup 编译器 ---
$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw '未找到 ISCC.exe，请先安装 Inno Setup 6。' }
Write-Host ("使用 Inno Setup: " + $iscc) -ForegroundColor Gray

# --- 2. 结束运行中的实例，避免文件锁 ---
$running = Get-Process HugoQuickStart -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning ("检测到 {0} 个 HugoQuickStart 实例在运行，尝试结束以释放文件锁。" -f $running.Count)
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

# --- 3. Release 构建 ---
Write-Host "---- [1/3] Release 构建 ----" -ForegroundColor Cyan
dotnet build $proj -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）。" }

if (-not $NoPublish) {
    # --- 4. 自包含发布到 publish（目录形式，含完整运行时；不打包 config.json） ---
    Write-Host "---- [2/3] 发布(自包含 win-x64) -> publish ----" -ForegroundColor Cyan
    # 若发布目录中残留 config.json（运行程序时生成），先备份并从打包源移除，避免覆盖用户数据
    if (Test-Path (Join-Path $pubDir 'config.json')) {
        Copy-Item (Join-Path $pubDir 'config.json') $bak -Force
        Remove-Item (Join-Path $pubDir 'config.json') -Force
        Write-Host "发布目录中的 config.json 已备份到 $bak（不会打入安装包）" -ForegroundColor Yellow
    }
    # 清空旧发布产物，避免残留过时文件
    if (Test-Path $pubDir) { Get-ChildItem $pubDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
    # DebugType=none：不生成 PDB（libSkiaSharp.pdb 等约 100MB，安装包无用的调试符号）
    dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false --nologo -v minimal -o $pubDir
    if ($LASTEXITCODE -ne 0) { throw "发布失败（退出码 $LASTEXITCODE）。" }
    # 兜底：清除所有 .pdb（含 NuGet 原生包带来的 libSkiaSharp.pdb 等，约 100MB）
    $pdbs = Get-ChildItem $pubDir -Recurse -Filter *.pdb -File -ErrorAction SilentlyContinue
    if ($pdbs) {
        $savedMB = [math]::Round((($pdbs | Measure-Object Length -Sum).Sum / 1MB), 1)
        $pdbs | Remove-Item -Force
        Write-Host ("已删除 {0} 个 .pdb 文件，减少 {1} MB" -f $pdbs.Count, $savedMB) -ForegroundColor Yellow
    }
} else {
    Write-Host "---- 已跳过发布步 (NoPublish) ----" -ForegroundColor Gray
    if (-not (Test-Path $pubDir)) { throw "publish\ 不存在且已跳过发布，无法打安装包。" }
}

# --- 5. 编译安装包 ---
Write-Host "---- [3/3] 生成安装包 (ISCC) ----" -ForegroundColor Cyan
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "安装包编译失败（退出码 $LASTEXITCODE）。" }

$setup = Join-Path $pubRoot 'dist\HugoQuickStart_setup.exe'
if (Test-Path $setup) {
    Write-Host ("完成！安装包：" + $setup + "  (大小 {0:N1} MB)" -f ((Get-Item $setup).Length/1MB)) -ForegroundColor Green
} else {
    throw "未找到生成的安装包。"
}
Write-Host "构建、发布、安装包全部完成。" -ForegroundColor Green
