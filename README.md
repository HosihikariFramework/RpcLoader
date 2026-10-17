# Hosihikari.PluginProvider.Rpc

[English](README.md) | [简体中文](README.zh_hans.md)

The **out-of-process plugin Provider** of the Hosihikari framework (ADR-0031). Script plugins run
as child processes and speak newline-delimited JSON-RPC 2.0 over stdio; the Provider knows no
language and only reads the command in the manifest. Unload means the process really exits.

**It is a plugin package itself**, named `hosihikari-rpc`. When it loads, it registers itself with
the framework from `OnLoad`; packages with `provider: "rpc"` are only handled after that — which is
why out-of-process plugins declare a dependency on it.

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
runtime executable is supplied by the operator (PATH or an absolute path).

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
- **Logging**: stdout carries protocol only; stderr is text logging; structured levels go through
  `hosihikari/log`.
- **Unload**: send `unload`, close stdin, wait 5 seconds, then kill the process tree. The release
  verifier checks that the process has exited.

## Capability surface

The language's standard library plus host services. There is no arbitrary .NET interop; the
type-keyed service registry does not cross the process boundary (there are no shared types). The
data directory is read and written directly; timers belong to the language runtime.

## Running it

```
dotnet run --project host/Hosihikari.PluginProvider.Rpc.Host.csproj -c Release
```

It needs sibling `Hosihikari.PluginManager` and `Hosihikari.Abstractions` checkouts, and Node on
PATH. It proves ten things: the Provider is registered by a plugin package, the hello snapshot
carries layered configuration, enable/disable/unload hooks arrive, asynchronous events arrive by
default, a subscription that declares sync waits for the ack, a refused enable stays loaded, a
refused disable aborts the unload, the plugin unloads once it allows it, and a process that dies
outside a hook is faulted out and reported to the carrier.

## Bindings

`bindings/node` holds the Node reference binding and three test fixtures. Reference bindings for
other languages follow; the protocol does not change.

## Licence

LGPL-3.0-or-later, see [LICENSE.md](LICENSE.md).
