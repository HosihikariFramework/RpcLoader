// 星光框架的进程外绑定（Node）。把行分隔 JSON-RPC 2.0 与插件默认导出的对象接起来。
// stdout 只走协议；console.log 已改道 stderr，插件自己也不要往 stdout 写东西。
import { createInterface } from 'node:readline';
import { pathToFileURL } from 'node:url';
import fs from 'node:fs';
import path from 'node:path';

const entry = process.argv[2];
if (!entry) {
  console.error('usage: node hosihikari.mjs <entry.mjs>');
  process.exit(2);
}

console.log = (...args) => console.error(...args);

const out = process.stdout;
const send = (message) => out.write(JSON.stringify(message) + '\n');
const notify = (method, params) => send({ jsonrpc: '2.0', method, params });

let nextId = 1;
const pending = new Map();
const handlers = new Map();
let plugin = null;
let ctx = null;

function request(method, params) {
  const id = nextId++;
  send({ jsonrpc: '2.0', id, method, params });
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

function makeContext(params) {
  const configObject = params.config ?? {};
  return {
    name: params.name,
    dataDirectory: params.dataDirectory,
    packageDirectory: params.packageDirectory,
    config: (key) => configObject[key] ?? null,
    configObject,
    logInfo: (message) => notify('hosihikari/log', { level: 1, message: String(message) }),
    logWarn: (message) => notify('hosihikari/log', { level: 2, message: String(message) }),
    logError: (message) => notify('hosihikari/log', { level: 3, message: String(message) }),
    writeData: (file, text) => fs.writeFileSync(path.join(params.dataDirectory, file), text),
    publish: (topic, json) => request('hosihikari/events/publish', { topic, json }),
    subscribe: (topic, handler, options = {}) => {
      if (!handlers.has(topic)) {
        handlers.set(topic, []);
        request('hosihikari/events/subscribe', { topic, sync: options.sync === true })
          .catch((error) => console.error('subscribe failed:', error));
      }
      handlers.get(topic).push(handler);
      return () => {
        const list = handlers.get(topic);
        if (!list) return;
        const index = list.indexOf(handler);
        if (index >= 0) list.splice(index, 1);
        if (list.length === 0) {
          handlers.delete(topic);
          request('hosihikari/events/unsubscribe', { topic }).catch(() => {});
        }
      };
    },
    runLater: (ms, fn) => setTimeout(fn, ms),
    runEvery: (ms, fn) => setInterval(fn, ms),
  };
}

function normalize(value) {
  if (value === false) return { allowed: false };
  if (value && typeof value === 'object' && typeof value.allowed === 'boolean') {
    return { allowed: value.allowed, reason: value.reason ?? null };
  }
  return { allowed: true };
}

async function load(params) {
  ctx = makeContext(params);
  const module = await import(pathToFileURL(path.resolve(process.cwd(), entry)).href);
  plugin = module.default ?? module;
  if (!plugin || typeof plugin !== 'object') {
    throw new Error('入口模块的默认导出必须是一个对象');
  }
  await plugin.onLoad?.(ctx);
}

async function handle(message) {
  let result = null;
  switch (message.method) {
    case 'hosihikari/hello':
      await load(message.params ?? {});
      result = { name: ctx.name };
      break;
    case 'hosihikari/enable':
      result = normalize(await plugin?.onEnable?.(ctx));
      break;
    case 'hosihikari/disable':
      result = normalize(await plugin?.onDisable?.(ctx));
      break;
    case 'hosihikari/unload':
      await plugin?.onUnload?.(ctx);
      result = null;
      break;
    case 'hosihikari/events/deliver': {
      const topic = message.params?.topic;
      const json = message.params?.json ?? null;
      for (const handler of handlers.get(topic) ?? []) {
        await handler(json);
      }
      result = null;
      break;
    }
    default:
      throw new Error(`unknown method ${message.method}`);
  }
  return result;
}

const reader = createInterface({ input: process.stdin, crlfDelay: Infinity });
reader.on('line', (line) => {
  if (!line.trim()) return;

  let message;
  try {
    message = JSON.parse(line);
  } catch (error) {
    console.error('bad json from host:', error);
    return;
  }

  if (message.method) {
    handle(message)
      .then((result) => {
        if (message.id !== undefined) {
          send({ jsonrpc: '2.0', id: message.id, result });
        }
      })
      .catch((error) => {
        if (message.id !== undefined) {
          send({
            jsonrpc: '2.0',
            id: message.id,
            error: { code: -32603, message: String(error?.stack ?? error) },
          });
        } else {
          console.error(error);
        }
      });
    return;
  }

  const completion = pending.get(message.id);
  if (!completion) return;
  pending.delete(message.id);
  if (message.error) {
    completion.reject(new Error(message.error.message ?? 'rpc error'));
  } else {
    completion.resolve(message.result);
  }
});

reader.on('close', () => process.exit(0));
