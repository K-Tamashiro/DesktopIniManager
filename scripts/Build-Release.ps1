[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = '3.0.0'
$packageName = "DesktopIniManager-v$version-DIR-win-x64"
$releaseRoot = Join-Path $repoRoot 'release'
[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
# Always publish into a new directory so old framework/CLI files cannot leak in.
$stage = Join-Path $releaseRoot ('.stage-' + $packageName + '-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $releaseRoot ($packageName + '.zip')
$archiveTemp = Join-Path $releaseRoot ('.' + $packageName + '-' + [guid]::NewGuid().ToString('N') + '.zip')

Push-Location $repoRoot
try {
    & dotnet publish DesktopIniManager.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
        -o $stage -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

    $notes = Join-Path $repoRoot 'docs/releases/v3.0.0-DIR.md'
    Copy-Item -LiteralPath $notes -Destination (Join-Path $stage 'RELEASE_NOTES.md')
    $runtime = Get-Content -LiteralPath (Join-Path $stage 'DesktopIniManager.runtimeconfig.json') -Raw | ConvertFrom-Json
    $assets = Get-Content -LiteralPath (Join-Path $repoRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $packageId = ($framework.name + '.Runtime.win-x64').ToLowerInvariant()
        $packagePath = $null
        foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $cache ($packageId + '/' + $framework.version)
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packagePath = $candidate; break }
        }
        if (!$packagePath) { throw "Runtime license source not found: $packageId" }
        if ($framework.name -eq 'Microsoft.NETCore.App') {
            Copy-Item -LiteralPath (Join-Path $packagePath 'LICENSE.TXT') -Destination (Join-Path $stage 'LICENSE.txt')
            Copy-Item -LiteralPath (Join-Path $packagePath 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $stage 'ThirdPartyNotices.txt')
        }
        elseif ($framework.name -eq 'Microsoft.WindowsDesktop.App') {
            Copy-Item -LiteralPath (Join-Path $packagePath 'LICENSE') -Destination (Join-Path $stage 'LICENSE-WindowsDesktop.txt')
        }
    }
    $required = @(
        'DesktopIniManager.exe', 'DesktopIniManager.dll', 'DesktopIniManager.deps.json',
        'DesktopIniManager.runtimeconfig.json', 'FastVolumeIndex.Core.dll',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll',
        'LICENSE.txt', 'LICENSE-WindowsDesktop.txt', 'ThirdPartyNotices.txt', 'README.md', 'RELEASE_NOTES.md',
        'Assets/folder_set.icl', 'Assets/DeveloperDifferencer_iconset.icl', 'Assets/Flag.icl',
        'docs/images/physical-tree-dark.png', 'docs/images/mft-differencer.png',
        'docs/images/mft-diff-view.png', 'docs/releases/v3.0.0-DIR.md'
    )
    foreach ($relative in $required) {
        if (!(Test-Path -LiteralPath (Join-Path $stage $relative) -PathType Leaf)) {
            throw "Missing package file: $relative"
        }
    }
    if (@(Get-ChildItem (Join-Path $stage 'Languages') -Filter '*.txt').Count -lt 4) {
        throw 'Expected all four language files.'
    }
    foreach ($relative in @('DesktopIniManager.dll','FastVolumeIndex.Core.dll')) {
        $actual = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $stage $relative)).Version
        if ($actual.ToString(3) -ne $version) { throw "Unexpected assembly version in ${relative}: $actual" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $stage 'DesktopIniManager.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (!$runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) {
        throw 'The release must include its runtime rather than require an installed runtime.'
    }
    if (Test-Path -LiteralPath (Join-Path $stage 'mftree.exe')) { throw 'Obsolete CLI found in package.' }
    if (Get-ChildItem $stage -Recurse -File -Filter '*.pdb') { throw 'Debug symbols found in package.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archiveTemp, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::OpenRead($archiveTemp)
    try {
        foreach ($relative in $required) {
            if (!$zip.GetEntry($relative)) { throw "Missing ZIP entry: $relative" }
        }
        $fileCount = @(Get-ChildItem $stage -Recurse -File).Count
        if ($zip.Entries.Count -ne $fileCount) { throw 'ZIP file count does not match publish output.' }
    }
    finally { $zip.Dispose() }

    Move-Item -LiteralPath $archiveTemp -Destination $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($archive + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($archive) + "`n", [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $notes -Destination (Join-Path $releaseRoot 'RELEASE_NOTES_v3.0.0-DIR.md') -Force
    Write-Output "Package: $archive"
    Write-Output "SHA256: $hash"
    Write-Output "Files: $fileCount"
    Write-Output "Published directory: $stage"
}
finally { Pop-Location }
