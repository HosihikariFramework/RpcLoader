# Hosihikari.PluginProvider.Rpc

[English](README.md) | [简体中文](README.zh_hans.md)

> **Exception first-class provider.** RpcProvider is an official provider: the one exception to the source-AOT-to-IL rule (ADR-0031). Use it when a plugin cannot be compiled to IL.

The **exception first-class cross-process bridge** of the Hosihikari framework. Plugins run as child processes
and speak newline-delimited JSON-RPC 2.0 over stdio; the bridge reads only the command in the
manifest and the protocol. Unload means the process really exits.

**It is a plugin package itself**, named `hosihikari-rpc`. When it loads, it registers itself with
the framework from `OnLoad`; packages with `provider: "rpc"` are only handled after that — which is
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
        "api": "2",
        "format_version": "1",
        "provider": "rpc",
        "command": "[\"node\", \"./hosihikari.mjs\", \"./main.mjs\"]"
      }
    }
  hosihikari.mjs     ← the binding, copied from bindings/node in this repository
  main.mjs
  Hello.conf
```

`extraInfo.command` is a non-empty JSON array encoded as a string — the parent's manifest parser
requires `extraInfo` values to be strings. The Provider decodes it and passes the items into the
argument list one by one; there is no shell, and the working directory is the package directory. The
runtime executable is supplied by the operator (PATH or an absolute path). Under Endstone, the CLI
sets the child's PATH to Python's `sys.path`, so name the runtime by absolute path there.

## How a plugin is written

The binding connects a default-exported object to the protocol. Hooks may be async; `undefined` /
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
`runLater/runEvery`.

## Protocol

- **Handshake**: the host sends `hosihikari/hello` with the protocol version, name, data directory,
  package directory and a configuration snapshot; the binding calls `onLoad(ctx)` before replying.
- **Host to plugin**: `hosihikari/enable`, `disable`, `unload`, `events/deliver`.
- **Plugin to host**: `hosihikari/events/publish`, `subscribe`, `unsubscribe`, `log`.
- **Events**: asynchronous by default (the host sends a notification and the publisher does not
  wait); a subscription can declare `sync: true`, in which case the host waits for the ack and
  treats a timeout as a fault.
- **Deadlines**: waiting for the write gate, writing, and waiting for the response share one timeout.
  A timeout terminates the child to release blocked pipe writes; notifications and replies have a
  five-second write timeout. Subscription changes follow their receive order.
- **Logging**: stdout carries protocol only; stderr is text logging; structured levels go through
  `hosihikari/log`.
- **Unload**: send `unload`, close stdin, wait 5 seconds, then kill the process tree. The release
  verifier checks that the process has exited.

## Capability surface

The child process's standard library plus host services. There is no arbitrary .NET interop; the
type-keyed service registry does not cross the process boundary (there are no shared types). The
data directory is read and written directly; timers belong to the child process.

## Running it

```
dotnet run --project host/Hosihikari.PluginProvider.Rpc.Host.csproj -c Release
```

It needs sibling `Hosihikari.PluginManager`, `Hosihikari.Abstractions` and McMaster fork checkouts,
and Node, `qjs`, Python (`python3`; on Windows `python` or `py -3`), JDK 21 and `kotlinc` on PATH.
It covers: the Provider is registered by a plugin package; the Node, QuickJS, Python, Java and
Kotlin bindings each verify the hello snapshot with layered configuration, enable/disable/unload
hooks, asynchronous events by default, the sync ack, timers and publishing back to the host; Java
also covers refused enable/disable and faulting out a process that dies outside a hook, and Kotlin
verifies the same JVM binding from Kotlin.

## Bindings

`bindings/node`, `bindings/quickjs`, `bindings/python` and `bindings/java` hold the reference
bindings, each with test fixtures; `bindings/kotlin` holds the Kotlin fixture for the JVM binding.

The QuickJS binding needs `qjs` (quickjs-ng) on PATH or carried in the package; copy
`hosihikari.js` and `stdin_worker.js` next to the entry and point the command at it:

```json
"command": "[\"qjs\", \"./hosihikari.js\", \"./main.js\"]"
```

Its `ctx` writes through `ctx.writeData`; files and timers come from the child process's standard
library (`qjs:std` / `qjs:os`), and `print` / `console` go to stderr.

The Python binding copies `hosihikari.py` next to the entry; the plugin defines module-level
`on_load(ctx)` / `on_enable(ctx)` / `on_disable(ctx)` / `on_unload(ctx)`:

```json
"command": "[\"python3\", \"./hosihikari.py\", \"./main.py\"]"
```

`ctx` provides `name`, `data_directory`, `package_directory`, `config(key)`, `config_object`,
`log_info/log_warn/log_error`, `write_data(file, text)`, `publish(topic, json)`,
`subscribe(topic, fn, sync=False)`, `run_later(seconds, fn)` and `run_every(seconds, fn)`.
`print` goes to stderr; stdout is protocol only.

The JVM binding lives in `bindings/java/hosihikari` and serves Java and Kotlin plugins. A plugin
implements `hosihikari.Plugin` (`onLoad` / `onEnable` / `onDisable` / `onUnload`); hooks return
`HookResult.allow()` / `HookResult.refuse(reason)`. `Context` provides `name()`, `dataDirectory()`,
`packageDirectory()`, `config(key)`, `configObject()`, `logInfo/logWarn/logError`,
`writeData(file, text)`, `publish(topic, json)`, `subscribe(topic, Consumer<String>[, sync])`,
`runLater(millis, Runnable)` and `runEvery(millis, Runnable)`. Compile the Java plugin together
with the binding:

```json
"command": "[\"java\", \"-cp\", \"./classes\", \"hosihikari.Binding\", \"hello.HelloPlugin\"]"
```

A Kotlin plugin implements the same interface; compile it against the binding classes and bundle
the Kotlin stdlib (`-include-runtime`). The runtime classpath separator is `:` on Unix and `;` on
Windows:

```json
"command": "[\"java\", \"-cp\", \"./plugin.jar:./classes\", \"hosihikari.Binding\", \"hello.HelloPlugin\"]"
```

The JVM binding needs JDK 21; Kotlin plugins additionally need `kotlinc` at build time, while the
runtime only needs `java` and the packaged jar. Other languages follow on demand; the protocol
does not change.

The process supervision and subscription ordering regressions can also run without Node, QuickJS,
Python, a JDK or Kotlin:

```
dotnet run --project test/Hosihikari.PluginProvider.Rpc.Regression.csproj -c Release
```

This check links the production connection source and sibling Abstractions source. It covers blocked
writes, shared request deadlines, concurrent disposal, notification timeouts, subscription ordering,
and reentrant synchronous event publication. CI runs it alongside the binding assertions.

## Packaging

```
pwsh ./pack.ps1 -Version 0.2.3 -OutputRoot artifacts
```

It assembles the provider DLL, its private dependencies, the manifest and `bindings/` into
`hosihikari-rpc/`; the shared layer is not copied. Pushing a `v*` tag attaches the zip to the
GitHub release (ADR-0036).

The script invokes the core packager (`Hosihikari.PluginManager/tools/Pack`), which owns the
shared-layer list. Check out the core repository alongside this one, or set
`HOSIHIKARI_PLUGINMANAGER`.

After release: `lip install github.com/HosihikariFramework/RpcProvider` (install the Shim variant
for the carrier first).

## Licence

LGPL-3.0-or-later, see [LICENSE.md](LICENSE.md).
