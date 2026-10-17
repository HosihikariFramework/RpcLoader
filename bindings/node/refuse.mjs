import fs from 'node:fs';
import path from 'node:path';

const allowed = (ctx) => fs.existsSync(path.join(ctx.dataDirectory, 'allow.txt'));

export default {
  onLoad(ctx) {},
  onEnable(ctx) {
    return false;
  },
  onDisable(ctx) {
    return allowed(ctx) ? true : { allowed: false, reason: '还不想停' };
  },
  onUnload(ctx) {},
};
