import * as std from 'qjs:std';

const allowed = (ctx) => std.loadFile(ctx.dataDirectory + '/allow.txt') !== null;

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
