# Builds Streamy's phone-side helper (helper/src) into src/Streamy/Assets/streamy-helper.jar, a jar holding
# classes.dex that app_process runs over adb. The built jar is committed, so only run this after changing the
# helper. Needs a JDK (javac) and the Android SDK (android.jar and d8).
#
#   pwsh tools/build-helper.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }

$platform = Get-ChildItem (Join-Path $sdk 'platforms') -Directory | Sort-Object { [version]($_.Name -replace '^android-', '' -replace '^(\d+)$', '$1.0') } | Select-Object -Last 1
$buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1
$androidJar = Join-Path $platform.FullName 'android.jar'
$d8 = Join-Path $buildTools.FullName 'd8.bat'
if (-not $env:JAVA_HOME -or -not (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) {
    # javac on PATH is often Oracle's javapath shim; ask Java itself where it lives (d8.bat needs JAVA_HOME).
    $props = cmd /c "java -XshowSettings:properties -version 2>&1"
    $env:JAVA_HOME = (($props | Select-String 'java.home = (.+)$').Matches[0].Groups[1].Value).Trim()
}
Write-Host "Using $($platform.Name) and build-tools $($buildTools.Name)"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("streamy-helper-" + [Guid]::NewGuid())
$classes = Join-Path $tmp 'classes'
New-Item -ItemType Directory -Force $classes | Out-Null
try {
    $sources = Get-ChildItem (Join-Path $root 'helper/src') -Recurse -Filter *.java | ForEach-Object FullName
    & javac --release 11 -nowarn -cp $androidJar -d $classes $sources
    if ($LASTEXITCODE -ne 0) { throw 'javac failed' }
    $classFiles = Get-ChildItem $classes -Recurse -Filter *.class | ForEach-Object FullName
    $jar = Join-Path $tmp 'streamy-helper.jar'
    & $d8 --release --min-api 26 --lib $androidJar --output $jar $classFiles
    if ($LASTEXITCODE -ne 0) { throw 'd8 failed' }
    Copy-Item $jar (Join-Path $root 'src/Streamy/Assets/streamy-helper.jar') -Force
    Write-Host "Built src/Streamy/Assets/streamy-helper.jar ($((Get-Item $jar).Length) bytes)"
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
