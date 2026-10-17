import fs from 'node:fs';
import path from 'node:path';

const write = (ctx, name, text) => fs.writeFileSync(path.join(ctx.dataDirectory, name), text);

export default {
  onLoad(ctx) {
    write(ctx, 'body.txt', 'body:' + ctx.config('greeting'));
    ctx.subscribe('ping', (json) => write(ctx, 'ping.txt', 'ping:' + json));
    ctx.subscribe('sync', (json) => write(ctx, 'sync.txt', 'sync:' + json), { sync: true });
    // 真机没有外部发布者：插件自己发一条，走一遍 publish 与投递。
    ctx.subscribe('self', (json) => write(ctx, 'self.txt', 'self:' + json));
    ctx.subscribe('syncself', (json) => write(ctx, 'syncself.txt', 'syncself:' + json), { sync: true });
    setTimeout(() => ctx.publish('self', '"hello-publish"').catch(() => {}), 500);
    setTimeout(() => ctx.publish('syncself', '"sync-publish"').catch(() => {}), 700);
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
