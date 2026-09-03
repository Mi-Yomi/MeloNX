# Compile the desktop solution and/or the managed iOS library on Windows.
# The iOS check stops at C# compilation; use Xcode on a Mac to build an IPA.
[CmdletBinding()]
param(
    [ValidateSet('All', 'Desktop', 'IosManaged')]
    [string]$Target = 'All'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetRoot = Join-Path $repositoryRoot '.tools/dotnet'
$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'Run scripts/prepare-build.ps1 first to install the local SDK.'
}

$logsRoot = Join-Path $repositoryRoot 'artifacts/logs'
New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
$buildEnvironment = @{
    DOTNET_ROOT = $dotnetRoot
    PATH = "$dotnetRoot;$env:PATH"
    NUGET_PACKAGES = (Join-Path $repositoryRoot '.tools/nuget/packages')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
}
$savedEnvironment = @{}
Push-Location -LiteralPath $repositoryRoot
try {
    foreach ($name in $buildEnvironment.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $buildEnvironment[$name], 'Process')
    }

    if ($Target -in @('All', 'Desktop')) {
        & $dotnet build Ryujinx.sln -c Release -p:ExtraDefineConstants=DISABLE_UPDATER `
            '-clp:ErrorsOnly;Summary' '-flp:logfile=artifacts/logs/build-windows.log;verbosity=normal'
        if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed; see artifacts/logs/build-windows.log.' }
    }

    if ($Target -in @('All', 'IosManaged')) {
        & $dotnet build src/Ryujinx.Library/Ryujinx.Library.csproj -c Release -r ios-arm64 `
            -p:SelfContained=true -p:ExtraDefineConstants=DISABLE_UPDATER `
            '-clp:ErrorsOnly;Summary' '-flp:logfile=artifacts/logs/build-ios-managed.log;verbosity=normal'
        if ($LASTEXITCODE -ne 0) { throw 'Managed iOS build failed; see artifacts/logs/build-ios-managed.log.' }
        Write-Host 'Managed iOS compilation passed. NativeAOT linking, Swift and device execution require a Mac/iPhone.'
    }
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    Pop-Location
}
