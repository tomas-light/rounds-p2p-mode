param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root '.cache\dotnet'
$env:NUGET_PACKAGES = Join-Path $root '.cache\nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $root '.cache\nuget-http'
$env:NUGET_SCRATCH = Join-Path $root '.cache\nuget-scratch'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& dotnet @Arguments
exit $LASTEXITCODE
