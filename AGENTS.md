# AGENTS.md

Out-of-process loader for the Hosihikari Framework. Plugins run as child processes and speak
line-delimited JSON-RPC 2.0 over stdio.

## Repository map

- `src/` — process supervision, protocol, and loader registration.
- `host/` — full assertion host, including child processes for each binding.
- `test/` — protocol regression tests and fixtures.
- `bindings/` — Node, Python, Java, Kotlin, and QuickJS bindings.
- `pack.ps1`, `pack.sh` — build the `hosihikari-rpc` lip package.
- `tooth.json` — lip package metadata.

## Build and verify

```bash
dotnet run --project host/Hosihikari.PluginLoader.Rpc.Host.csproj -c Release --nologo
dotnet run --project test/Hosihikari.PluginLoader.Rpc.Regression.csproj -c Release --nologo
pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
```

The host needs sibling `PluginManager`, `Plugin.Abstractions`, and McMaster checkouts, plus Node 24,
`qjs`, Python, JDK 21, and `kotlinc` on `PATH`. Under Endstone, use an absolute runtime path because
the CLI replaces the child `PATH`.

## Invariants

- The package is a plugin package named `hosihikari-rpc`; it registers itself during `OnLoad`.
- Bindings under `bindings/` are copied into `RpcPluginTemplate`; keep both copies identical.
- Queues are bounded; Node and QuickJS host requests time out after 30 seconds; notifications keep
  wire order.
- Child processes receive an argument list, never a shell string. Unload exits the child.
- Public default branches are protected. Work on a branch, open a pull request, and squash merge.

## Context

- [Design record](https://github.com/HosihikariFramework/Design) — GLOSSARY and ADRs.
- [Out-of-process guide](https://hf.srcz.one/authoring-rpc/) and
  [RpcPluginTemplate](https://github.com/HosihikariFramework/RpcPluginTemplate).
