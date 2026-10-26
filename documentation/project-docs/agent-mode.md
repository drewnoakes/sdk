# .NET Agent Mode

Agent Mode lets the .NET SDK apply toolchain improvements when it detects an agent
session, without requiring the agent to learn replacement commands.

Buildx is the first integration in this prototype, not the definition of Agent Mode.
Additional toolchain improvements can be integrated over time. The goal is a faster
agent development loop while preserving normal SDK selection, correct outputs,
diagnostics, failure behavior, and an explicit opt-out.

## Buildx integration

Ordinary `dotnet build` can use the experimental buildx engine inside an agent session.
This prototype explicitly provisions the internal `Microsoft.FastBuild` package; its
packaged command is still named `dotnet-fastbuild`. There is no automatic acquisition
or update service yet. The eventual public design will acquire and service buildx
independently of SDK releases, using a tool such as `dnx`.

The SDK source baseline for this prototype is `c70b3856e7948678daa3950ec5517759754bf47c`.
Compare the prototype delta against that baseline, not an older fork's `origin/main`.

### Components and invocation paths

`server-host` is a generated prototype directory, **not a component or service added
to the .NET SDK**. It contains a small native launcher, `fastbuild-server.exe`, and
unchanged server files copied from the `Microsoft.FastBuild` package. The launcher
loads the .NET runtime and starts the package's managed `fastbuild-server.dll`; it
does not contain a different build engine or NativeAOT-compiled server.

| Component | Function | Invoked by |
| --- | --- | --- |
| Custom SDK CLI | Detects agent context, checks eligibility, and selects stock or accelerated execution. | The ordinary `dotnet` host after SDK selection; NativeAOT build handling retains its managed fallback. |
| Packaged `dotnet-fastbuild` client | Makes incremental-build decisions and orchestrates work. | The [SDK dispatcher](../../src/Cli/dotnet/AgentModeBuildxDispatcher.cs), with translated arguments and the selected SDK/host context. |
| Generated `fastbuild-server.exe` launcher | Starts the unchanged managed server using the Windows launch workaround described below. | The FastBuild client, through `FASTBUILD_SERVER_EXE`, when a resident server needs starting. |
| Packaged `fastbuild-server.dll` | Hosts MSBuild from the selected SDK and reuses its evaluation/build state across requests. | The launcher at startup; subsequent build requests arrive from the client over a local named pipe. |

The relevant flows are:

1. **Provisioning:** [Prototype.ps1](../../.github/skills/agent-mode-buildx/scripts/Prototype.ps1)
   builds the custom SDK and installs the pinned tool.
   [CreatePackagedServerHost.cs](../../.github/skills/agent-mode-buildx/scripts/CreatePackagedServerHost.cs)
   copies the package's server files and generates the launcher from the bootstrap
   SDK's apphost template. This is an ordinary managed application launch, not another
   SDK entry point.
2. **Accelerated build:** `dotnet build` reaches the SDK dispatcher, which starts the
   packaged client with `--server`. The client connects to an existing compatible
   workspace server or starts the launcher with pipe/workspace/SDK arguments. The
   dispatcher supplies `FASTBUILD_SDK_PATH`; the client forwards it as `--sdk`, and the
   server registers that SDK's MSBuild. Build requests, diagnostics, and results pass
   over the named pipe. The client returns an exit code to the SDK, which returns it
   to the caller without replaying the operation through another engine.
3. **No-op and stock paths:** an up-to-date client can return without compiling or
   starting a server. Unsupported SDK invocations or disabled/unprovisioned Agent Mode
   stay with the SDK handler and do not launch this client or server. The package also
   owns its own decisions to spawn ordinary SDK operations; nested SDK invocations
   inherit the dispatch bypass to prevent recursion.
4. **Shutdown:** the prototype's `Stop` action asks the client to stop this sample's
   server through its pipe and checks that it exits. It separately shuts down the
   private SDK's MSBuild worker/compiler servers; those are not the FastBuild server.
   Without an explicit stop, the FastBuild server has its own idle timeout.

The pinned package sources define [client startup and IPC][fastbuild-client],
[server SDK registration][fastbuild-server], and
[resident MSBuild state and lifetime][fastbuild-loop]. The SDK borrows the apphost
template for this local workaround; ownership of the resident server remains with
FastBuild.

### Setup, run, and cleanup

Use a local checkout of the .NET SDK repository (`dotnet/sdk`) on Windows, PowerShell 7, the
[SDK build prerequisites](developer-guide.md#prerequisites), and existing authorized
Azure Artifacts access to the `dotnet-core-internal-tooling` feed. Never put tokens in
scripts, command lines, or checked-in NuGet configuration.
A regular clone or a Git worktree is suitable; using a worktree is not required.

From the repository root:

```powershell
$prototype = '.github\skills\agent-mode-buildx\scripts\Prototype.ps1'
pwsh -NoProfile -File $prototype Setup
pwsh -NoProfile -File $prototype Verify
pwsh -NoProfile -File $prototype Shell
# In the new shell, inside the isolated sample:
dotnet --version
dotnet build
exit
# Back in the original shell:
pwsh -NoProfile -File $prototype Stop
```

`Setup` installs exactly **Microsoft.FastBuild 11.9.10-preview** from
`https://devdiv.pkgs.visualstudio.com/_packaging/dotnet-core-internal-tooling/nuget/v3/index.json`
into `artifacts\agent-mode\tools`. Add `-Interactive` if the existing credential provider
requires sign-in. It uses the bootstrap SDK pinned by `global.json` and runs
`build.cmd -c Release`, producing `artifacts\bin\redist\Release\dotnet`.
Use `-Configuration Debug` consistently for a Debug environment. Do not copy a different
experiment's SDK or loose accelerator binaries into this layout.

The checked-in `Prototype.ps1` is the pin for this isolated `--tool-path` installation:
`--version [11.9.10-preview]`, with package SHA-256
`7D05F8ADB468576C9D80E7BAA092037B1CAB38DFC7AD9D5B467831367555173B`.
It never runs `tool update` or selects the latest version. Setup and subsequent actions
check the installed tool's reported version and package hash, failing on drift rather
than silently accepting or updating it. Upgrading requires an explicit edit to both
checked-in pins followed by the real-package verification. The repository-wide tool
manifest is not used for this private installation.

The setup also creates a native apphost for the **unchanged packaged server DLLs** under
`artifacts\agent-mode\server-host\11.9.10-preview`, using the bootstrap SDK's apphost
template and `Microsoft.NET.HostModel`. Verification and the child shell explicitly set
`FASTBUILD_SERVER_EXE` to that launcher. This is a local provisioning **mitigation** for
Windows handle inheritance: the package's managed-sidecar launch redirects its three
standard streams, but an additional captured caller pipe can still survive in the
resident process and prevent EOF after the build client exits. The native apphost uses
FastBuild's existing Windows shell-detached launch instead. No accelerator source or
packaged DLL is rebuilt, and verification records their matching SHA-256 hashes.
The launcher copies the SDK muxer's Win32 resources, including its `longPathAware`
manifest, rather than using the bare apphost template's manifest. Verification also
resolves and copies an assembly from a path longer than 260 characters through the
resident server, and observes the same actual server PID across entry modes.

The upstream correction belongs in FastBuild's managed-server launch/qualification:
ensure that no caller capture handles survive daemon creation, not merely that the
three standard streams are replaced. An absolute-host shell-detached launch or a
RID-specific packaged apphost needs upstream qualification. The local launcher is not
that shipping fix. `--no-server` is another supported mitigation but loses the resident
MSBuild benefit; it is deliberately not used by this prototype.
The [packaged launcher source][fastbuild-client]
shows the managed versus native/apphost launch branches at the package's source commit.

`Verify` creates a self-contained sample under `artifacts\agent-mode\sample`, with
`global.json` pinned to the built SDK and repository build imports isolated. It checks
real initial/repeated/edited builds, output execution, SDK identity, both CLI entry modes,
another telemetry-recognized agent, telemetry opt-out, stock controls, and failures.
Each verification resets only its private `artifacts\agent-mode\cache` so the initial
build is a cold resident-engine check, independent of previous demo/cache state.
It retains command logs and exact SDK/package identity in
`artifacts\agent-mode\verification.json`, then stops that sample's resident server.
The sample's framework comes from the built SDK's bundled consumer-version metadata,
not the repository's internal assembly TFM (currently `net11.0` and `net12.0`, respectively).
SDK regression tests remain under `AgentModeBuildxTests`; run them through the
[run-tests workflow](../../.github/skills/run-tests/SKILL.md).

`Shell` changes only a child process's environment. Exit it to undo the configuration.
Agent detection is still required there; a human can set `DOTNET_AGENT_MODE=on` in
that child shell for an explicit local opt-in.
`Stop` addresses this sample workspace's FastBuild server and uses the private SDK's
MSBuild shutdown command plus its exact compiler DLL's `-shutdown` entry point.
It does not enumerate compiler toolsets in the shared NuGet cache. `Setup` also shuts
those down before rebuilding, avoiding Windows file locks on the redist. No global
process-name termination is used. No user PATH, global tool install, shared SDK
installation, or credential configuration is changed. Normal restore may
populate NuGet caches. Generated tools, sample, home, and logs are under
`artifacts\agent-mode`; remove only those specific
subdirectories/files when their evidence is no longer needed.

For a noninteractive agent tool call, use a new process so environment changes cannot
leak into the parent shell:

```powershell
pwsh -NoProfile -Command {
  $repo = (Get-Location).Path
  $env:PATH = "$repo\artifacts\bin\redist\Release\dotnet;$env:PATH"
  $env:DOTNET_AGENT_BUILDX_PATH = "$repo\artifacts\agent-mode\tools\dotnet-fastbuild.exe"
  $env:FASTBUILD_SERVER_EXE = "$repo\artifacts\agent-mode\server-host\11.9.10-preview\fastbuild-server.exe"
  $env:FASTBUILD_CACHE_DIR = "$repo\artifacts\agent-mode\cache"
  $env:DOTNET_AGENT_MODE = 'auto'
  $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
  $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
  $env:DOTNET_CLI_HOME = "$repo\artifacts\agent-mode\home"
  Set-Location "$repo\artifacts\agent-mode\sample"
  dotnet build
  exit $LASTEXITCODE
}
```

After `Stop`, optional file cleanup is confined to these generated directories; keep
the adjacent verification/log files if they are still useful:

```powershell
Remove-Item -LiteralPath artifacts\agent-mode\tools, artifacts\agent-mode\server-host,
  artifacts\agent-mode\sample, artifacts\agent-mode\home, artifacts\agent-mode\cache,
  artifacts\agent-mode\long-path -Recurse -Force
```

### Updating the pinned buildx package

Treat a version change as an explicit compatibility update, not routine demo setup.
A newer feed version is a candidate, not automatically a supported version.

1. **Discover without updating.** Query the authorized feed, including prereleases:

   ```powershell
   $feed = 'https://devdiv.pkgs.visualstudio.com/_packaging/dotnet-core-internal-tooling/nuget/v3/index.json'
   .\.dotnet\dotnet.exe package search Microsoft.FastBuild --source $feed --prerelease --exact-match --format json
   ```

2. **Inspect an isolated candidate.** Deliberately choose an exact version from that
   result. The example below uses the current pin; change it only when evaluating a
   different release. Use an empty candidate directory, leaving the working demo alone:

   ```powershell
   $candidateVersion = '11.9.10-preview'
   $candidateDirectory = Join-Path (Get-Location) "artifacts\agent-mode\candidates\$candidateVersion"
   if (Test-Path -LiteralPath $candidateDirectory) {
     throw 'Choose an empty candidate directory; do not overwrite a previous qualification.'
   }
   .\.dotnet\dotnet.exe tool install Microsoft.FastBuild --tool-path $candidateDirectory `
     --source $feed --version "[$candidateVersion]" --no-cache
   if ($LASTEXITCODE -ne 0) { throw 'Candidate package acquisition failed.' }
   .\.dotnet\dotnet.exe tool list --tool-path $candidateDirectory --format json
   $candidatePackage = Join-Path $candidateDirectory ".store\microsoft.fastbuild\$candidateVersion\microsoft.fastbuild\$candidateVersion\microsoft.fastbuild.$candidateVersion.nupkg"
   Get-FileHash -LiteralPath $candidatePackage -Algorithm SHA256
   & (Join-Path $candidateDirectory 'dotnet-fastbuild.exe') --help
   ```

   Use the existing credential provider (`--interactive` if needed), never an embedded
   token. Inspect the package's source/release changes, tool command, runtime requirement,
   supported arguments, and server payload layout. In particular, this prototype assumes
   `dotnet-fastbuild` and `tools\net10.0\any\server`; a renamed command or changed layout
   requires corresponding implementation changes, not just new version/hash values.
   Reassess whether the Windows server-launch workaround is still necessary.

3. **Preserve rollback, then update the pins.** Before editing, run the existing
   `Prototype.ps1 Stop` with the old pins. Save both old pin values and the last
   successful verification/logs, then move
   `artifacts\agent-mode\tools` to a clearly named, unused backup directory. Retain the
   old version's `server-host` directory. Update `$packageVersion` and `$packageSha256`
   in [Prototype.ps1](../../.github/skills/agent-mode-buildx/scripts/Prototype.ps1) to the
   candidate's exact version and acquired SHA-256. Update this guide and the
   [skill](../../.github/skills/agent-mode-buildx/SKILL.md), including versioned example
   paths. Setup intentionally rejects an installed tool that does not match the pins;
   changing the constants without moving the old installation aside is not an upgrade.

4. **Provision and qualify before a demo.** Run `Setup` to install the new exact pin and
   regenerate its server launcher, then `Verify` to exercise the real package. Require
   the actual resident engine, repeated/edited outputs, selected SDK, supported argument
   mapping, stock controls, failure propagation, capture completion, and cleanup to pass.
   Run affected SDK regression tests through the
   [run-tests workflow](../../.github/skills/run-tests/SKILL.md), especially
   `AgentModeBuildxTests` when invocation or routing changes. Retain the new
   `verification.json` and logs with their version, hash, architecture, and server
   identity. Qualify on every platform that will be used for the demo.

5. **Roll back if qualification fails.** Stop the candidate while its pins are still
   active, move its tool installation aside, restore the previous version/hash values
   and documentation, and move the saved working tool directory back to
   `artifacts\agent-mode\tools`. The versioned old launcher remains available. Re-run
   `Verify` before using the restored demo; do not accept a failing candidate merely
   because it is the newest package.

### Routing contract

The [dispatcher](../../src/Cli/dotnet/AgentModeBuildxDispatcher.cs) runs at the
[managed invocation boundary](../../src/Cli/dotnet/CommandInvocation.cs).
NativeAOT keeps its existing
[managed build fallback](../../src/Cli/dotnet/Parser.cs); no accelerator implementation
or new dependencies are added to the native closure.
Running SDK-side Agent Mode dispatch directly in the native CLI is
[tracked as follow-up work](#nativeaot-sdk-side-agent-mode-dispatch).

| Process-scoped setting | Effect |
| --- | --- |
| `DOTNET_AGENT_BUILDX_PATH` | Absolute trusted installed shim or managed assembly. Nothing is resolved from a repository mapping or downloaded on demand. |
| `DOTNET_AGENT_MODE` unset / `auto` | Route only when provisioned and the [existing telemetry detector](../../src/Cli/dotnet/Telemetry/LLMEnvironmentDetectorForTelemetry.cs) detects an agent. Telemetry upload need not be enabled. |
| `DOTNET_AGENT_MODE=on` | Explicit local opt-in, even without a detected agent. Requires valid provisioning. Boolean `true` / `1` also work. |
| `DOTNET_AGENT_MODE=off` | Stock SDK behavior. Boolean `false` / `0` also work. |
| `DOTNET_AGENT_DISPATCH_BYPASS=1` | Stock behavior in nested SDK invocations; overrides mode validation. |

For a different consumer, prepend the custom redist to **that process's** PATH and set
both the absolute tool path and the scoped `FASTBUILD_SERVER_EXE` launcher override.
Preserve normal `global.json` selection: an older pinned SDK
will not gain this behavior just because another SDK is on PATH. Do not modify the
consumer's SDK policy without permission.

Supported forms are an existing explicit `.csproj`, `.vbproj`, `.sln`, `.slnx`, or
`.slnf`, or an unambiguous default/directory target resolved by the SDK's existing
project/solution helper. Configuration and framework options become MSBuild properties;
verbosity, `--no-restore`, and recognized property switches are preserved. Arguments
are passed as individual process arguments, including values with spaces.

Help, directives, explicit response files, queries, multiple/missing/ambiguous targets,
file-based apps, other verbs, and other switches stay with the SDK **before** any
activation message or tool launch. This intentionally includes output/runtime switches,
explicit targets, binary-log switches, no-incremental, and disabling build servers.
This is a bounded prototype, not a claim of universal build-system compatibility.

On activation the CLI emits a short localized runtime message. It pins the package's
`FASTBUILD_SDK_PATH` to the already selected SDK, supplies the selected host/root to the
child, explicitly selects the packaged server mode, and prevents recursive dispatch.
Standard SDK process/reaper infrastructure preserves streams, working directory,
interruption handling, and the child exit code. A failed accelerator build is **not**
retried through another engine. Resident-server correctness and broader project/custom
target compatibility remain properties of the experimental package.

## Follow-up work

This checklist tracks follow-ups for the current buildx integration, not the full
Agent Mode roadmap or a committed delivery schedule.

### Cross-platform demo setup (macOS first)

**Status:** not started; setup and verification currently support Windows only.
The goal is the same ordinary `dotnet build` demo on macOS, starting with Apple Silicon.
Intel macOS and Linux need separate execution evidence before claiming support.

- [ ] Make [Prototype.ps1](../../.github/skills/agent-mode-buildx/scripts/Prototype.ps1)
  select platform-appropriate executable names, paths, SDK bootstrap/build entry points,
  and NativeAOT library names rather than assuming `.exe`, `build.cmd`, and
  `dotnet-aot.dll`.
- [ ] Replace `Get-CimInstance Win32_Process` with portable server identity/reuse checks,
  retaining workspace-scoped shutdown and confirmation that the owned server exits.
- [ ] Keep the [generated apphost and Win32 manifest workaround](../../.github/skills/agent-mode-buildx/scripts/CreatePackagedServerHost.cs)
  Windows-only. On Unix, qualify the package's managed-server launch path; verify
  captured output reaches EOF promptly, the resident server is reused, and cancellation
  and shutdown work. Do not substitute `--no-server` and call it equivalent evidence.
- [ ] On an actual Mac with PowerShell 7, SDK build prerequisites, and authorized internal
  feed access, acquire the real pinned package and verify initial/repeated/edited builds,
  executed outputs, selected SDK, argument forwarding, telemetry opt-out, stock controls,
  and failure propagation in managed and NativeAOT-enabled entry modes.
- [ ] Update this guide and the [agent skill](../../.github/skills/agent-mode-buildx/SKILL.md)
  with executable Mac setup/run/cleanup instructions and retain exact versions,
  architecture, resident-server identity, and validation results. Keep untested platforms
  explicitly marked as unvalidated.

**Planning estimate:** roughly half a day for the script port and an Apple Silicon
validation pass, assuming Mac access, prerequisites, and feed authentication are ready.
This is not a deadline; an upstream package/daemon issue or additional platform
qualification can extend the work.

### NativeAOT SDK-side Agent Mode dispatch

**Status:** not started; feasibility and performance investigation, not an implemented
native fast path.

The SDK already has a [NativeAOT CLI library](../../src/Cli/dotnet-aot/DESIGN.md)
loaded by the `dotnet` host. The proposal is to compile the shared Agent Mode
detection/eligibility/dispatch logic into that existing native CLI and invoke the
external tool from there. This could avoid initializing CoreCLR and the managed
`dotnet.dll` merely to dispatch an eligible build.

This is distinct from loading the FastBuild engine into the SDK, compiling its
client/server with NativeAOT, or the generated native apphost used by this demo.
The separately provisioned FastBuild client/server would remain out of process and
may still require the managed runtime.

- [ ] Measure the current managed handoff and external-client startup separately to
  establish whether SDK-side native dispatch removes a meaningful cost.
- [ ] Assess the shared [dispatcher](../../src/Cli/dotnet/AgentModeBuildxDispatcher.cs)
  and [invocation boundary](../../src/Cli/dotnet/CommandInvocation.cs) for AOT
  compatibility. Reuse the existing telemetry detector and localized resources;
  do not create a second parser, detector, or policy implementation.
- [ ] Audit the native source/dependency closure. Directory discovery currently calls
  [SolutionAndProjectUtility](../../src/Cli/dotnet/Commands/Test/MTP/SolutionAndProjectUtility.cs),
  whose source file also contains MSBuild-dependent test logic and is excluded from
  [AotSourceFiles.props](../../src/Cli/dotnet-aot/AotSourceFiles.props). Extract/reuse
  the pure discovery helper if needed rather than importing that whole utility.
- [ ] Wire original arguments and authoritative host/selected-SDK context through the
  native entry point. Preserve eligibility, opt-out, recursion bypass, working
  directory, streams, errors, exit codes, and cancellation. Unsupported or
  first-run-dependent cases must retain managed fallback before any tool launch or
  activation message; never retry a tool failure through another engine.
- [ ] Follow the [NativeAOT integration workflow](../../.github/skills/add-dotnet-aot-command/SKILL.md):
  validate a clean native publish, actual redist/native execution, separated SDK
  layout, managed/native contract parity, and native binary-size impact. Prove that
  the eligible request bypassed managed CLI fallback; the existing
  `DOTNET_CLI_ENABLEAOT=true` demo checks alone do not prove native dispatch.
- [ ] Compare whole-command cold, warm/no-op, and edited-build timings with the
  current path, using the same pinned package and observing the actual dispatch
  route and resident-server reuse. Record remaining managed startup costs and any
  size/correctness trade-offs. Treat a speedup as a hypothesis until measured.

[fastbuild-client]: https://devdiv.visualstudio.com/DevDiv/_git/vs-green?path=%2Ffastbuild%2FFastBuild%2FServer%2FServerClient.cs&version=GCadfaba2f64419fc82261665e10e8cb7f90399fbd
[fastbuild-server]: https://devdiv.visualstudio.com/DevDiv/_git/vs-green?path=%2Ffastbuild%2FFastBuild.Server%2FServerHost.cs&version=GCadfaba2f64419fc82261665e10e8cb7f90399fbd
[fastbuild-loop]: https://devdiv.visualstudio.com/DevDiv/_git/vs-green?path=%2Ffastbuild%2FFastBuild.Server%2FBuildLoop.cs&version=GCadfaba2f64419fc82261665e10e8cb7f90399fbd
