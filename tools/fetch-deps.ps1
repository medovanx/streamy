# Downloads the pinned scrcpy Windows release, checks its SHA-256 and unpacks the files Streamy
# embeds (adb, its two DLLs, scrcpy-server and the FFmpeg/SDL-free decoder DLLs) into
# src/Streamy/ThirdParty. Run it once after cloning and whenever deps.json changes.
#
#   pwsh tools/fetch-deps.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deps = Get-Content (Join-Path $root 'deps.json') -Raw | ConvertFrom-Json
$scrcpy = $deps.scrcpy

$out = Join-Path $root 'src/Streamy/ThirdParty'
$stamp = Join-Path $out '.version'
if ((Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $scrcpy.version)) {
    Write-Host "ThirdParty already holds scrcpy $($scrcpy.version)."
    return
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("streamy-deps-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'scrcpy.zip'
    Write-Host "Downloading $($scrcpy.url)"
    Invoke-WebRequest $scrcpy.url -OutFile $zip -UseBasicParsing

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $scrcpy.sha256) { throw "SHA-256 mismatch: got $hash, expected $($scrcpy.sha256)" }

    $unpacked = Join-Path $tmp 'x'
    Expand-Archive $zip $unpacked
    $dir = Get-ChildItem $unpacked -Directory | Select-Object -First 1

    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Force $out | Out-Null
    foreach ($pattern in $scrcpy.files) {
        $found = Get-ChildItem $dir.FullName -Filter $pattern
        if (-not $found) { throw "Missing $pattern in the scrcpy archive" }
        $found | Copy-Item -Destination $out
    }
    Set-Content $stamp $scrcpy.version -NoNewline
    Write-Host "Unpacked into $out"
    Get-ChildItem $out -File | Where-Object Name -ne '.version' | ForEach-Object { "  $($_.Name)  $([math]::Round($_.Length / 1KB)) KB" }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
