# Installs b-speak on Windows from the latest GitHub release, or updates an existing install.
#   irm https://raw.githubusercontent.com/BlakeHastings/b-speak/main/install.ps1 | iex
# Installs to ~/.bspeak/bin (or $env:BSPEAK_HOME\bin) and adds that folder to your user PATH.

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$repo = 'BlakeHastings/b-speak'
$bspeakHome = if ($env:BSPEAK_HOME) { $env:BSPEAK_HOME } else { Join-Path $HOME '.bspeak' }
$bin = Join-Path $bspeakHome 'bin'
$exe = Join-Path $bin 'bspeak.exe'

# Already installed from a release: let it update itself (it handles files that are in use).
# If that fails (for example an older build with a broken updater), fall through to a fresh install.
if ((Test-Path $exe) -and (Test-Path (Join-Path $bin 'bspeak.release'))) {
    & $exe update
    if ($LASTEXITCODE -eq 0) { exit 0 }
    Write-Output "bspeak update did not succeed; reinstalling from the latest release instead."
}

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$platform = "win-$arch"
$headers = @{ 'User-Agent' = 'b-speak-installer' }
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
$version = $release.tag_name.TrimStart('v')
$name = "bspeak-$version-$platform.zip"
$asset = $release.assets | Where-Object { $_.name -eq $name }
$sums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS' }
if (-not $asset) { throw "b-speak $version has no build for $platform." }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("bspeak-install-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp $name
    Write-Output "Downloading b-speak $version for $platform ..."
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers $headers
    $sumsText = (Invoke-WebRequest $sums.browser_download_url -UseBasicParsing -Headers $headers).Content
    if ($sumsText -is [byte[]]) { $sumsText = [Text.Encoding]::UTF8.GetString($sumsText) }
    $expected = $sumsText -split "`n" | ForEach-Object {
        $parts = $_.Trim() -split '\s+'
        if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $name) { $parts[0] }
    } | Select-Object -First 1
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected -or $actual -ne $expected.ToLowerInvariant()) { throw "The download failed its checksum." }

    # A build from source may be running from this folder: stop its host first.
    if (Test-Path $exe) { & $exe host delete | Out-Null }
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    Expand-Archive -Path $zip -DestinationPath $bin -Force
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (($userPath -split ';') -contains $bin)) {
    [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ";$bin").TrimStart(';'), 'User')
    $env:Path = "$env:Path;$bin"
    Write-Output "Added $bin to your PATH (new terminals pick it up)."
}

Write-Output "b-speak $version installed to $bin."
Write-Output "Next: bspeak setup   (downloads the voice models once and checks they work)"
