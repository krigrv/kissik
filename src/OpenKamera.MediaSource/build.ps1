# Builds OpenKamera.VirtualCamera.dll (x64) and the test harness with the VS 2022 C++ build tools.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$out = Join-Path $here "bin\$Configuration"
New-Item -ItemType Directory -Force $out | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "VS C++ build tools not found" }

$opt = if ($Configuration -eq "Debug") { "/Od /Zi /MTd" } else { "/O2 /Zi /MT /DNDEBUG" }
$common = "/nologo /std:c++20 /EHsc /W4 /permissive- /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN $opt"
$libs = "mfplat.lib mfuuid.lib mf.lib ole32.lib advapi32.lib gdi32.lib user32.lib runtimeobject.lib"
$srcs = "DllMain.cpp MediaSource.cpp MediaStream.cpp FrameSource.cpp"

$cmd = @"
call "$vcvars" >nul
cd /d "$here"
cl $common /LD $srcs /Fe:"$out\OpenKamera.VirtualCamera.dll" /Fo:"$out\\" /Fd:"$out\\" /link /DEF:OpenKamera.VirtualCamera.def $libs || exit /b 1
cl $common tests\Harness.cpp /Fe:"$out\VirtualCameraHarness.exe" /Fo:"$out\\" /Fd:"$out\harness.pdb" /link $libs || exit /b 1
"@
$bat = Join-Path $env:TEMP "okvc-build.cmd"
Set-Content $bat $cmd -Encoding ascii
cmd /c $bat
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
Get-ChildItem $out | Where-Object { $_.Extension -in ".dll",".exe" } | ForEach-Object { "built $($_.FullName)" }
