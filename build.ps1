param([string]$Output = (Join-Path $PSScriptRoot 'dist/MonitorSetting'))
$ErrorActionPreference = 'Stop'
$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
$bundledNode = Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'
if ($nodeCommand) { $nodeRuntime = $nodeCommand.Source }
elseif (Test-Path -LiteralPath $bundledNode) { $nodeRuntime = $bundledNode }
else { throw 'Install Node.js to generate the local route bundle.' }
& $nodeRuntime (Join-Path $PSScriptRoot 'scripts/generate-routes.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Generating the local route bundle failed.' }
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue
$portableSdk = Join-Path $env:LOCALAPPDATA 'MonitorSettingBuild/dotnet/dotnet.exe'
if (Test-Path -LiteralPath $portableSdk) { $dotnet = $portableSdk }
elseif ($sdk) { $dotnet = $sdk.Source }
else { throw 'Install the .NET 8 SDK to build MonitorSetting.' }
& $dotnet publish (Join-Path $PSScriptRoot 'native/MonitorSetting.csproj') -c Release -r win-x64 --self-contained true -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw 'The Windows application build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $Output
Write-Output (Join-Path $Output 'MonitorSetting.exe')
