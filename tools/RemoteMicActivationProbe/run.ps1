param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ProbeArgs = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $PSScriptRoot 'RemoteMicActivationProbe.csproj'
$exe = Join-Path $PSScriptRoot 'bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\RemoteMicActivationProbe.exe'

Push-Location $repoRoot
try {
    $build = & dotnet build $project -c Debug -p:Platform=x64 --nologo -v:q 2>&1
    if ($LASTEXITCODE -ne 0) {
        $build | Write-Host
        exit $LASTEXITCODE
    }
    & $exe @ProbeArgs
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
