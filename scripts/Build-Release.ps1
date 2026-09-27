[CmdletBinding()]
param([string]$PrebuiltDirectory)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = '3.5.0'
$releaseVersion = "$version-develop-fffe"
$packageName = "DesktopIniManager-v$releaseVersion-win-x64"
$releaseRoot = Join-Path $repoRoot 'release'
[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
$stage = Join-Path $releaseRoot ('.stage-' + $packageName + '-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $releaseRoot ($packageName + '.zip')
$archiveTemp = Join-Path $releaseRoot ('.' + $packageName + '-' + [guid]::NewGuid().ToString('N') + '.zip')

Push-Location $repoRoot
try {
    if (!$PrebuiltDirectory) {
    # Normal framework-dependent release: do not bundle the .NET runtime.
    & dotnet publish DesktopIniManager.csproj -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
        -o $stage -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    }

    # Keep the framework-dependent runtime layout explicit and include current documentation.
    $expected = @(
        'DesktopIniManager.exe', 'DesktopIniManager.dll', 'DesktopIniManager.deps.json',
        'DesktopIniManager.runtimeconfig.json', 'FastVolumeIndex.Core.dll', 'README.md',
        'Assets/DeveloperDifferencer_iconset.icl', 'Assets/Flag.icl', 'Assets/folder_set.icl',
        'Languages/culture.txt', 'Languages/ja.txt', 'Languages/ko.txt', 'Languages/zh-Hans.txt'
    )
    $expected += @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -Recurse -File | ForEach-Object {
        [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
    })

    if ($PrebuiltDirectory) {
        $source = (Resolve-Path -LiteralPath $PrebuiltDirectory).Path
        foreach ($relative in $expected) {
            # Release documentation must match the current release commit,
            # even when binaries were built before documentation was updated.
            $sourceFile = Join-Path $(if ($relative -eq 'README.md' -or $relative.StartsWith('docs/') -or $relative.StartsWith('Languages/') -or $relative.StartsWith('Assets/')) { $repoRoot } else { $source }) $relative
            if (!(Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "Missing package file: $sourceFile" }
            $destination = Join-Path $stage $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $sourceFile -Destination $destination
        }
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot "docs/releases/v$releaseVersion.md") -Destination (Join-Path $stage 'RELEASE_NOTES.md')
    $expected += 'RELEASE_NOTES.md'

    $actualFiles = @(Get-ChildItem $stage -Recurse -File -Force | ForEach-Object {
        [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
    })
    $difference = Compare-Object ($expected | Sort-Object) ($actualFiles | Sort-Object)
    if ($difference) { throw ("Unexpected release layout: " + ($difference | Out-String)) }
    $assemblyVersions = @{ 'DesktopIniManager.dll' = $version; 'FastVolumeIndex.Core.dll' = '3.1.0' }
    foreach ($relative in $assemblyVersions.Keys) {
        $actual = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $stage $relative)).Version
        if ($actual.ToString(3) -ne $assemblyVersions[$relative]) { throw "Unexpected assembly version in ${relative}: $actual" }
    }
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'DesktopIniManager.dll')).ProductVersion
    if ($productVersion -ne $releaseVersion) { throw "Unexpected prerelease product version: $productVersion" }
    $runtime = Get-Content -LiteralPath (Join-Path $stage 'DesktopIniManager.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.includedFrameworks -or !$runtime.runtimeOptions.frameworks) {
        throw 'Expected a framework-dependent release requiring .NET 10 Desktop Runtime.'
    }
    if (!($runtime.runtimeOptions.frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version.StartsWith('10.') })) {
        throw 'Expected .NET 10 Desktop Runtime.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archiveTemp, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::OpenRead($archiveTemp)
    try {
        if (Compare-Object ($expected | Sort-Object) ($zip.Entries.FullName | Sort-Object)) {
            throw 'ZIP entries do not match the approved release layout.'
        }
    }
    finally { $zip.Dispose() }

    Move-Item -LiteralPath $archiveTemp -Destination $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($archive + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($archive) + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Package: $archive"
    Write-Output "SHA256: $hash"
    Write-Output "Files: $($expected.Count)"
    Write-Output "Published directory: $stage"
}
finally { Pop-Location }
