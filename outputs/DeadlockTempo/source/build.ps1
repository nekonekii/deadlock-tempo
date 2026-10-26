param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))) {
    $taskFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319'
}
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework 4.x compiler is required.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Split-Path -Parent $PSScriptRoot }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskOutput = Join-Path $OutputDirectory 'DeadlockTempo.exe'
$taskArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', "/out:$taskOutput", "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')")
foreach ($taskAssembly in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll', 'Microsoft.CSharp.dll')) {
    $taskArgs += "/reference:$(Join-Path $taskFramework $taskAssembly)"
}
foreach ($taskAssembly in @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll')) {
    $taskArgs += "/reference:$(Join-Path $taskFramework "WPF/$taskAssembly")"
}
$taskRuntime = Join-Path $env:WINDIR 'Microsoft.NET/assembly/GAC_MSIL/System.Runtime/v4.0_4.0.0.0__b03f5f7f11d50a3a/System.Runtime.dll'
$taskArgs += "/reference:$taskRuntime"
$taskArgs += @((Get-ChildItem -Path (Join-Path $env:WINDIR 'Microsoft.NET/assembly/GAC_MSIL/System.Runtime.WindowsRuntime*') -Recurse -Filter '*.dll') | ForEach-Object { '/reference:' + $_.FullName })
$taskArgs += @(Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'System32/WinMetadata') -Filter '*.winmd' | ForEach-Object { '/reference:' + $_.FullName })
foreach ($taskResource in @('Theme.xaml', 'MainWindow.xaml', 'Overlay.xaml', 'profile.json')) {
    $taskArgs += "/resource:$(Join-Path $PSScriptRoot $taskResource),$taskResource"
}
foreach ($taskIcon in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'icons') -Filter '*.png') {
    $taskArgs += "/resource:$($taskIcon.FullName),GameIcon.$($taskIcon.Name)"
}
$taskArgs += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object FullName)
& $taskCompiler @taskArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
$taskConfig = Join-Path (Split-Path -Parent $PSScriptRoot) 'DeadlockTempo.exe.config'
if ((Split-Path -Parent $taskOutput) -ne (Split-Path -Parent $taskConfig)) { Copy-Item -LiteralPath $taskConfig -Destination ($taskOutput + '.config') }
Write-Output "Built: $taskOutput"
