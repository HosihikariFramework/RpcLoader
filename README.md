# Hosihikari.PluginLoader.Rpc

[English](README.md) | [简体中文](README.zh_hans.md)

> **Out-of-process loader.** RpcLoader is an official loader and the one exception to the source-AOT-to-IL rule (ADR-0031). Use it when a plugin cannot be compiled to IL.

The **out-of-process loader** of the Hosihikari framework. Plugins run as child processes
and speak newline-delimited JSON-RPC 2.0 over stdio; the bridge reads only the command in the
manifest and the protocol. Unload means the process really exits.

**It is a plugin package itself**, named `hosihikari-rpc`. When it loads, it registers itself with
the framework from `OnLoad`; packages with `loader: "rpc"` are only handled after that — which is
why bridge plugins declare a dependency on it.

## Templates

[RpcPluginTemplate](https://github.com/HosihikariFramework/RpcPluginTemplate) carries complete
packages for three runtimes:

- [Node.js/TypeScript runtime](https://github.com/HosihikariFramework/RpcPluginTemplate/tree/main/node-typescript)
- [Python runtime](https://github.com/HosihikariFramework/RpcPluginTemplate/tree/main/python)
- [Kotlin/JVM runtime](https://github.com/HosihikariFramework/RpcPluginTemplate/tree/main/kotlin)

## What a plugin package looks like

```
plugins/Hello/
  manifest.json
    {
      "name": "Hello",
      "entry": "main.mjs",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-rpc" }
      ],
      "extraInfo": {
        "api_version": "2",
        "manifest_version": "1",
        "loader": "rpc",
        "command": "[\"node\", \"./hosihikari.mjs\", \"./main.mjs\"]"
      }
    }
  hosihikari.mjs     ← the protocol definition, copied from protocol/node in this repository
  main.mjs
  Hello.conf
```

`extraInfo.command` is a non-empty JSON array encoded as a string — the parent's manifest parser
requires `extraInfo` values to be strings. The loader decodes it and passes the items into the
argument list one by one; there is no shell, and the working directory is the package directory. The
runtime executable is supplied by the operator (PATH or an absolute path). Under Endstone, the CLI
sets the child's PATH to Python's `sys.path`, so name the runtime by absolute path there.

## How a plugin is written

The protocol definition connects a default-exported object to the wire protocol. Hooks may be async; `undefined` /
`true` allow, `false` or `{ allowed: false, reason }` refuse, and a thrown exception is a fault.

```js
import fs from 'node:fs';
import path from 'node:path';

export default {
  onLoad(ctx) {
    fs.writeFileSync(path.join(ctx.dataDirectory, 'body.txt'), 'body:' + ctx.config('greeting'));
    ctx.subscribe('ping', (json) => ctx.logInfo('ping:' + json));
  },
  onEnable(ctx) { },
  onDisable(ctx) { return { allowed: false, reason: 'not yet' }; },
  onUnload(ctx) { },
};
```

`ctx` exposes `name`, `dataDirectory`, `packageDirectory`, `config(key)`, `configObject`,
`logInfo/logWarn/logError`, `writeData`, `publish`, `subscribe(topic, fn, { sync })`, and
`scheduleOnce/schedulePeriodic`.

## Protocol

- **Handshake**: the host sends `hosihikari/hello` with the protocol version, name, data directory,
  package directory and a configuration snapshot; the protocol definition calls `onLoad(ctx)` before replying.
- **Host to plugin**: `hosihikari/enable`, `disable`, `unload`, `events/deliver`.
- **Plugin to host**: `hosihikari/events/publish`, `subscribe`, `unsubscribe`, `log`.
- **Events**: asynchronous by default (the host sends a notification and the publisher does not
  wait); a subscription can declare `sync: true`, in which case the host waits for the ack and
  treats a timeout as a fault.
- **Deadlines**: waiting for the write gate, writing, and waiting for the response share one timeout.
  A timeout terminates the child to release blocked pipe writes; notifications and replies have a
  five-second write timeout. Subscription changes follow their receive order. Synchronous-delivery
  reentry uses a dedicated bounded queue; while waiting for an ack, that thread keeps draining
  nested reentrant requests, with a bounded nesting depth. Subscription changes stay on the ordered
  queue.
- **Logging**: stdout carries protocol only; stderr is text logging; structured levels go through
  `hosihikari/log`.
- **Unload**: send `unload`, close stdin, wait 5 seconds, then kill the process tree. The release
  verifier checks that the process has exited. Dispose stops dispatching queued incoming work,
  cancels the stdout/stderr reads, closes both streams and disposes the process, so a grandchild
  that inherited the pipes does not pin the connection.

## Capability surface

The child process's standard library plus host services. There is no arbitrary .NET interop; the
type-keyed service registry does not cross the process boundary (there are no shared types). The
data directory is read and written directly; timers belong to the child process.

## Running it

```
dotnet run --project host/Hosihikari.PluginLoader.Rpc.Host.csproj -c Release
```

It needs sibling `PluginManager`, the contract checkout (package `Hosihikari.Plugin.Abstractions`) and McMaster fork checkouts,
and Node, `qjs`, Python (`python3`; on Windows `python` or `py -3`), JDK 21 and `kotlinc` on PATH.
It covers: the loader is registered by a plugin package; the Node, QuickJS, Python, Java and
Kotlin protocol definitions each verify the hello snapshot with layered configuration, enable/disable/unload
hooks, asynchronous events by default, the sync ack, timers and publishing back to the host; Java
also covers refused enable/disable and faulting out a process that dies outside a hook, and Kotlin
verifies the same JVM protocol definition from Kotlin.

## Protocol definitions

`protocol/node`, `protocol/quickjs`, `protocol/python` and `protocol/java` hold the reference
protocol definitions, each with test fixtures; `protocol/kotlin` holds the Kotlin fixture for the JVM one.
The Node definition ships `hosihikari.d.ts`, the Python one ships `hosihikari.pyi`, and the QuickJS
one carries JSDoc types. Host requests from the Node and QuickJS definitions time out after 30
seconds; a timed-out request leaves the pending map and rejects.

The QuickJS definition needs `qjs` (quickjs-ng) on PATH or carried in the package; copy
`hosihikari.js` and `stdin_worker.js` next to the entry and point the command at it:

```json
"command": "[\"qjs\", \"./hosihikari.js\", \"./main.js\"]"
```

Its `ctx` writes through `ctx.writeData`; files and timers come from the child process's standard
library (`qjs:std` / `qjs:os`), and `print` / `console` go to stderr.

The Python protocol definition copies `hosihikari.py` next to the entry; the plugin defines module-level
`on_load(ctx)` / `on_enable(ctx)` / `on_disable(ctx)` / `on_unload(ctx)`:

```json
"command": "[\"python3\", \"./hosihikari.py\", \"./main.py\"]"
```

`ctx` provides `name`, `data_directory`, `package_directory`, `config(key)`, `config_object`,
`log_info/log_warn/log_error`, `write_data(file, text)`, `publish(topic, json)`,
`subscribe(topic, fn, sync=False)`, `schedule_once(seconds, fn)` and `schedule_periodic(seconds, fn)`.
`print` goes to stderr; stdout is protocol only.

The JVM protocol definition lives in `protocol/java/hosihikari` and serves Java and Kotlin plugins. A plugin
implements `hosihikari.Plugin` (`onLoad` / `onEnable` / `onDisable` / `onUnload`); hooks return
`HookResult.allow()` / `HookResult.refuse(reason)`. `Context` provides `name()`, `dataDirectory()`,
`packageDirectory()`, `config(key)`, `configObject()`, `logInfo/logWarn/logError`,
`writeData(file, text)`, `publish(topic, json)`, `subscribe(topic, Consumer<String>[, sync])`,
`scheduleOnce(millis, Runnable)` and `schedulePeriodic(millis, Runnable)`. Compile the Java plugin together
with the protocol definition:

```json
"command": "[\"java\", \"-cp\", \"./classes\", \"hosihikari.Binding\", \"hello.HelloPlugin\"]"
```

A Kotlin plugin implements the same interface; compile it against the protocol definition classes and bundle
the Kotlin stdlib (`-include-runtime`). The runtime classpath separator is `:` on Unix and `;` on
Windows:

```json
"command": "[\"java\", \"-cp\", \"./plugin.jar:./classes\", \"hosihikari.Binding\", \"hello.HelloPlugin\"]"
```

The JVM protocol definition needs JDK 21; Kotlin plugins additionally need `kotlinc` at build time, while the
runtime only needs `java` and the packaged jar. Other languages follow on demand; the protocol
does not change.

The process supervision and subscription ordering regressions can also run without Node, QuickJS,
Python, a JDK or Kotlin:

```
dotnet run --project test/Hosihikari.PluginLoader.Rpc.Regression.csproj -c Release
```

This check links the production connection source and sibling Abstractions source. It covers blocked
writes, shared request deadlines, concurrent disposal, notification timeouts, subscription ordering,
and reentrant synchronous event publication. CI runs it alongside the protocol definition assertions.

## Packaging

```
pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
```

It assembles the loader DLL, its private dependencies, the manifest and `protocol/` into
`plugins/hosihikari-rpc/`; the shared layer is not copied. Pushing a `v*` tag attaches the zip to
the GitHub release (ADR-0044).

The script invokes the core packager (`PluginManager/tools/Pack`), which owns the
shared-layer list. Check out the core repository alongside this one, or set
`HOSIHIKARI_PLUGINMANAGER`.

After release: download `hosihikari-rpc-<version>.zip` from the Releases page and extract it at the
server root; install the Shim variant for the carrier first.

## Licence

LGPL-3.0-or-later, see [LICENSE.md](LICENSE.md).
