import * as std from 'qjs:std';

export default {
  onLoad(ctx) {},
  onEnable(ctx) {
    // 钩子内直接退出：宿主看不到响应，按故障卸载并通知载体（ADR-0030）。
    std.exit(1);
  },
  onDisable(ctx) {},
  onUnload(ctx) {},
};
