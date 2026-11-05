const PAGE_SIZE = 40;
const escape = (value) => String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const tokens = (value) => Number.isFinite(value) ? value.toLocaleString('zh-CN') : '—';
const time = (value) => value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '暂无记录';
const title = (s) => s.name || s.title || '对话 ' + s.id.slice(0, 8);

// Keep live nodes (including the browser's scroll anchor and focused controls)
// when only a counter or timestamp changes.
const renderedMarkup = new WeakMap();
function updateMarkup(target, markup) {
  if (renderedMarkup.get(target) === markup) return;
  const template = document.createElement('template');
  template.innerHTML = markup;
  function patch(parent, next) {
    for (let i = 0; i < next.childNodes.length; i++) {
      const desired = next.childNodes[i];
      const existing = parent.childNodes[i];
      if (!existing) { parent.append(desired.cloneNode(true)); continue; }
      if (existing.nodeType !== desired.nodeType || existing.nodeName !== desired.nodeName) {
        existing.replaceWith(desired.cloneNode(true)); continue;
      }
      if (existing.nodeType === 3) {
        if (existing.nodeValue !== desired.nodeValue) existing.nodeValue = desired.nodeValue;
      } else if (existing.nodeType === 1) {
        for (const attr of [...existing.attributes]) {
          if (!desired.hasAttribute(attr.name) && !(existing.tagName === 'DETAILS' && attr.name === 'open')) existing.removeAttribute(attr.name);
        }
        for (const attr of desired.attributes) {
          if (existing.getAttribute(attr.name) !== attr.value) existing.setAttribute(attr.name, attr.value);
        }
        patch(existing, desired);
      }
    }
    while (parent.childNodes.length > next.childNodes.length) parent.lastChild.remove();
  }
  patch(target, template.content);
  renderedMarkup.set(target, markup);
}

function updateRows(target, sessions) {
  const existing = new Map([...target.children].map((row) => [row.dataset.contextRow, row]));
  if (!sessions.length) { updateMarkup(target, renderContextRows([])); return; }
  const keep = new Set();
  sessions.forEach((session, index) => {
    let row = existing.get(session.id);
    if (!row) { row = document.createElement('tr'); row.dataset.contextRow = session.id; }
    const template = document.createElement('template');
    template.innerHTML = renderContextRows([session]);
    updateMarkup(row, template.content.firstElementChild.innerHTML);
    keep.add(row);
    if (target.children[index] !== row) target.insertBefore(row, target.children[index] || null);
  });
  for (const row of [...target.children]) if (!keep.has(row)) row.remove();
}

export function filterContextSessions(sessions, { query = '', scope = 'all', sort = 'lastAt', now = Date.now() } = {}) {
  const needle = query.trim().toLocaleLowerCase();
  return sessions.filter((s) => {
    if (scope === 'main' && s.sessionKind === 'agent') return false;
    if (scope === 'agents' && s.sessionKind !== 'agent') return false;
    if (scope === 'archived' && !s.archived) return false;
    if (scope === 'recent' && !(Date.parse(s.lastAt) >= now - 86_400_000)) return false;
    return !needle || [s.name, s.title, s.id, s.project, s.model].some((v) => String(v || '').toLocaleLowerCase().includes(needle));
  }).sort((a, b) => {
    if (sort === 'context') return (b.context?.percentUsed ?? -1) - (a.context?.percentUsed ?? -1) || Date.parse(b.lastAt || 0) - Date.parse(a.lastAt || 0);
    if (sort === 'total') return (b.total?.total || 0) - (a.total?.total || 0);
    return Date.parse(b.lastAt || 0) - Date.parse(a.lastAt || 0);
  });
}

function meter(c) {
  if (!Number.isFinite(c?.percentUsed)) return '<span class="context-muted">未知 · 暂无窗口统计</span>';
  const percent = Math.max(0, c.percentUsed);
  const tone = percent >= 85 ? 'danger' : percent >= 70 ? 'warning' : 'normal';
  return '<div class="context-meter-line"><strong>' + percent.toFixed(1) + '%</strong><span>' + tokens(c.input) + ' / ' + tokens(c.window) + '</span></div><div class="context-meter ' + tone + '" role="meter" aria-label="上下文参考占用" aria-valuemin="0" aria-valuemax="100" aria-valuenow="' + Math.min(100, percent) + '"><span style="width:' + Math.min(100, percent) + '%"></span></div>';
}

export function renderContextRows(sessions) {
  if (!sessions.length) return '<tr><td colspan="5" class="context-empty">没有匹配的对话。可调整搜索或范围；未落地到本机的对话暂不可见。</td></tr>';
  return sessions.map((s) => '<tr>' +
    '<td class="context-identity" data-label="对话"><strong title="' + escape(title(s)) + '">' + escape(title(s)) + '</strong>' +
    '<div class="row-actions"><button type="button" class="analysis-link" data-context-session="' + escape(s.id) + '" ' + (s.hasUsage ? '' : 'disabled') + '>用量分析</button><details class="session-details" data-session-details="' + escape(s.id) + '"><summary>展开信息</summary><div><p>项目：' + escape(s.project || '未记录') + '</p><p>ID：' + escape(s.id) + '</p><p>推理强度：' + escape(s.reasoningEffort || '未记录') + '</p><p>累计输入 ' + tokens(s.hasUsage ? s.total?.input : undefined) + ' · 输出 ' + tokens(s.hasUsage ? s.total?.output : undefined) + '</p><p>缓存输入 ' + tokens(s.hasUsage ? s.total?.cached : undefined) + '（已含在输入中）</p><p>用量采样：' + escape(time(s.context?.at)) + '</p>' + (s.includesInheritedHistory ? '<p>包含继承历史</p>' : '') + (s.archived ? '<p>已归档</p>' : '') + (s.context?.staleModel ? '<p>占用采样模型：' + escape(s.context.model) + '</p>' : '') + '</div></details></div></td>' +
    '<td data-label="模型"><span class="model-name">' + escape(s.model || '未知模型') + '</span></td>' +
    '<td class="context-usage" data-label="参考占用">' + meter(s.context) + '</td>' +
    '<td class="context-tokens" data-label="累计 Token"><strong>' + (s.hasUsage ? tokens(s.total?.total) : '暂无数据') + '</strong></td>' +
    '<td class="context-time" data-label="最近活动">' + escape(time(s.lastAt)) + '</td></tr>').join('');
}

export function renderCurrentSession(view, sessions, { now = Date.now(), usageFailed = false } = {}) {
  const expired = view?.status === 'identified' && !(Date.parse(view.expiresAt) > now);
  const identified = view?.status === 'identified' && !expired;
  if (!identified) {
    const labels = { 'not-a-thread': '当前不是聊天页面', ambiguous: '当前窗口不明确', stale: '当前聊天识别已过期' };
    const heading = expired ? labels.stale : labels[view?.status] || '未识别当前聊天';
    const badge = expired ? '已过期' : ({ 'not-a-thread': '非聊天页面', ambiguous: '待识别', stale: '已过期' })[view?.status] || '未连接';
    return '<div class="current-header"><div><p class="context-eyebrow">CODEX 当前打开的聊天</p><h2 id="currentTitle">' + heading + '</h2></div><span class="identity-badge">' + badge + '</span></div><p class="current-message">' + escape(expired ? '等待新的窗口识别信息。' : view?.reason || '尚未连接 Codex 当前聊天识别。') + '</p><div class="current-placeholder"><div><span>参考占用</span><strong>—</strong></div><div><span>会话累计 Token</span><strong>—</strong></div></div><p class="current-note">下方可查看本机对话统计。</p>';
  }
  // A remote thread must never accidentally match a local log with the same ID.
  const s = view.hostId === 'local' ? sessions.find((s) => s.id === view.threadId) : null;
  const name = s ? title(s) : view.name || '对话 ' + view.threadId.slice(0, 8);
  const c = s?.context;
  return '<div class="current-header"><div><p class="context-eyebrow">CODEX 当前打开的聊天</p><h2 id="currentTitle">' + escape(name) + '</h2><p class="current-model">' + escape(s?.model || '模型未知') + ' · ' + escape(s?.reasoningEffort || '强度未记录') + '</p></div><span class="identity-badge connected">已识别</span></div>' +
    '<div class="current-metrics"><div class="current-occupancy"><span class="metric-label">参考占用</span>' + meter(c) + '</div><div class="current-total"><span class="metric-label">会话累计 Token</span><strong>' + (s?.hasUsage ? tokens(s.total?.total) : '—') + '</strong><small>输入 ' + tokens(s?.hasUsage ? s.total?.input : undefined) + ' · 输出 ' + tokens(s?.hasUsage ? s.total?.output : undefined) + '</small></div></div>' +
    '<div class="current-extra"><span>最近请求输入 <b>' + tokens(c?.input) + '</b></span><span>最近请求输出 <b>' + tokens(c?.output) + '</b></span></div>' +
    '<p class="current-note">用量采样：' + escape(time(c?.at)) + ' · 窗口识别：' + escape(time(view.observedAt)) + '</p>' +
    (c?.staleModel ? '<p class="current-note">占用来自模型 ' + escape(c.model) + ' 的采样。</p>' : '') +
    (s?.includesInheritedHistory ? '<p class="current-note">累计 Token 包含继承历史。</p>' : '') +
    (usageFailed ? '<p class="current-note error">统计更新失败；以上为旧采样。</p>' : '') +
    (!s || !s.hasUsage ? '<p class="current-note">当前聊天暂无本地用量记录。</p>' : '');
}

export function startContextOverview() {
  const $ = (id) => document.getElementById(id);
  if (!$('contextOverview') || window.__CODEX_USAGE_REPORT__) return;
  let sessions = [], page = 0, busy = false, timer, viewTimer, expiryTimer;
  let viewBusy = false, currentView = { status: 'unavailable', reason: '尚未连接 Codex 当前聊天识别。' };
  let fingerprint = '', failed = false, checkedAt = '', interval = 10000;
  const expanded = new Set();
  try {
    const stored = localStorage.getItem('codexContextRefreshMs');
    if (stored !== null && [0, 5000, 10000, 30000].includes(Number(stored))) interval = Number(stored);
  } catch { /* Optional preference. */ }
  $('contextInterval').value = String(interval);

  function status() {
    $('contextLiveDot').classList.toggle('paused', !interval || document.hidden || failed);
    const mode = !interval ? '已暂停' : document.hidden ? '后台暂停' : '每 ' + interval / 1000 + ' 秒刷新';
    $('contextStatus').textContent = (failed ? '更新失败 · 显示旧采样 · ' : '') + mode + (checkedAt ? ' · 更新于 ' + time(checkedAt) : ' · 首次扫描中');
  }
  function renderCurrent() {
    const markup = renderCurrentSession(currentView, sessions, { usageFailed: failed });
    updateMarkup($('currentCard'), markup);
    clearTimeout(expiryTimer);
    if (currentView.status === 'identified' && Date.parse(currentView.expiresAt) > Date.now()) {
      expiryTimer = setTimeout(renderCurrent, Date.parse(currentView.expiresAt) - Date.now() + 1);
    }
  }
  function render() {
    renderCurrent();
    const settings = { query: $('contextSearch').value, scope: $('contextScope').value, sort: $('contextSort').value };
    const rows = filterContextSessions(sessions, settings);
    const pages = Math.max(1, Math.ceil(rows.length / PAGE_SIZE));
    page = Math.min(page, pages - 1);
    updateRows($('contextRows'), rows.slice(page * PAGE_SIZE, (page + 1) * PAGE_SIZE));
    const agents = settings.scope === 'main' ? filterContextSessions(sessions, { ...settings, scope: 'agents' }) : [];
    $('agentGroup').hidden = !agents.length;
    $('agentCount').textContent = '代理会话 · ' + agents.length;
    updateRows($('agentRows'), agents.slice(0, PAGE_SIZE));
    for (const detail of document.querySelectorAll('[data-session-details]')) detail.open = expanded.has(detail.dataset.sessionDetails);
    $('contextCount').textContent = rows.length + ' 个匹配 · 本机共 ' + sessions.length + ' 个对话';
    $('contextPage').textContent = page + 1 + ' / ' + pages;
    $('contextPrev').disabled = page === 0;
    $('contextNext').disabled = page === pages - 1;
  }
  function schedule() {
    clearTimeout(timer);
    if (interval && !document.hidden) timer = setTimeout(() => void refresh(), interval);
    status();
  }
  function scheduleView() {
    clearTimeout(viewTimer);
    if (interval && !document.hidden) viewTimer = setTimeout(() => void refreshCurrent(), currentView.source === 'windows-sidebar-ocr' ? 1000 : currentView.status === 'identified' ? 2000 : 10000);
  }
  async function refreshCurrent() {
    if (viewBusy) return;
    viewBusy = true;
    try {
      const response = await fetch('/api/current-view', { signal: AbortSignal.timeout(4000) });
      if (!response.ok) throw new Error('HTTP ' + response.status);
      currentView = await response.json();
    } catch {
      currentView = { status: 'unavailable', reason: '当前聊天识别连接中断。' };
    } finally {
      viewBusy = false;
      renderCurrent();
      scheduleView();
    }
  }
  async function refresh() {
    if (busy) return;
    busy = true;
    $('contextRefresh').disabled = true;
    try {
      const response = await fetch('/api/context-overview', { signal: AbortSignal.timeout(180000) });
      if (!response.ok) throw new Error('HTTP ' + response.status);
      const data = await response.json();
      sessions = data.sessions || [];
      checkedAt = data.checkedAt;
      failed = false;
      render();
      const warnings = data.warnings || [];
      $('contextWarnings').hidden = !warnings.length;
      $('contextWarnings').textContent = warnings.length ? warnings.length + ' 个扫描问题；统计可能不完整。' + warnings[0] : '';
      if (fingerprint && fingerprint !== data.fingerprint) window.dispatchEvent(new CustomEvent('codex-context-updated'));
      fingerprint = data.fingerprint;
    } catch (error) {
      failed = true;
      if (!checkedAt) $('contextRows').innerHTML = '<tr><td colspan="5" class="context-empty">加载失败，请检查本地服务后点击刷新。</td></tr>';
      $('contextStatus').title = error.message;
      renderCurrent();
    } finally {
      busy = false;
      $('contextRefresh').disabled = false;
      schedule();
    }
  }
  for (const id of ['contextSearch', 'contextScope', 'contextSort']) $(id).addEventListener(id === 'contextSearch' ? 'input' : 'change', () => { page = 0; render(); });
  $('contextPrev').addEventListener('click', () => { page--; render(); });
  $('contextNext').addEventListener('click', () => { page++; render(); });
  $('showAllAgents').addEventListener('click', () => { $('contextScope').value = 'agents'; page = 0; render(); });
  $('contextRefresh').addEventListener('click', () => { void refreshCurrent(); void refresh(); });
  $('refreshButton')?.addEventListener('click', () => { void refreshCurrent(); void refresh(); });
  $('contextInterval').addEventListener('change', () => {
    interval = Number($('contextInterval').value);
    try { localStorage.setItem('codexContextRefreshMs', String(interval)); } catch { /* Optional preference. */ }
    schedule(); scheduleView();
  });
  $('contextOverview').addEventListener('toggle', (event) => {
    const id = event.target.dataset.sessionDetails;
    if (id) event.target.open ? expanded.add(id) : expanded.delete(id);
  }, true);
  $('contextOverview').addEventListener('click', (event) => {
    const button = event.target.closest('[data-context-session]');
    if (!button || button.disabled) return;
    window.dispatchEvent(new CustomEvent('codex-select-session', { detail: button.dataset.contextSession }));
    window.dispatchEvent(new CustomEvent('codex-show-view', { detail: 'analysis' }));
  });
  document.addEventListener('visibilitychange', () => {
    clearTimeout(timer); clearTimeout(viewTimer);
    renderCurrent();
    if (!document.hidden && interval) { void refreshCurrent(); void refresh(); }
    else status();
  });
  const card = $('currentSession');
  let expandedHeight = card.getBoundingClientRect().height;
  function reserveCardSpace() {
    const height = card.getBoundingClientRect().height;
    if (!card.classList.contains('is-compact')) expandedHeight = height;
    card.style.setProperty('--current-collapse-gap', Math.max(0, expandedHeight - height) + 'px');
  }
  const cardResize = new ResizeObserver(reserveCardSpace);
  cardResize.observe(card);
  const observer = new IntersectionObserver(([entry]) => {
    if (!card.classList.contains('is-compact')) expandedHeight = card.getBoundingClientRect().height;
    card.classList.toggle('is-compact', !entry.isIntersecting);
    reserveCardSpace();
  });
  observer.observe($('currentSentinel'));
  window.addEventListener('pagehide', () => { clearTimeout(timer); clearTimeout(viewTimer); clearTimeout(expiryTimer); observer.disconnect(); cardResize.disconnect(); });
  window.addEventListener('pageshow', (event) => {
    if (event.persisted) { cardResize.observe(card); observer.observe($('currentSentinel')); renderCurrent(); if (interval) { void refreshCurrent(); void refresh(); } }
  });
  void refreshCurrent();
  void refresh();
}

if (typeof document !== 'undefined') startContextOverview();
