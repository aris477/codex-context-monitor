// A window-selection provider must report explicit UI identity. Usage activity is
// deliberately never consulted here.
export async function readCurrentView(provider, { now = Date.now(), timeoutMs = 1500 } = {}) {
  const checkedAt = new Date(now).toISOString();
  const unavailable = (reason) => ({ status: 'unavailable', reason, checkedAt });
  if (typeof provider !== 'function') return unavailable('尚未连接 Codex 当前聊天识别');
  const controller = new AbortController();
  let timer;
  try {
    const result = await Promise.race([
      Promise.resolve().then(() => provider({ signal: controller.signal })),
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('timeout')), timeoutMs); }),
    ]);
    if (!result || !['identified', 'not-a-thread', 'unavailable', 'ambiguous', 'stale'].includes(result.status)) {
      return unavailable('聊天识别返回无效状态');
    }
    const source = typeof result.source === 'string' ? result.source : '';
    const observed = Date.parse(result.observedAt);
    const expires = Date.parse(result.expiresAt);
    const identity = {
      threadId: typeof result.threadId === 'string' ? result.threadId : null,
      name: typeof result.name === 'string' ? result.name.slice(0, 1000) : null,
      hostId: typeof result.hostId === 'string' ? result.hostId : null,
      windowId: typeof result.windowId === 'string' ? result.windowId : null,
      source, checkedAt,
      observedAt: Number.isFinite(observed) ? new Date(observed).toISOString() : null,
      expiresAt: Number.isFinite(expires) ? new Date(expires).toISOString() : null,
    };
    if (result.status === 'identified') {
      if (!identity.threadId || !identity.hostId || !identity.windowId || !source) return unavailable('聊天识别缺少明确窗口身份');
      if (!Number.isFinite(observed) || !Number.isFinite(expires) || observed > now || expires <= observed || expires - observed > 15000) {
        return unavailable('聊天识别缺少有效的新鲜度信息');
      }
      if (expires <= now) return { ...identity, status: 'stale', reason: '当前聊天识别已过期' };
    }
    return { ...identity, status: result.status, reason: typeof result.reason === 'string' ? result.reason : '' };
  } catch {
    return unavailable('当前聊天识别连接中断');
  } finally {
    clearTimeout(timer);
    controller.abort();
  }
}
