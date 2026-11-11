param([ValidateSet('win-x64', 'win-arm64', 'win-x86')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\DSAceLogViewer\DSAceLogViewer.csproj'
$destination = Join-Path $PSScriptRoot 'dist'
dotnet publish $project -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Get-Item -LiteralPath (Join-Path $destination 'DSAceLogViewer.exe') | Select-Object FullName, Length
