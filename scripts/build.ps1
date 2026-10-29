param(
    [string]$OutputDir = "dist",
    [switch]$InstallToApp
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$srcProject = Join-Path $root "src\PaperTodo.Plugin.MinioSync\PaperTodo.Plugin.MinioSync.csproj"
$pluginJson = Join-Path $root "src\PaperTodo.Plugin.MinioSync\plugin.json"
$out = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir
} else {
    Join-Path $root $OutputDir
}

# 智能探测优先使用 .NET 10 SDK（避免系统 PATH 默认调用到 .NET 8 导致插件与宿主不兼容）
$dotnetCmd = "dotnet"
$candidates = @(
    "C:\Users\keep_\app\dotnet-sdk-10.0.401-win-x64\dotnet.exe",
    (Join-Path $env:USERPROFILE "app\dotnet-sdk-10.0.401-win-x64\dotnet.exe"),
    "C:\Program Files\dotnet\dotnet.exe"
)
foreach ($cand in $candidates) {
    if (Test-Path $cand) {
        try {
            $ver = & $cand --version
            if ($ver -and $ver.StartsWith("10.")) {
                $dotnetCmd = $cand
                $env:DOTNET_ROOT = Split-Path -Parent $cand
                Write-Host ">>> Using .NET 10 SDK at: $cand ($ver)" -ForegroundColor Green
                break
            }
        } catch { }
    }
}

if (Test-Path $out) {
    Remove-Item -Path $out -Recurse -Force
}
New-Item -ItemType Directory -Path $out -Force | Out-Null

Write-Host "Publishing PaperTodo.Plugin.MinioSync..."
& $dotnetCmd publish $srcProject `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $out `
    /p:DebugType=none `
    /p:DebugSymbols=false `
    /p:GenerateDocumentationFile=false

Copy-Item -Path $pluginJson -Destination (Join-Path $out "plugin.json") -Force

# Clean shared abstractions
$shared = @("PaperTodo.Plugin.Abstractions.dll", "PaperTodo.Plugin.Abstractions.pdb", "PaperTodo.Plugin.Abstractions.xml")
foreach ($s in $shared) {
    $f = Join-Path $out $s
    if (Test-Path $f) { Remove-Item $f -Force }
}

Write-Host "Build completed successfully. Output at: $out" -ForegroundColor Green

if ($InstallToApp) {
    $targets = @(
        "C:\Users\keep_\app\PaperTodo-v4.0.0\plugins\com.papertodo.plugin.minio",
        "C:\Users\keep_\work\PaperTodo\dist\PaperTodo-v4.0.0-beta1\plugins\com.papertodo.plugin.minio"
    )
    foreach ($t in $targets) {
        if (Test-Path (Split-Path -Parent $t)) {
            New-Item -ItemType Directory -Force -Path $t | Out-Null
            Copy-Item -Path "$out\*" -Destination $t -Recurse -Force
            Write-Host ">>> Installed latest plugin to: $t" -ForegroundColor Cyan
        }
    }
}
