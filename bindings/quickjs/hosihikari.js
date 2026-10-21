// 星光框架的进程外绑定（QuickJS）。行分隔 JSON-RPC 2.0；stdout 只走协议。
// 主线程跑事件循环；stdin 由 stdin_worker.js 在另一个线程阻塞读取，
// 否则阻塞读会把定时器与 Promise 一起停住。
import * as std from 'qjs:std';
import * as os from 'qjs:os';

/**
 * @typedef {Object} Context
 * @property {string} name
 * @property {string} dataDirectory
 * @property {string} packageDirectory
 * @property {(key: string) => string | null} config
 * @property {Object<string, string>} configObject
 * @property {(message: *) => void} logInfo
 * @property {(message: *) => void} logWarn
 * @property {(message: *) => void} logError
 * @property {(file: string, text: string) => void} writeData
 * @property {(topic: string, json?: string | null) => Promise<*>} publish
 * @property {(topic: string, handler: (json: string | null) => void, options?: {sync?: boolean}) => () => void} subscribe
 * @property {(milliseconds: number, callback: () => void) => *} scheduleOnce
 * @property {(milliseconds: number, callback: () => void) => *} schedulePeriodic
 */

/**
 * @typedef {Object} Plugin
 * @property {(ctx: Context) => void | Promise<void>} [onLoad]
 * @property {(ctx: Context) => *} [onEnable]
 * @property {(ctx: Context) => *} [onDisable]
 * @property {(ctx: Context) => void | Promise<void>} [onUnload]
 */

const entry = scriptArgs[1];
if (!entry) {
  std.err.puts('usage: qjs hosihikari.js <entry.js>\n');
  std.exit(2);
}

globalThis.print = (...args) => std.err.puts(args.map(String).join(' ') + '\n');
globalThis.console = {
  log: (...args) => std.err.puts(args.map(String).join(' ') + '\n'),
  info: (...args) => std.err.puts(args.map(String).join(' ') + '\n'),
  warn: (...args) => std.err.puts(args.map(String).join(' ') + '\n'),
  error: (...args) => std.err.puts(args.map(String).join(' ') + '\n'),
};

const send = (message) => {
  std.out.puts(JSON.stringify(message) + '\n');
  std.out.flush();
};
const notify = (method, params) => send({ jsonrpc: '2.0', method, params });

let nextId = 1;
const pending = new Map();
const REQUEST_TIMEOUT_MS = 30000;
const handlers = new Map();
const syncTopics = new Set();
let plugin = null;
let ctx = null;

function request(method, params) {
  const id = nextId++;
  send({ jsonrpc: '2.0', id, method, params });
  return new Promise((resolve, reject) => {
    const timer = os.setTimeout(() => {
      pending.delete(id);
      reject(new Error(method + ' timed out after ' + REQUEST_TIMEOUT_MS + 'ms'));
    }, REQUEST_TIMEOUT_MS);
    pending.set(id, { resolve, reject, timer });
  });
}

function join(dir, file) {
  return dir.endsWith('/') || dir.endsWith('\\') ? dir + file : dir + '/' + file;
}

function writeData(dir, file, text) {
  const handle = std.open(join(dir, file), 'w');
  if (!handle) {
    throw new Error('cannot open ' + file);
  }
  handle.puts(String(text));
  handle.close();
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
    writeData: (file, text) => writeData(params.dataDirectory, file, text),
    publish: (topic, json) => request('hosihikari/events/publish', { topic, json }),
    subscribe: (topic, handler, options = {}) => {
      const sync = options.sync === true;
      if (!handlers.has(topic)) {
        handlers.set(topic, []);
        if (sync) syncTopics.add(topic);
        request('hosihikari/events/subscribe', { topic, sync }).catch(() => {});
      } else if (sync && !syncTopics.has(topic)) {
        syncTopics.add(topic);
        request('hosihikari/events/subscribe', { topic, sync: true }).catch(() => {});
      }
      handlers.get(topic).push(handler);
      return () => {
        const list = handlers.get(topic);
        if (!list) {
          return;
        }
        const index = list.indexOf(handler);
        if (index >= 0) {
          list.splice(index, 1);
        }
        if (list.length === 0) {
          handlers.delete(topic);
          syncTopics.delete(topic);
          request('hosihikari/events/unsubscribe', { topic }).catch(() => {});
        }
      };
    },
    scheduleOnce: (ms, fn) => os.setTimeout(fn, ms),
    schedulePeriodic: (ms, fn) => os.setInterval(fn, ms),
    clearLater: (id) => os.clearTimeout(id),
    clearEvery: (id) => os.clearInterval(id),
  };
}

function normalize(value) {
  if (value === false) {
    return { allowed: false };
  }
  if (value && typeof value === 'object' && typeof value.allowed === 'boolean') {
    return { allowed: value.allowed, reason: value.reason ?? null };
  }
  return { allowed: true };
}

async function load(params) {
  ctx = makeContext(params);
  const module = await import(entry);
  plugin = module.default ?? module;
  if (!plugin || typeof plugin !== 'object') {
    throw new Error('entry must default-export an object');
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
      for (const handler of [...(handlers.get(topic) ?? [])]) {
        await handler(json);
      }
      result = null;
      break;
    }
    default:
      throw new Error('unknown method ' + message.method);
  }
  return result;
}

const worker = new os.Worker('./stdin_worker.js');
worker.onmessage = (event) => {
  const data = event.data;
  if (data?.eof) {
    std.exit(0);
  }
  if (typeof data?.line !== 'string') {
    return;
  }

  let message;
  try {
    message = JSON.parse(data.line);
  } catch (error) {
    std.err.puts('bad json: ' + String(error) + '\n');
    return;
  }

  if (message.method) {
    handle(message)
      .then((result) => {
        if (message.id !== undefined) {
          send({ jsonrpc: '2.0', id: message.id, result });
        }
        if (message.method === 'hosihikari/unload') {
          std.exit(0);
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
          std.err.puts(String(error?.stack ?? error) + '\n');
        }
      });
    return;
  }

  const completion = pending.get(message.id);
  if (!completion) {
    return;
  }
  pending.delete(message.id);
  os.clearTimeout(completion.timer);
  if (message.error) {
    completion.reject(new Error(message.error.message ?? 'rpc error'));
  } else {
    completion.resolve(message.result);
  }
};
