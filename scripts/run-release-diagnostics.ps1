param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Run-Step([string] $Name, [string] $Program, [string[]] $Arguments) {
        Write-Host "`n== $Name =="
        & $Program @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)" }
    }

    Run-Step 'Formal Release build' 'dotnet' @('build', 'SupportCaseManager.slnx', '-c', 'Release', '--no-restore')
    Run-Step 'Targeted diagnostics tests' 'dotnet' @(
        'test', 'tests/SupportCaseManager.AiAssistant.App.Tests/SupportCaseManager.AiAssistant.App.Tests.csproj',
        '-c', 'Release', '--no-build', '--filter', 'FullyQualifiedName~QuickDiagnosticServiceTests')

    foreach ($project in @(
        'SupportCaseManager.App.Tests',
        'SupportCaseManager.AiAssistant.App.Tests',
        'SupportCaseManager.Ai.Tests',
        'SupportCaseManager.Tests')) {
        Run-Step $project 'dotnet' @('test', "tests/$project/$project.csproj", '-c', 'Release', '--no-build')
    }

    Run-Step 'Full solution tests' 'dotnet' @('test', 'SupportCaseManager.slnx', '-c', 'Release', '--no-build')
    Run-Step 'git diff --check' 'git' @('diff', '--check')
    Write-Host "`nRelease diagnostics: PASS"
}
finally {
    Pop-Location
}
