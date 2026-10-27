#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet('Setup', 'Verify', 'Shell', 'Stop')]
    [string] $Action,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Interactive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This prototype provisioning script currently supports Windows only.' }

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$stateDirectory = Join-Path $repositoryRoot 'artifacts\agent-mode'
$cacheDirectory = Join-Path $stateDirectory 'cache'
$bootstrap = Join-Path $repositoryRoot '.dotnet\dotnet.exe'
$toolDirectory = Join-Path $stateDirectory 'tools'
$buildx = Join-Path $toolDirectory 'dotnet-fastbuild.exe'
$packageVersion = '11.9.10-preview'
$packageSha256 = '7D05F8ADB468576C9D80E7BAA092037B1CAB38DFC7AD9D5B467831367555173B'
$package = Join-Path $toolDirectory ".store\microsoft.fastbuild\$packageVersion\microsoft.fastbuild\$packageVersion\microsoft.fastbuild.$packageVersion.nupkg"
$packagedServer = Join-Path (Split-Path $package) 'tools\net10.0\any\server'
$serverDirectory = Join-Path $stateDirectory "build-server\$packageVersion"
$serverHost = Join-Path $serverDirectory 'fastbuild-server.exe'
$feed = 'https://devdiv.pkgs.visualstudio.com/_packaging/dotnet-core-internal-tooling/nuget/v3/index.json'
$redist = Join-Path $repositoryRoot "artifacts\bin\redist\$Configuration\dotnet"
$hostPath = Join-Path $redist 'dotnet.exe'
$sample = Join-Path $stateDirectory 'sample'
$project = Join-Path $sample 'Sample.csproj'
$savedEnvironment = [Environment]::GetEnvironmentVariables()
$results = [Collections.Generic.List[object]]::new()

function Invoke-Checked([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable exited with code $LASTEXITCODE." }
}

function Invoke-Captured([string] $Executable, [string[]] $Arguments, [string] $Directory) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Directory
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not start $Executable." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Executable exceeded the three-minute smoke-test timeout."
        }
        $streams = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout, $stderr))
        if (-not $streams.Wait(10000)) {
            $process.StandardOutput.Dispose()
            $process.StandardError.Dispose()
            throw "Process $($process.Id) exited with code $($process.ExitCode), but inherited output handles did not close within ten seconds."
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout.GetAwaiter().GetResult()
            StdErr = $stderr.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

function Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-PinnedTool {
    $result = Invoke-Captured $bootstrap @('tool', 'list', '--tool-path', $toolDirectory, '--format', 'json') $repositoryRoot
    Check ($result.ExitCode -eq 0) "Could not inspect the installed tool version: $($result.StdErr)"
    $tools = @((($result.StdOut | ConvertFrom-Json).data) | Where-Object packageId -EQ 'microsoft.fastbuild')
    Check ($tools.Count -eq 1) "The private tool directory must contain Microsoft.FastBuild $packageVersion."
    Check ($tools[0].version -ceq $packageVersion) "Expected Microsoft.FastBuild $packageVersion; found $($tools[0].version). Automatic updates are not supported."
    Check ($tools[0].commands -contains 'dotnet-fastbuild') 'The pinned tool command is missing.'
    Check ((Test-Path -LiteralPath $package) -and (Test-Path -LiteralPath $buildx)) 'The pinned tool installation is incomplete.'
    Check ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ceq $packageSha256) 'The installed package differs from the checked-in SHA-256 pin. Refusing to use it.'
}

function Run-Check([string] $Name, [string[]] $Arguments, [bool] $Dispatch, [bool] $Success = $true) {
    $result = Invoke-Captured $hostPath $Arguments $sample
    $text = $result.StdOut + $result.StdErr
    $text | Set-Content -LiteralPath (Join-Path $stateDirectory "$Name.log")
    Write-Host "[$Name] exit=$($result.ExitCode)"
    Write-Host $text.TrimEnd()
    Check (($result.ExitCode -eq 0) -eq $Success) "$Name returned an unexpected exit code. See $Name.log."
    Check ($text.Contains('Agent mode: using experimental buildx.') -eq $Dispatch) "$Name took the wrong route."
    $results.Add([pscustomobject]@{
        Name = $Name
        ExitCode = $result.ExitCode
        Buildx = $Dispatch
        AotEnabled = $env:DOTNET_CLI_ENABLEAOT
    })
    return $result
}

function Get-SampleServers {
    Get-CimInstance Win32_Process -Filter "Name = 'fastbuild-server.exe'" | Where-Object {
        $_.ExecutablePath -eq $serverHost -and (
            $_.CommandLine.Contains('--workspace "' + $sample + '" ') -or
            $_.CommandLine.Contains('--workspace ' + $sample + ' '))
    }
}

function Stop-SampleServer([switch] $ReportFailureOnly) {
    if ((Test-Path -LiteralPath $buildx) -and (Test-Path -LiteralPath $project)) {
        $owned = @(Get-SampleServers)
        if ($owned.Count -eq 0) { return }
        $result = Invoke-Captured $buildx @($project, '--stop-server') $sample
        if ($result.ExitCode -ne 0) {
            $message = "Could not stop the sample server: $($result.StdErr)"
            if ($ReportFailureOnly) { Write-Warning $message }
            else { throw $message }
        }
    }
}

function Stop-PrivateSdkServers {
    $sdkRoot = Join-Path $redist 'sdk'
    if ((Test-Path -LiteralPath $hostPath) -and (Test-Path -LiteralPath $sdkRoot)) {
        $previousHome = $env:DOTNET_CLI_HOME
        $env:DOTNET_CLI_HOME = Join-Path $stateDirectory 'home'
        try {
            foreach ($sdk in Get-ChildItem -LiteralPath $sdkRoot -Directory) {
                $cli = Join-Path $sdk.FullName 'dotnet.dll'
                if (Test-Path -LiteralPath $cli) {
                    $result = Invoke-Captured $hostPath @('exec', $cli, 'build-server', 'shutdown',
                        '--msbuild') $repositoryRoot
                    Write-Host $result.StdOut.TrimEnd()
                    Check ($result.ExitCode -eq 0) "Could not shut down the private SDK build servers: $($result.StdErr)"
                    # Avoid the combined CLI shutdown's additional scan of shared NuGet compiler toolsets.
                    $compiler = Join-Path $sdk.FullName 'Roslyn\bincore\VBCSCompiler.dll'
                    if (Test-Path -LiteralPath $compiler) {
                        $result = Invoke-Captured $hostPath @('exec', $compiler, '-shutdown') $repositoryRoot
                        Write-Host $result.StdOut.TrimEnd()
                        Check ($result.ExitCode -eq 0) "Could not shut down the private compiler: $($result.StdErr)"
                    }
                }
            }
        }
        finally { $env:DOTNET_CLI_HOME = $previousHome }
    }
}

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_AGENT_MODE = 'off'
    $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = '1'

    if ($Action -eq 'Setup') {
        . (Join-Path $repositoryRoot 'eng\common\tools.ps1')
        InitializeDotNetCli -install $true | Out-Null
        if (Test-Path -LiteralPath $buildx) { Assert-PinnedTool }
        Stop-SampleServer
        Stop-PrivateSdkServers
        Invoke-Checked (Join-Path $repositoryRoot 'build.cmd') @('-c', $Configuration, '-binaryLog',
            '-binaryLogName', 'AgentModeBuildx.binlog')
        if (-not (Test-Path -LiteralPath $package)) {
            $installArguments = @('tool', 'install', 'Microsoft.FastBuild', '--tool-path', $toolDirectory,
                '--add-source', $feed, '--version', "[$packageVersion]", '--no-cache')
            if ($Interactive) { $installArguments += '--interactive' }
            Invoke-Checked $bootstrap $installArguments
        }
        Assert-PinnedTool
        Invoke-Checked $bootstrap @('msbuild', 'src\Cli\dotnet\dotnet.csproj', '-t:UpdateXlf',
            "-p:Configuration=$Configuration", '-nologo', '-v:minimal')
        $rid = 'win-' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
        $template = Get-ChildItem (Join-Path $repositoryRoot ".dotnet\packs\Microsoft.NETCore.App.Host.$rid") -Recurse -Filter apphost.exe |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        Check ($null -ne $template) "The bootstrap SDK apphost template for $rid is missing."
        Invoke-Checked $bootstrap @((Join-Path $PSScriptRoot 'CreatePackagedServerHost.cs'), '--',
            $packagedServer, $template.FullName, $serverDirectory, $bootstrap)
        Write-Host "Provisioned Microsoft.FastBuild $packageVersion. Custom SDK: $hostPath"
        return
    }

    Check ((Test-Path -LiteralPath $package) -and (Test-Path -LiteralPath $hostPath) -and
        (Test-Path -LiteralPath $serverHost)) 'Run Setup successfully first.'
    Assert-PinnedTool
    $properties = Invoke-Captured $bootstrap @('msbuild', 'src\Cli\dotnet\dotnet.csproj',
        "-p:Configuration=$Configuration", '-getProperty:Version,SdkTargetFramework', '-nologo') $repositoryRoot
    Check ($properties.ExitCode -eq 0) "Could not evaluate SDK identity: $($properties.StdErr)"
    $identity = ($properties.StdOut | ConvertFrom-Json).Properties
    $sdkDirectory = Join-Path $redist "sdk\$($identity.Version)"
    Check (Test-Path -LiteralPath (Join-Path $sdkDirectory 'dotnet.dll')) 'The custom SDK layout is incomplete.'
    $bundledVersions = [xml][IO.File]::ReadAllText((Join-Path $sdkDirectory 'Microsoft.NETCoreSdk.BundledVersions.props'))
    $maximumVersion = $bundledVersions.GetElementsByTagName('NETCoreAppMaximumVersion').Item(0).InnerText
    Check ($maximumVersion -match '^\d+\.\d+$') 'Could not determine the consumer framework supported by this SDK.'
    $targetFramework = "net$maximumVersion"

    foreach ($name in @([Environment]::GetEnvironmentVariables().Keys)) {
        if ($name -like 'DOTNET_ROOT*') { [Environment]::SetEnvironmentVariable($name, $null) }
    }
    $env:DOTNET_ROOT = $redist
    $env:PATH = "$redist$([IO.Path]::PathSeparator)$env:PATH"
    $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = '1'
    $env:FASTBUILD_SDK_PATH = $sdkDirectory
    $env:FASTBUILD_SERVER_EXE = $serverHost
    $env:FASTBUILD_CACHE_DIR = $cacheDirectory
    $env:DOTNET_AGENT_DISPATCH_BYPASS = $null
    $env:DOTNET_AGENT_MODE = $null
    $env:DOTNET_AGENT_BUILDX_PATH = $buildx
    $env:DOTNET_CLI_HOME = Join-Path $stateDirectory 'home'

    if ($Action -eq 'Stop') { Stop-SampleServer; Stop-PrivateSdkServers; return }
    if ($Action -eq 'Shell') {
        Check (Test-Path -LiteralPath $project) 'Run Verify to create the isolated sample first.'
        Push-Location $sample
        try {
            Write-Host "Process-scoped custom SDK shell. Run dotnet build; exit to restore the parent environment."
            & (Get-Process -Id $PID).Path -NoLogo -NoProfile -NoExit
        }
        finally { Pop-Location; Stop-SampleServer; Stop-PrivateSdkServers }
        return
    }

    # A previous structural-invalidation fallback must not replace the cold resident-engine proof.
    Stop-SampleServer
    if (Test-Path -LiteralPath $cacheDirectory) {
        Remove-Item -LiteralPath $cacheDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $sample -Force | Out-Null
    foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        '<Project />' | Set-Content -LiteralPath (Join-Path $sample $name)
    }
    '<configuration><packageSources><clear /></packageSources></configuration>' |
        Set-Content -LiteralPath (Join-Path $sample 'NuGet.Config')
    @{ sdk = @{ version = $identity.Version; paths = @('$host$'); rollForward = 'disable'; allowPrerelease = $true } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $sample 'global.json')
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>$targetFramework</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <Target Name="RecordSelectedSdk" AfterTargets="Build">
    <WriteLinesToFile File="`$(IntermediateOutputPath)agent-sdk.txt" Lines="`$(MSBuildSDKsPath)|`$(NETCoreSdkVersion)" Overwrite="true" />
    <WriteLinesToFile File="`$(IntermediateOutputPath)agent-property.txt" Lines="`$(AgentModeProbe)" Overwrite="true" />
  </Target>
  <Target Name="VerifyLongPath" AfterTargets="Build" Condition="'`$(LongPathReference)' != ''">
    <ResolveAssemblyReference Assemblies="`$(LongPathReference)" SearchPaths="{RawFileName}" FindDependencies="false">
      <Output TaskParameter="ResolvedFiles" ItemName="_LongReference" />
    </ResolveAssemblyReference>
    <Error Condition="'@(_LongReference)' == ''" Text="Long assembly path was not resolved." />
    <Copy SourceFiles="@(_LongReference)" DestinationFiles="`$(OutDir)long-reference.dll" />
  </Target>
</Project>
"@ | Set-Content -LiteralPath $project
    $source = Join-Path $sample 'Program.cs'
    $output = Join-Path $sample "bin\Debug\$targetFramework\Sample.dll"
    $agentVariables = [regex]::Matches(
        [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src\Cli\dotnet\Telemetry\LLMEnvironmentDetectorForTelemetry.cs')),
        '"([A-Z][A-Z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    foreach ($name in $agentVariables) { [Environment]::SetEnvironmentVariable($name, $null) }
    $env:AI_AGENT = 'github_copilot_app_agent'
    @{ Status = 'running'; StartedUtc = [DateTime]::UtcNow.ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stateDirectory 'verification.json')
    $serverStopped = $false
    $serverPids = [Collections.Generic.List[int]]::new()

    try {
        Check (Test-Path -LiteralPath (Join-Path $sdkDirectory 'dotnet-aot.dll')) 'NativeAOT product artifact is missing.'
        foreach ($native in @('false', 'true')) {
            $env:DOTNET_CLI_ENABLEAOT = $native
            'System.Console.WriteLine("agent-buildx-initial");' | Set-Content -LiteralPath $source
            $initial = Run-Check "$native-initial" @('build') $true
            Check ($initial.StdOut.Contains('[server]')) 'The initial build did not use the real resident engine.'
            $before = Get-Item -LiteralPath $output
            $beforeTime = $before.LastWriteTimeUtc
            $beforeHash = (Get-FileHash -LiteralPath $output).Hash
            $null = Run-Check "$native-repeat" @('build') $true
            Check ((Get-FileHash -LiteralPath $output).Hash -eq $beforeHash) 'Repeated build changed the output.'
            Check ((Get-Item -LiteralPath $output).LastWriteTimeUtc -eq $beforeTime) 'Repeated build rewrote the assembly.'
            $run = Invoke-Captured $hostPath @($output) $sample
            Check (($run.ExitCode -eq 0) -and ($run.StdOut.Trim() -eq 'agent-buildx-initial')) 'The initial/repeated assembly produced incorrect output.'
            'System.Console.WriteLine("agent-buildx-edited");' | Set-Content -LiteralPath $source
            $edit = Run-Check "$native-edit" @('build', '-c', 'Debug', '--no-restore') $true
            Check ($edit.StdOut.Contains('[server]')) 'The edited build did not use the real resident engine.'
            $run = Invoke-Captured $hostPath @($output) $sample
            Check (($run.ExitCode -eq 0) -and ($run.StdOut.Trim() -eq 'agent-buildx-edited')) 'The edited assembly produced incorrect output.'
            $sdkProof = Get-Content -LiteralPath (Join-Path $sample "obj\Debug\$targetFramework\agent-sdk.txt") -Raw
            Check ($sdkProof.Contains($sdkDirectory) -and $sdkProof.Contains($identity.Version)) 'Buildx used a different SDK.'
            $servers = @(Get-SampleServers)
            Check ($servers.Count -eq 1) 'The real resident server could not be identified.'
            if ($serverPids.Count -gt 0) {
                Check ($servers[0].ProcessId -eq $serverPids[0]) 'The resident server was not reused.'
            }
            $serverPids.Add($servers[0].ProcessId)
        }

        $env:AI_AGENT = $null
        $env:CLAUDECODE = '1'
        $null = Run-Check 'shared-detector-claude' @('build', '--no-restore') $true
        foreach ($name in $agentVariables) { [Environment]::SetEnvironmentVariable($name, $null) }
        $null = Run-Check 'no-agent' @('build') $false
        $env:AI_AGENT = 'github_copilot_app_agent'
        $env:DOTNET_AGENT_MODE = 'off'
        $null = Run-Check 'disabled' @('build') $false
        $env:DOTNET_AGENT_MODE = $null
        $env:DOTNET_AGENT_BUILDX_PATH = $null
        $null = Run-Check 'unprovisioned' @('build') $false
        $env:DOTNET_AGENT_BUILDX_PATH = $buildx
        $null = Run-Check 'supported-arguments' @('build', 'Sample.csproj', '-c', 'Release', '-f', $targetFramework,
            '-v', 'quiet', '--no-restore', '-p:AgentModeProbe=hello world') $true
        $propertyProof = Get-Content -LiteralPath (Join-Path $sample "obj\Release\$targetFramework\agent-property.txt") -Raw
        Check ($propertyProof.Trim() -eq 'hello world') 'Configuration/framework/property argument translation changed a value.'
        $query = Run-Check 'property-query' @('build', '-getProperty:TargetFramework') $false
        Check ($query.StdOut.Trim() -eq $targetFramework) 'The property-query output was polluted.'
        $null = Run-Check 'unsupported-rebuild' @('build', '--no-incremental') $false
        $null = Run-Check 'invalid-verbosity' @('build', '--verbosity', 'invalid') $false $false
        $env:DOTNET_AGENT_BUILDX_PATH = Join-Path $stateDirectory 'missing.exe'
        $null = Run-Check 'invalid-tool-path' @('build') $false $false
        $env:DOTNET_AGENT_BUILDX_PATH = $buildx
        'this is not valid C#;' | Set-Content -LiteralPath $source
        $null = Run-Check 'compiler-failure' @('build') $true $false
        'System.Console.WriteLine("agent-buildx-edited");' | Set-Content -LiteralPath $source
        $null = Run-Check 'recovered-build' @('build') $true
        $longDirectory = Join-Path $stateDirectory ('long-path\' + ('a' * 100) + '\' + ('b' * 100))
        New-Item -ItemType Directory -Path $longDirectory -Force | Out-Null
        $longReference = Join-Path $longDirectory 'LongReference.dll'
        Check ($longReference.Length -gt 260) 'The long-path probe must exceed MAX_PATH.'
        Copy-Item -LiteralPath (Join-Path $packagedServer 'fastbuild-server.dll') -Destination $longReference -Force
        $null = Run-Check 'long-path-reference' @('build', "-p:LongPathReference=$longReference") $true
        $copiedReference = Join-Path $sample "bin\Debug\$targetFramework\long-reference.dll"
        Check ((Get-FileHash -LiteralPath $longReference).Hash -eq (Get-FileHash -LiteralPath $copiedReference).Hash) 'Long-path assembly resolution/copy changed its bytes.'
        Stop-SampleServer
        Check (@(Get-SampleServers).Count -eq 0) 'The sample server did not exit.'
        Stop-PrivateSdkServers
        $serverStopped = $true

        [pscustomobject]@{
            Status = 'passed'
            CompletedUtc = [DateTime]::UtcNow.ToString('O')
            Package = 'Microsoft.FastBuild'
            PackageVersion = $packageVersion
            PackageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
            ServerHost = $serverHost
            ServerHostSha256 = (Get-FileHash -LiteralPath $serverHost -Algorithm SHA256).Hash
            ServerProcessIds = @($serverPids | Select-Object -Unique)
            LongPathReferenceLength = $longReference.Length
            ServerPayload = @(Get-ChildItem -LiteralPath $packagedServer -File | ForEach-Object {
                $copy = Join-Path $serverDirectory $_.Name
                $originalHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                Check ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -eq $originalHash) "Server payload changed: $($_.Name)"
                @{ File = $_.Name; Sha256 = $originalHash }
            })
            SdkVersion = $identity.Version
            SdkDirectory = $sdkDirectory
            TargetFramework = $targetFramework
            SdkTargetFramework = $identity.SdkTargetFramework
            TelemetryOptOut = $env:DOTNET_CLI_TELEMETRY_OPTOUT
            Checks = $results
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stateDirectory 'verification.json')
        Write-Host "Passed $($results.Count) real-package/control checks. Evidence: $stateDirectory\verification.json"
    }
    finally {
        'System.Console.WriteLine("agent-buildx-edited");' | Set-Content -LiteralPath $source
        if (-not $serverStopped) {
            Stop-SampleServer -ReportFailureOnly
            Stop-PrivateSdkServers
        }
    }
}
finally {
    Pop-Location
    foreach ($name in @([Environment]::GetEnvironmentVariables().Keys)) {
        if (-not $savedEnvironment.Contains($name)) { [Environment]::SetEnvironmentVariable($name, $null) }
    }
    foreach ($entry in $savedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
}
