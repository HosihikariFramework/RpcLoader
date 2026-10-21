import fs from 'node:fs';
import path from 'node:path';

export default {
  onLoad(ctx) {
    // First async, then sync: the second handler must upgrade the topic.
    ctx.subscribe('mixed', () => {});
    ctx.subscribe('mixed', () => {
      fs.writeFileSync(path.join(ctx.dataDirectory, 'mixed-sync.txt'), 'sync');
    }, { sync: true });

    // A self-unsubscribing handler must not skip the next one in the same delivery.
    const off = ctx.subscribe('self', () => {
      fs.writeFileSync(path.join(ctx.dataDirectory, 'self-a.txt'), 'a');
      off();
    });
    ctx.subscribe('self', () => {
      fs.writeFileSync(path.join(ctx.dataDirectory, 'self-b.txt'), 'b');
    });
  },

  onEnable() {
    return true;
  },

  onDisable() {
    return true;
  },

  onUnload() {},
};
