# Hosihikari.PluginProvider.Rpc

[English](README.md) | [简体中文](README.zh_hans.md)

星光框架的**进程外插件 Provider**（ADR-0031）。脚本插件以子进程运行，协议是行分隔的 JSON-RPC 2.0
over stdio；Provider 不认识语言，只认清单里的命令。卸载就是让进程真正退出。

**它自己也是一个插件包**：`hosihikari-rpc`。装载时在 `OnLoad` 里把自己登记给框架，此后
`provider: "rpc"` 的包才会被它接管 —— 所以进程外插件要在清单里声明依赖它。

## 插件包长什么样

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
  hosihikari.mjs     ← 绑定，从本仓 bindings/node 拷来
  main.mjs
  Hello.conf
```

`extraInfo.command` 是**编码成字符串的 JSON 数组**——父级的清单解析要求 `extraInfo` 的值是字符串。
Provider 解码后把各项逐个放进参数表；不经 shell，工作目录是包目录。运行时可执行文件由运维提供
（PATH 或绝对路径）。

## 插件怎么写

绑定把默认导出的对象接到协议上。钩子可以 async；`undefined` / `true` 允许，`false` 或
`{ allowed: false, reason }` 拒绝，抛异常是故障。

```js
import fs from 'node:fs';
import path from 'node:path';

export default {
  onLoad(ctx) {
    fs.writeFileSync(path.join(ctx.dataDirectory, 'body.txt'), 'body:' + ctx.config('greeting'));
    ctx.subscribe('ping', (json) => ctx.logInfo('ping:' + json));
  },
  onEnable(ctx) { },
  onDisable(ctx) { return { allowed: false, reason: '还不想停' }; },
  onUnload(ctx) { },
};
```

`ctx` 提供 `name`、`dataDirectory`、`packageDirectory`、`config(key)`、`configObject`、
`logInfo/logWarn/logError`、`writeData`、`publish`、`subscribe(topic, fn, { sync })`、
`runLater/runEvery`。

## 协议

- **握手**：宿主发 `hosihikari/hello`，带协议版本、名字、数据目录、包目录与配置快照；绑定在返回前
  调用 `onLoad(ctx)`。
- **宿主→插件**：`hosihikari/enable` / `disable` / `unload` / `events/deliver`。
- **插件→宿主**：`hosihikari/events/publish` / `subscribe` / `unsubscribe` / `log`。
- **事件**：默认异步（宿主发通知，发布方不等）；订阅声明 `sync: true` 时宿主等 ack，超时即故障。
- **日志**：stdout 只走协议；stderr 是文本日志；结构化级别走 `hosihikari/log`。
- **卸载**：`unload` 请求 → 关 stdin → 等 5 秒 → 杀进程树；验证器看进程是否退出。

## 能力面

语言标准库 + 宿主服务。没有任意 .NET 互操作；类型键的服务注册表不跨进程（没有共享类型）。
数据目录由插件直接读写；定时器用语言自己的。

## 跑一遍

```
dotnet run --project host/Hosihikari.PluginProvider.Rpc.Host.csproj -c Release
```

需要并列的 `Hosihikari.PluginManager` 与 `Hosihikari.Abstractions` checkout，以及 PATH 上的 Node。
它证明十件事：Provider 由插件包登记、hello 快照与配置分层、启用/停用/卸载钩子到达、默认异步事件、
声明同步的事件等到 ack、拒绝启用留在册、拒绝停用中止卸载、允许后真正卸掉、进程死亡走故障卸载并
通知载体。

## 绑定

`bindings/node` 是 Node 的参考绑定与三个测试夹具。Python / Lua 的参考绑定随后补上；协议不变。

## 许可

LGPL-3.0-or-later，见 [LICENSE.md](LICENSE.md)。
