<#
    Live smoke test for the out-of-process loader, on a LeviLamina server.

    Deploys the current shim + core, stages the loader package and two Node
    plugins (Hello, Crash), drives the server console through show / disable /
    enable / unload / load, and checks the log and the files the plugins wrote.

    Local only: needs a BDS directory with LeviLamina already installed, and Node
    on PATH. ASCII only, like the carrier scripts: Windows PowerShell 5.1 reads a
    BOM-less script in the system codepage.
#>
param(
    [Parameter(Mandatory = $true)][string]$ServerRoot
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$framework = Split-Path $here -Parent
$shim = Join-Path $framework 'HostingShim'
$core = Join-Path $framework 'PluginManager'

$running = Get-Process -Name 'bedrock_server_mod' -ErrorAction SilentlyContinue
if ($running) {
    throw "a server is already running (PID $($running.Id -join ', ')); stop it first"
}

Write-Host '== build core and loader =='
& dotnet build (Join-Path $core 'src\Hosihikari.PluginManager.csproj') -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'the managed core failed to build' }
& dotnet build (Join-Path $here 'src\Hosihikari.PluginLoader.Rpc.csproj') -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'the rpc loader failed to build' }

Write-Host '== deploy the shim =='
& (Join-Path $shim 'carriers\levilamina\deploy.bat') $ServerRoot | Out-Null

# Old api-1 packages from earlier runs would only add rejection noise.
$stash = Join-Path $ServerRoot 'plugins-disabled'
New-Item -ItemType Directory -Force $stash | Out-Null
foreach ($name in @('Bar', 'HelloFs', 'HelloJs', 'hosihikari-fsharp', 'hosihikari-javascript')) {
    $old = Join-Path $ServerRoot "plugins\$name"
    if (Test-Path $old) {
        Move-Item $old (Join-Path $stash $name) -Force
    }
}

function Write-Manifest([string]$Path, [string]$Name, [string]$Entry, [string]$Loader, [object]$Command, [object]$Dependencies) {
    $extra = [ordered]@{ api_version = '2'; manifest_version = '1'; loader = $Loader }
    if ($null -ne $Command) { $extra.command = $Command }
    $manifest = [ordered]@{
        name         = $Name
        entry        = $Entry
        type         = 'hosihikari'
        version      = '1.0.0'
        dependencies = $Dependencies
        extraInfo    = $extra
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $Path -Encoding UTF8
}

Write-Host '== stage the loader package =='
$rpcPackage = Join-Path $ServerRoot 'plugins\hosihikari-rpc'
if (Test-Path $rpcPackage) { Remove-Item $rpcPackage -Recurse -Force }
New-Item -ItemType Directory -Path $rpcPackage | Out-Null
$rpcOut = Join-Path $here 'src\bin\Release\net10.0'
Copy-Item (Join-Path $rpcOut '*.dll') $rpcPackage
Remove-Item (Join-Path $rpcPackage 'Hosihikari.Plugin.Abstractions.dll') -ErrorAction SilentlyContinue
Write-Manifest (Join-Path $rpcPackage 'manifest.json') 'hosihikari-rpc' 'Hosihikari.PluginLoader.Rpc.dll' 'assembly' $null @(@{ name = 'hosihikari' })

Write-Host '== stage the Node plugins =='
function Stage-RpcPlugin([string]$Name, [string]$Entry) {
    $package = Join-Path $ServerRoot "plugins\$Name"
    if (Test-Path $package) { Remove-Item $package -Recurse -Force }
    New-Item -ItemType Directory -Path $package | Out-Null
    Copy-Item (Join-Path $here 'bindings\node\hosihikari.mjs') $package
    Copy-Item (Join-Path $here "bindings\node\$Entry") $package
    $command = '["node","./hosihikari.mjs","./' + $Entry + '"]'
    Write-Manifest (Join-Path $package 'manifest.json') $Name $Entry 'rpc' `
        $command `
        @(@{ name = 'hosihikari' }, @{ name = 'hosihikari-rpc' })
}
Stage-RpcPlugin 'Hello' 'hello.mjs'
Stage-RpcPlugin 'Crash' 'crash.mjs'
Set-Content -Path (Join-Path $ServerRoot 'plugins\Hello\Hello.conf') -Value 'greeting = "package"' -Encoding ASCII

# The operator's override lives outside the package (ADR-0018).
$configDir = Join-Path $ServerRoot 'config\hosihikari'
New-Item -ItemType Directory -Force $configDir | Out-Null
Set-Content -Path (Join-Path $configDir 'Hello.conf') -Value 'greeting = "overridden"' -Encoding ASCII

# Fresh data directories make the assertions unambiguous.
foreach ($name in @('Hello', 'Crash')) {
    $data = Join-Path $ServerRoot "data\hosihikari\$name"
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
}

$exe = Join-Path $ServerRoot 'bedrock_server_mod.exe'
if (-not (Test-Path $exe)) { throw "no bedrock_server_mod.exe under $ServerRoot" }

$steps = @(
    @{ Wait = 30; Line = 'll show Hello' },
    @{ Wait = 3;  Line = 'll show Crash' },
    @{ Wait = 5;  Line = 'll disable Hello' },
    @{ Wait = 5;  Line = 'll enable Hello' },
    @{ Wait = 6;  Line = 'll unload Hello' },
    @{ Wait = 8;  Line = 'll load Hello' }
)

$log = Join-Path $ServerRoot 'smoke-rpc.log'
$driver = Join-Path $env:TEMP 'hosihikari-rpc-smoke.cmd'
$lines = New-Object System.Text.StringBuilder
[void]$lines.AppendLine('@echo off')
[void]$lines.AppendLine('(')
foreach ($step in $steps) {
    [void]$lines.AppendLine("  ping -n $($step.Wait) 127.0.0.1 >nul & echo $($step.Line)")
}
[void]$lines.AppendLine(") | `"$exe`" > `"$log`" 2>&1")
Set-Content -Path $driver -Value $lines.ToString() -Encoding ASCII

Write-Host '== running the server through the lifecycle =='
$run = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "`"$driver`"" `
    -WorkingDirectory $ServerRoot -WindowStyle Hidden -PassThru
$totalWait = ($steps | ForEach-Object { $_.Wait } | Measure-Object -Sum).Sum
Start-Sleep -Seconds ($totalWait + 15)
& taskkill /PID $run.Id /T /F 2>&1 | Out-Null
Start-Sleep -Seconds 2

$text = Get-Content $log -Raw
$helloData = Join-Path $ServerRoot 'data\hosihikari\Hello'
$checks = @(
    @{ Name = 'hello log reaches the parent'; Expect = 'hello loaded' },
    @{ Name = 'show answers';                 Expect = 'Name: Hello' },
    @{ Name = 'disable reaches the hook';     Expect = 'Disable mod Hello successfully' },
    @{ Name = 'enable reaches the hook';      Expect = 'Enable mod Hello successfully' },
    @{ Name = 'unload reclaims it';           Expect = 'Unload mod Hello successfully' },
    @{ Name = 'load brings it back';          Expect = 'Load mod Hello successfully' },
    @{ Name = 'crashed plugin left the ledger'; Expect = 'Mod Crash not found' }
)
foreach ($check in $checks) {
    $ok = $text.Contains($check.Expect)
    Write-Host ("{0}  {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $check.Name)
}

function Check-File([string]$Name, [string]$Expected) {
    $path = Join-Path $helloData $Name
    $actual = if (Test-Path $path) { (Get-Content $path -Raw).Trim() } else { '(missing)' }
    $ok = $actual -eq $Expected
    Write-Host ("{0}  {1}  ({2})" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $actual)
    return $ok
}

$fileChecks = @(
    Check-File 'body.txt' 'body:overridden'
    Check-File 'enabled.txt' 'enabled'
    Check-File 'disabled.txt' 'disabled'
    Check-File 'unloaded.txt' 'unloaded'
    Check-File 'self.txt' 'self:"hello-publish"'
    Check-File 'syncself.txt' 'syncself:"sync-publish"'
)

foreach ($bad in @('Unhandled exception', 'Segmentation fault')) {
    if ($text.Contains($bad)) {
        Write-Host "FAIL  the server died: $bad"
        exit 1
    }
}

$logOk = -not ($checks | Where-Object { -not $text.Contains($_.Expect) })
if (-not $logOk -or $fileChecks -contains $false) {
    Write-Host "see $log"
    exit 1
}

Write-Host "all checks passed; log at $log"
