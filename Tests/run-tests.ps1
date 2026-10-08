<#
.SYNOPSIS
Runs the queued event bus test suites or benchmark matrix.

.DESCRIPTION
Runs the selected .NET harness or Unity test platform, checks its exit status,
and writes Unity reports and benchmark output beneath the repository artifacts
folder. Unity runs in batch mode in a hidden window.

.PARAMETER Mode
The validation target. All runs the core, generator, EditMode, and PlayMode suites.
Benchmark, BenchmarkContext, and IL2CPP are separate targets because they have
additional runtime or installed-tooling requirements. BenchmarkContext measures
callbacks that consume publication metadata; Benchmark measures payload-only callbacks.

.PARAMETER Unity
The full path to the Unity editor executable used by Unity validation targets.

.PARAMETER NoDomainReload
Disables domain reload when configuring the PlayMode target, including the
PlayMode stage of All.
#>
param(
    [ValidateSet('Core', 'Generator', 'EditMode', 'PlayMode', 'IL2CPP', 'Benchmark', 'BenchmarkContext', 'All')]
    [string]$Mode = 'All',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe',
    [switch]$NoDomainReload
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repository 'TestProject'
$artifacts = Join-Path $repository 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

function Invoke-DotnetProject
{
    <#
    .SYNOPSIS
    Runs a standalone .NET test harness and rejects unsuccessful exit codes.

    .PARAMETER RelativeProject
    The project path relative to the repository root.

    .PARAMETER ExtraArguments
    Additional arguments forwarded to dotnet run.
    #>
    param(
        [string]$RelativeProject,
        [string[]]$ExtraArguments = @()
    )

    & dotnet run --project (Join-Path $repository $RelativeProject) --configuration Release @ExtraArguments

    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet test harness failed: $RelativeProject ($LASTEXITCODE)"
    }
}

function Invoke-Unity
{
    <#
    .SYNOPSIS
    Runs Unity in hidden batch mode and checks the editor process exit status.

    .PARAMETER Arguments
    Arguments appended after the standard project and log configuration.

    .PARAMETER LogName
    The log filename written inside the repository artifacts folder.
    #>
    param(
        [string[]]$Arguments,
        [string]$LogName
    )

    if (-not (Test-Path -LiteralPath $Unity -PathType Leaf))
    {
        throw "Unity executable missing: $Unity"
    }

    $log = Join-Path $artifacts $LogName
    $unityArguments = @('-batchmode', '-nographics', '-projectPath', $project, '-logFile', $log) + $Arguments

    # Start-Process passes one command line to Unity. Quote paths containing spaces
    # and reject embedded quotes rather than silently changing argument boundaries.
    $quotedArguments = $unityArguments | ForEach-Object {
        if ($_.Contains('"'))
        {
            throw 'Arguments containing double quotes are unsupported.'
        }

        if ($_ -match '\s')
        {
            '"' + $_ + '"'
        }
        else
        {
            $_
        }
    }

    $process = Start-Process -FilePath $Unity -ArgumentList $quotedArguments -WindowStyle Hidden -Wait -PassThru

    if ($process.ExitCode -ne 0)
    {
        throw "Unity failed with exit code $($process.ExitCode). See $log"
    }
}

function Invoke-UnityTests
{
    <#
    .SYNOPSIS
    Runs a Unity test platform and requires a nonempty, fully passing report.

    .PARAMETER Platform
    The Unity test platform, such as EditMode, PlayMode, or StandaloneWindows64.

    .PARAMETER Name
    The report basename used for the XML results and editor log in artifacts.
    #>
    param(
        [string]$Platform,
        [string]$Name
    )

    $results = Join-Path $artifacts ($Name + '.xml')

    # Remove only this run's previous report so a failed launch cannot reuse stale results.
    if (Test-Path -LiteralPath $results)
    {
        Remove-Item -LiteralPath $results
    }

    Invoke-Unity @('-runTests', '-testPlatform', $Platform, '-testResults', $results) ($Name + '.log')

    if (-not (Test-Path -LiteralPath $results))
    {
        throw "Unity produced no results: $results"
    }

    [xml]$report = Get-Content -LiteralPath $results -Raw
    $run = $report.'test-run'

    if ($run.result -ne 'Passed' -or [int]$run.failed -ne 0 -or [int]$run.total -eq 0)
    {
        throw "Unity tests did not pass: $($run.result), $($run.passed) passed, $($run.failed) failed. See $results"
    }

    Write-Host "$Name`: $($run.passed) passed, $($run.failed) failed. Results: $results"
}

if ($Mode -eq 'All' -or $Mode -eq 'Core')
{
    Invoke-DotnetProject 'Tests/QueuedEventBus.Tests/QueuedEventBus.Tests.csproj'
}

if ($Mode -eq 'All' -or $Mode -eq 'Generator')
{
    Invoke-DotnetProject 'Tools/QueuedEventBus.Generator.Tests/QueuedEventBus.Generator.Tests.csproj'
}

if ($Mode -eq 'Benchmark' -or $Mode -eq 'BenchmarkContext')
{
    $benchmarkArgument = if ($Mode -eq 'BenchmarkContext')
    {
        '--benchmark-context'
    }
    else
    {
        '--benchmark'
    }

    $benchmarkFile = if ($Mode -eq 'BenchmarkContext')
    {
        'benchmarks-context.csv'
    }
    else
    {
        'benchmarks.csv'
    }

    $output = & dotnet run --project (Join-Path $repository 'Tests/QueuedEventBus.Tests/QueuedEventBus.Tests.csproj') --configuration Release -- $benchmarkArgument

    if ($LASTEXITCODE -ne 0)
    {
        throw "Benchmark failed ($LASTEXITCODE)"
    }

    $output | Set-Content -LiteralPath (Join-Path $artifacts $benchmarkFile) -Encoding UTF8
    Write-Host "Benchmark results: $(Join-Path $artifacts $benchmarkFile)"
}

if ($Mode -eq 'All' -or $Mode -eq 'EditMode')
{
    Invoke-UnityTests 'EditMode' 'editmode'
}

if ($Mode -eq 'All' -or $Mode -eq 'PlayMode')
{
    $configure = if ($NoDomainReload)
    {
        'PlayerValidation.ConfigureNoDomainReload'
    }
    else
    {
        'PlayerValidation.ConfigureDomainReload'
    }

    Invoke-Unity @('-executeMethod', $configure, '-quit') 'configure-playmode.log'

    $name = if ($NoDomainReload)
    {
        'playmode-no-domain-reload'
    }
    else
    {
        'playmode'
    }

    Invoke-UnityTests 'PlayMode' $name
}

if ($Mode -eq 'IL2CPP')
{
    Invoke-Unity @('-executeMethod', 'PlayerValidation.ConfigureIl2Cpp', '-quit') 'configure-il2cpp.log'
    Invoke-UnityTests 'StandaloneWindows64' 'il2cpp'
}
