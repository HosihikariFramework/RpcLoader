# Hosihikari.PluginLoader.Rpc

[English](README.md) | [简体中文](README.zh_hans.md)

> **进程外加载器。** RpcLoader 是官方加载器，也是源码编译为 IL 规则的唯一例外（ADR-0031）；插件无法编译为 IL 时才用它。

星光框架的**进程外加载器**。插件以子进程运行，协议是行分隔的 JSON-RPC 2.0
over stdio；它只认清单里的命令与协议。卸载就是让进程真正退出。

**它自己也是一个插件包**：`hosihikari-rpc`。装载时在 `OnLoad` 里把自己登记给框架，此后
`loader: "rpc"` 的包才会被它接管 —— 所以进程外插件要在清单里声明依赖它。

## 模板

[RpcPluginTemplate](https://github.com/HosihikariFramework/RpcPluginTemplate) 提供三个运行时的完整
插件包：

- [Node.js/TypeScript 运行时](https://github.com/HosihikariFramework/RpcPluginTemplate/tree/main/node-typescript)
- [Python 运行时](https://github.com/HosihikariFramework/RpcPluginTemplate/tree/main/python)
- [Kotlin/JVM 运行时](https://github.com/HosihikariFramework/RpcPluginTemplate/tree/main/kotlin)

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
        "api_version": "2",
        "manifest_version": "1",
        "loader": "rpc",
        "command": "[\"node\", \"./hosihikari.mjs\", \"./main.mjs\"]"
      }
    }
  hosihikari.mjs     ← 绑定，从本仓 bindings/node 拷来
  main.mjs
  Hello.conf
```

`extraInfo.command` 是**编码成字符串的 JSON 数组**——父级的清单解析要求 `extraInfo` 的值是字符串。
加载器解码后把各项逐个放进参数表；不经 shell，工作目录是包目录。子进程的可执行文件由运维提供
（PATH 或绝对路径）。在 Endstone 下，CLI 会把子进程的 PATH 换成 Python 的 `sys.path`，可执行文件要写绝对路径。

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
`scheduleOnce/schedulePeriodic`。

## 协议

- **握手**：宿主发 `hosihikari/hello`，带协议版本、名字、数据目录、包目录与配置快照；绑定在返回前
  调用 `onLoad(ctx)`。
- **宿主→插件**：`hosihikari/enable` / `disable` / `unload` / `events/deliver`。
- **插件→宿主**：`hosihikari/events/publish` / `subscribe` / `unsubscribe` / `log`。
- **事件**：默认异步（宿主发通知，发布方不等）；订阅声明 `sync: true` 时宿主等 ack，超时即故障。
- **超时**：等待写锁、写入与等待响应共用同一个截止时间；超时终止子进程以解除管道阻塞。
  通知与回复的写入限时五秒，订阅变更按收到的顺序处理；同步投递的重入走独立的有界队列，
  等待 ack 时继续泵送嵌套重入请求，嵌套深度有上限；订阅变更仍留在有序队列上。
- **日志**：stdout 只走协议；stderr 是文本日志；结构化级别走 `hosihikari/log`。
- **卸载**：`unload` 请求 → 关 stdin → 等 5 秒 → 杀进程树；验证器看进程是否退出。Dispose 不再派发
  已排队的入站工作，取消 stdout/stderr 读取并关闭两条流、释放 Process；孙进程继承管道也不会钉住连接。

## 能力面

子进程自带的标准库 + 宿主服务。没有任意 .NET 互操作；类型键的服务注册表不跨进程（没有共享类型）。
数据目录由插件直接读写；定时器用语言自己的。

## 跑一遍

```
dotnet run --project host/Hosihikari.PluginLoader.Rpc.Host.csproj -c Release
```

需要并列的 `PluginManager`、契约仓检出（契约包 `Hosihikari.Plugin.Abstractions`）与 McMaster 的 fork checkout，以及 PATH 上的
Node、`qjs`、Python（`python3`；Windows 上是 `python` 或 `py -3`）、JDK 21 与 `kotlinc`。它覆盖：
加载器由插件包登记；Node、QuickJS、Python、Java 与 Kotlin 绑定各自验证 hello 快照与配置分层、
启用/停用/卸载、默认异步事件、同步 ack、定时器与插件→宿主发布；Java 还覆盖拒绝启用/停用与进程死亡
故障卸载，Kotlin 验证同一份 JVM 绑定在 Kotlin 下的互操作。

## 绑定

`bindings/node`、`bindings/quickjs`、`bindings/python` 与 `bindings/java` 是参考绑定，各带测试夹具；
`bindings/kotlin` 是 JVM 绑定的 Kotlin 夹具。Node 绑定另附 `hosihikari.d.ts`，Python 绑定附
`hosihikari.pyi`，QuickJS 绑定带 JSDoc 类型。Node 与 QuickJS 发往宿主的请求 30 秒超时：超时的请求
移出待办并拒绝。

QuickJS 这一份需要 PATH 上的 `qjs`（quickjs-ng），或随包携带；把 `hosihikari.js` 与 `stdin_worker.js`
拷到入口旁边，命令指向它：

```json
"command": "[\"qjs\", \"./hosihikari.js\", \"./main.js\"]"
```

它的 `ctx` 通过 `ctx.writeData` 写文件；文件与定时器来自 QuickJS 自带标准库（`qjs:std` / `qjs:os`），
`print` / `console` 走 stderr。

Python 这一份把 `hosihikari.py` 拷到入口旁边；插件定义模块级的 `on_load(ctx)` /
`on_enable(ctx)` / `on_disable(ctx)` / `on_unload(ctx)`：

```json
"command": "[\"python3\", \"./hosihikari.py\", \"./main.py\"]"
```

`ctx` 提供 `name`、`data_directory`、`package_directory`、`config(key)`、`config_object`、
`log_info/log_warn/log_error`、`write_data(file, text)`、`publish(topic, json)`、
`subscribe(topic, fn, sync=False)`、`schedule_once(seconds, fn)`、`schedule_periodic(seconds, fn)`。
`print` 走 stderr，stdout 只留给协议。

JVM 绑定在 `bindings/java/hosihikari`，Java 与 Kotlin 插件共用同一份。插件实现
`hosihikari.Plugin`（`onLoad` / `onEnable` / `onDisable` / `onUnload`），钩子返回
`HookResult.allow()` / `HookResult.refuse(reason)`；`Context` 提供 `name()`、`dataDirectory()`、
`packageDirectory()`、`config(key)`、`configObject()`、`logInfo/logWarn/logError`、
`writeData(file, text)`、`publish(topic, json)`、`subscribe(topic, Consumer<String>[, sync])`、
`scheduleOnce(millis, Runnable)`、`schedulePeriodic(millis, Runnable)`。Java 插件与绑定一起编译：

```json
"command": "[\"java\", \"-cp\", \"./classes\", \"hosihikari.Binding\", \"hello.HelloPlugin\"]"
```

Kotlin 插件实现同一接口，编译时把 Java 绑定类放进 classpath，并把 Kotlin 标准库打进 jar；
运行时 classpath 分隔符在 Unix 是 `:`、Windows 是 `;`：

```json
"command": "[\"java\", \"-cp\", \"./plugin.jar:./classes\", \"hosihikari.Binding\", \"hello.HelloPlugin\"]"
```

JVM 绑定需要 JDK 21；Kotlin 插件另需构建期的 `kotlinc`，运行时只要有 `java` 与打好的 jar。
其余语言按需补，协议不变。

进程监督与订阅顺序回归不需要 Node、QuickJS、Python、JDK 或 Kotlin，也可以单独运行：

```
dotnet run --project test/Hosihikari.PluginLoader.Rpc.Regression.csproj -c Release
```

此检查直接编译生产连接源码及并列的 Abstractions 源码，覆盖阻塞写入、请求共用截止时间、
并发释放、通知超时、订阅顺序，以及同步事件处理中的再次发布。CI 同时运行它与绑定断言。

## 打包

```
pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
```

它把加载器 DLL、私有依赖、清单与 `bindings/` 装进 `hosihikari-rpc/`，共享层不随包走；打 `v*` 标签时 CI 把 zip 附到 release（ADR-0036）。

打包脚本调用插件管理器打包器（`PluginManager/tools/Pack`），共享层清单由插件管理器统一给出；需要并列检出插件管理器仓，或设置 `HOSIHIKARI_PLUGINMANAGER`。

发布后：`lip install github.com/HosihikariFramework/RpcLoader`（先按载体装 Shim）。

## 许可

LGPL-3.0-or-later，见 [LICENSE.md](LICENSE.md)。
