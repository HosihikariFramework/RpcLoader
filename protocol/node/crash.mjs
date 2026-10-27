export default {
  onLoad(ctx) {
    // 钩子之外死亡：宿主应当按故障卸载并通知载体（ADR-0030）。
    setTimeout(() => process.exit(1), 50);
  },
  onEnable(ctx) {},
  onDisable(ctx) {},
  onUnload(ctx) {},
};
