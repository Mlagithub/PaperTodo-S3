param(
    [string]$OutputDir = "dist"
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

if (Test-Path $out) {
    Remove-Item -Path $out -Recurse -Force
}
New-Item -ItemType Directory -Path $out -Force | Out-Null

Write-Host "Publishing PaperTodo.Plugin.MinioSync..."
& dotnet publish $srcProject `
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

Write-Host "Build completed successfully. Output at: $out"
