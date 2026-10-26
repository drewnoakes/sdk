---
name: agent-mode-buildx
description: >-
  Provision, run, and verify the local .NET Agent Mode buildx prototype. Use when an
  agent needs ordinary dotnet build to route to the pinned internal Microsoft.FastBuild
  tool through a custom SDK, or needs to reproduce or clean up that environment.
license: MIT
---

# Local Agent Mode with buildx

This is a Windows/PowerShell 7 prototype with explicit internal-package provisioning,
not the future public download/update service. Read the
[setup and behavior contract](../../../documentation/project-docs/agent-mode-buildx.md).

1. From the SDK worktree, run
   `pwsh -NoProfile -File .github\skills\agent-mode-buildx\scripts\Prototype.ps1 Setup`.
   It bootstraps the SDK pinned by `global.json`, installs an exact internal tool version
   into this worktree, builds a Release redist, and creates the documented native apphost
   for the unchanged packaged server. Keep that scoped `FASTBUILD_SERVER_EXE` mitigation;
   otherwise redirected command capture can wait for daemon idle expiry. Add `-Interactive` only when the
   existing Azure Artifacts credential provider needs sign-in. Do not copy tokens or
   add credentials to the repository.
2. Run the same script with `Verify`. Require its real-package builds and output checks
   to pass; the activation banner or a fake provider alone is not end-to-end evidence.
   Inspect `artifacts\agent-mode\verification.json` and the adjacent per-command logs.
3. Keep the model's entry point as `dotnet build`. For an interactive environment, use
   `Shell` after verification; agents should instead use an explicitly scoped process
   environment as documented. Preserve the consumer's SDK selection, and confirm that
   the selected SDK is this custom redist before expecting routing.
4. Use the repository [run-tests](../run-tests/SKILL.md) workflow with the
   `AgentModeBuildxTests` filter for SDK regression tests. The real-package verification
   is additional evidence, not a replacement for those tests.
5. Run `Stop` to stop this sample workspace's resident server and the private SDK's
   MSBuild/compiler servers. Do not terminate other dotnet processes or remove shared
   installations/caches.

The [dispatcher](../../../src/Cli/dotnet/AgentModeBuildxDispatcher.cs) owns eligibility,
argument translation, and child context. It calls the
[telemetry LLM detector](../../../src/Cli/dotnet/Telemetry/LLMEnvironmentDetectorForTelemetry.cs)
directly, independently of telemetry upload. Do not introduce another detection list
or broaden the supported argument set without parity coverage.
