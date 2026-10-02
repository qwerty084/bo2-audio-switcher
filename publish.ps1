# Builds dist\bo2audio.exe as a single file. It needs the .NET 10 Desktop Runtime, not the SDK.
# -Version overrides the version in src\Directory.Build.props. The release workflow passes the tag.
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }
dotnet publish (Join-Path $root 'src\bo2audio') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $dist @extra
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Get-ChildItem $dist
