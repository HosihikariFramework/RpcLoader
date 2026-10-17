import fs from 'node:fs';
import path from 'node:path';

const write = (ctx, name, text) => fs.writeFileSync(path.join(ctx.dataDirectory, name), text);

export default {
  onLoad(ctx) {
    write(ctx, 'body.txt', 'body:' + ctx.config('greeting'));
    ctx.subscribe('ping', (json) => write(ctx, 'ping.txt', 'ping:' + json));
    ctx.subscribe('sync', (json) => write(ctx, 'sync.txt', 'sync:' + json), { sync: true });
    ctx.logInfo('hello loaded');
  },
  onEnable(ctx) {
    write(ctx, 'enabled.txt', 'enabled');
  },
  onDisable(ctx) {
    write(ctx, 'disabled.txt', 'disabled');
  },
  onUnload(ctx) {
    write(ctx, 'unloaded.txt', 'unloaded');
  },
};
