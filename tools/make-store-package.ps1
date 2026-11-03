# Builds the Microsoft Store upload package (.msixupload) for KISSIK.
# The Store signs the package itself, so no certificate is needed here.
# Get the three identity values from Partner Center → your app → Product management → Product identity.
#
#   ./tools/make-store-package.ps1 -IdentityName "12345Krishna.KISSIK" -Publisher "CN=ABCD1234-..." -PublisherDisplayName "Krishna Gaurav" -Version 1.0.0.0
param(
  [Parameter(Mandatory)] [string] $IdentityName,
  [Parameter(Mandatory)] [string] $Publisher,
  [Parameter(Mandatory)] [string] $PublisherDisplayName,
  [string] $Version = "1.0.0.0",
  [string] $Out = "$PSScriptRoot\..\artifacts\store"
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$manifest = Join-Path $root 'src\OpenKamera.App\Package.appxmanifest'
$original = Get-Content $manifest -Raw
try {
  # Stamp the Store identity into the manifest for this build only; the file is restored afterwards.
  [xml]$x = $original
  $x.Package.Identity.Name = $IdentityName
  $x.Package.Identity.Publisher = $Publisher
  $x.Package.Identity.Version = $Version
  $x.Package.Properties.PublisherDisplayName = $PublisherDisplayName
  $x.Save($manifest)

  if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = "C:\Users\Krishna Gaurav\GitHub\tools\dotnet10" }
  $dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
  New-Item -ItemType Directory -Force $Out | Out-Null
  & $dotnet publish (Join-Path $root 'src\OpenKamera.App\OpenKamera.App.csproj') -c Release -r win-x64 `
      -p:GenerateAppxPackageOnBuild=true -p:UapAppxPackageBuildMode=StoreUpload -p:AppxBundle=Never `
      -p:AppxPackageSigningEnabled=false -p:AppxPackageDir="$Out\" -p:AppxSymbolPackageEnabled=false
  if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
  $upload = Get-ChildItem $Out -Recurse -Include *.msixupload, *.appxupload | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $upload) { throw "No .msixupload was produced; check the build output above." }
  "Store package: $($upload.FullName) ($([math]::Round($upload.Length / 1MB, 1)) MB)"
}
finally { Set-Content -Path $manifest -Value $original -NoNewline -Encoding utf8 }
