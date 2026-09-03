# Run from Windows PowerShell 5.1 or PowerShell 7:
# powershell -NoProfile -File scripts/prepare-build.ps1
# The macOS SDK is cached for transfer to a Mac; this script does not build an IPA.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This preparation script installs the Windows x64 SDK. Run it on Windows.'
}
Get-Command git -ErrorAction Stop | Out-Null

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$downloadsRoot = Join-Path $repositoryRoot '.tools/downloads'
$dotnetRoot = Join-Path $repositoryRoot '.tools/dotnet'
$swiftRoot = Join-Path $repositoryRoot '.tools/swift-packages'
$packagesRoot = Join-Path $repositoryRoot '.tools/nuget/packages'
$logsRoot = Join-Path $repositoryRoot 'artifacts/logs'
$sdkVersion = '10.0.400'
$metadataPath = Join-Path $downloadsRoot 'dotnet-10-releases.json'
$metadataUrl = 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'

foreach ($directory in @($downloadsRoot, $swiftRoot, $packagesRoot, $logsRoot)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

function Get-CachedDownload {
    param([string]$Url, [string]$Path, [string]$Sha512)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Host "Downloading $Url"
        $partialPath = "$Path.partial"
        $savedProgressPreference = $ProgressPreference
        $savedSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
        try {
            $ProgressPreference = 'SilentlyContinue'
            [Net.ServicePointManager]::SecurityProtocol = $savedSecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $Url -UseBasicParsing -OutFile $partialPath
        }
        finally {
            $ProgressPreference = $savedProgressPreference
            [Net.ServicePointManager]::SecurityProtocol = $savedSecurityProtocol
        }
        if ($Sha512 -and (Get-FileHash -LiteralPath $partialPath -Algorithm SHA512).Hash -ne $Sha512) {
            throw "SHA512 mismatch for $partialPath. The archive was not installed."
        }
        Move-Item -LiteralPath $partialPath -Destination $Path
    }
    if ($Sha512 -and (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash -ne $Sha512) {
        throw "SHA512 mismatch for cached file $Path. Inspect the file before retrying."
    }
}

function Invoke-CheckedGit {
    param([string[]]$Arguments)
    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Git failed (exit $LASTEXITCODE): git $($Arguments -join ' ')"
    }
}

Get-CachedDownload -Url $metadataUrl -Path $metadataPath
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$sdk = $null
foreach ($release in $metadata.releases) {
    foreach ($candidate in (@($release.sdk) + @($release.sdks))) {
        if ($candidate -and $candidate.version -eq $sdkVersion) {
            $sdk = $candidate
            break
        }
    }
    if ($sdk) { break }
}
if (-not $sdk) {
    throw "SDK $sdkVersion is absent from $metadataPath. Refresh it from $metadataUrl."
}
$runtimeVersion = $sdk.'runtime-version'
if ($runtimeVersion -notmatch '^10\.0\.\d+$') {
    throw "SDK $sdkVersion metadata has an invalid runtime version: '$runtimeVersion'."
}

foreach ($platform in @(
    @{ Rid = 'win-x64'; Extension = 'zip'; Selection = 'sdk-selection.json' },
    @{ Rid = 'osx-arm64'; Extension = 'tar.gz'; Selection = 'sdk-macos-selection.json' }
)) {
    $fileName = "dotnet-sdk-$($platform.Rid).$($platform.Extension)"
    $archiveFile = $sdk.files | Where-Object { $_.rid -eq $platform.Rid -and $_.name -eq $fileName } | Select-Object -First 1
    if (-not $archiveFile -or $archiveFile.hash -notmatch '^[0-9a-fA-F]{128}$') {
        throw "Official metadata has no valid SHA512 entry for $fileName."
    }
    $archiveUri = [Uri]$archiveFile.url
    if ($archiveUri.Scheme -ne 'https' -or $archiveUri.Host -notin @(
        'builds.dotnet.microsoft.com', 'dotnetcli.blob.core.windows.net', 'dotnetcli.azureedge.net'
    )) {
        throw "Unexpected SDK download URL: $archiveUri"
    }
    $archivePath = Join-Path $downloadsRoot "dotnet-sdk-$sdkVersion-$($platform.Rid).$($platform.Extension)"
    Get-CachedDownload -Url $archiveFile.url -Path $archivePath -Sha512 $archiveFile.hash
    @{ version = $sdkVersion; file = $archiveFile } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $downloadsRoot $platform.Selection) -Encoding UTF8
    Write-Host "Verified SHA512: $archivePath"
}

$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    if ((Test-Path -LiteralPath $dotnetRoot) -and @(Get-ChildItem -LiteralPath $dotnetRoot -Force).Count -gt 0) {
        throw "$dotnetRoot contains an incomplete installation. Inspect it before retrying."
    }
    New-Item -ItemType Directory -Path $dotnetRoot -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory(
        (Join-Path $downloadsRoot "dotnet-sdk-$sdkVersion-win-x64.zip"), $dotnetRoot)
}

$lockPath = Join-Path $repositoryRoot 'src/MeloNX/MeloNX.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved'
$swiftLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$manifest = @()
foreach ($pin in $swiftLock.pins) {
    if ($pin.identity -notmatch '^[a-z0-9-]+$' -or $pin.state.revision -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Invalid Swift package identity or revision in $lockPath."
    }
    $packagePath = Join-Path $swiftRoot $pin.identity
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath '.git'))) {
        if ((Test-Path -LiteralPath $packagePath) -and @(Get-ChildItem -LiteralPath $packagePath -Force).Count -gt 0) {
            throw "$packagePath already contains files without a Git checkout."
        }
        New-Item -ItemType Directory -Path $packagePath -Force | Out-Null
        Invoke-CheckedGit -Arguments @('-C', $packagePath, 'init', '-q') | Out-Host
        Invoke-CheckedGit -Arguments @('-C', $packagePath, 'remote', 'add', 'origin', $pin.location) | Out-Host
        Invoke-CheckedGit -Arguments @('-C', $packagePath, 'fetch', '--depth', '1', 'origin', $pin.state.revision) | Out-Host
        Invoke-CheckedGit -Arguments @('-C', $packagePath, 'checkout', '--detach', 'FETCH_HEAD') | Out-Host
    }
    $revision = Invoke-CheckedGit -Arguments @('-C', $packagePath, 'rev-parse', 'HEAD')
    if ($revision -ne $pin.state.revision) {
        throw "$packagePath has revision $revision; expected $($pin.state.revision). Existing checkout was preserved."
    }
    $changes = Invoke-CheckedGit -Arguments @('-C', $packagePath, 'status', '--porcelain')
    if ($changes) {
        throw "$packagePath has local changes. Existing checkout was preserved."
    }
    $manifest += [pscustomobject]@{
        identity = $pin.identity
        url = $pin.location
        revision = $revision
        version = $pin.state.version
        path = $packagePath
        bytes = (Get-ChildItem -LiteralPath $packagePath -Recurse -Force -File | Measure-Object -Property Length -Sum).Sum
    }
    Write-Host "Verified Swift package: $($pin.identity) $revision"
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $swiftRoot 'manifest.json') -Encoding UTF8

# Environment changes apply only to these restore commands, never to the system PATH.
$processEnvironment = @{
    DOTNET_ROOT = $dotnetRoot
    NUGET_PACKAGES = $packagesRoot
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
}
$savedEnvironment = @{}
Push-Location -LiteralPath $repositoryRoot
try {
    foreach ($name in $processEnvironment.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $processEnvironment[$name], 'Process')
    }
    $selectedSdk = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $selectedSdk -ne $sdkVersion) {
        throw "Expected local SDK $sdkVersion, selected '$selectedSdk'. Existing installation was preserved."
    }
    & $dotnet restore Ryujinx.sln | Tee-Object -FilePath (Join-Path $logsRoot 'restore-windows.log')
    if ($LASTEXITCODE -ne 0) { throw 'Ryujinx solution restore failed; see artifacts/logs/restore-windows.log.' }

    & $dotnet restore src/Ryujinx.Library/Ryujinx.Library.csproj -r ios-arm64 -p:SelfContained=true |
        Tee-Object -FilePath (Join-Path $logsRoot 'restore-ios.log')
    if ($LASTEXITCODE -ne 0) {
        throw 'iOS restore failed; see artifacts/logs/restore-ios.log. Retry on macOS if the host is unsupported. Cached SDKs and packages were preserved.'
    }

    # Windows restore does not automatically download the Apple NativeAOT toolchain.
    $appleCacheRoot = Join-Path $repositoryRoot 'artifacts/dependency-cache'
    New-Item -ItemType Directory -Path $appleCacheRoot -Force | Out-Null
    $appleCacheProject = Join-Path $appleCacheRoot 'AppleBuildPacks.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageDownload Include="runtime.osx-arm64.Microsoft.DotNet.ILCompiler" Version="[$runtimeVersion]" />
    <PackageDownload Include="Microsoft.NETCore.App.Runtime.NativeAOT.ios-arm64" Version="[$runtimeVersion]" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $appleCacheProject -Encoding UTF8
    & $dotnet restore $appleCacheProject |
        Tee-Object -FilePath (Join-Path $logsRoot 'restore-apple-packs.log')
    if ($LASTEXITCODE -ne 0) {
        throw 'Apple NativeAOT package restore failed; see artifacts/logs/restore-apple-packs.log.'
    }
    Write-Host 'Build dependencies are ready. Full iOS compilation still requires macOS and Xcode.'
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    Pop-Location
}
