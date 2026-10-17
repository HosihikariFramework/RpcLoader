const write = (ctx, name, text) => ctx.writeData(name, text);

export default {
  onLoad(ctx) {
    write(ctx, 'body.txt', 'body:' + ctx.config('greeting'));
    ctx.subscribe('ping', (json) => write(ctx, 'ping.txt', 'ping:' + json));
    ctx.subscribe('sync', (json) => write(ctx, 'sync.txt', 'sync:' + json), { sync: true });
    ctx.subscribe('self', (json) => write(ctx, 'self.txt', 'self:' + json));
    ctx.subscribe('syncself', (json) => write(ctx, 'syncself.txt', 'syncself:' + json), { sync: true });
    ctx.runLater(500, () => ctx.publish('self', '"quick-publish"').catch(() => {}));
    ctx.runLater(700, () => ctx.publish('syncself', '"quick-sync"').catch(() => {}));
    ctx.logInfo('quick hello loaded');
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
