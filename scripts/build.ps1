param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\artifacts'))
$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot '..\src\GrokPet\GrokPet.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory --nologo
if($LASTEXITCODE -ne 0){throw 'Build failed'}
