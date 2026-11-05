import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { readCurrentView } from '../src/current-view.js';
import { createUsageServer } from '../src/server.js';
import { renderCurrentSession, filterContextSessions } from '../public/context-overview.js';

const now = Date.parse('2026-11-05T08:00:00Z');
const identity = (threadId = 'selected') => ({
  status: 'identified', threadId, hostId: 'local', windowId: 'window-1', source: 'test-ui',
  observedAt: new Date(now - 1000).toISOString(), expiresAt: new Date(now + 4000).toISOString(),
});

test('current-view refuses missing, expired, malformed or disconnected identity', async () => {
  assert.equal((await readCurrentView(null, { now })).status, 'unavailable');
  const expired = await readCurrentView(() => ({ ...identity(), expiresAt: new Date(now - 1).toISOString() }), { now });
  assert.equal(expired.status, 'stale');
  for (const invalid of [null, { status: 'active' }, { ...identity(), windowId: null }, { ...identity(), observedAt: null }, { ...identity(), expiresAt: new Date(now + 60000).toISOString() }]) {
    assert.equal((await readCurrentView(() => invalid, { now })).status, 'unavailable');
  }
  assert.equal((await readCurrentView(() => { throw Error('private error'); }, { now })).status, 'unavailable');
  assert.equal((await readCurrentView(() => new Promise(() => {}), { now, timeoutMs: 5 })).status, 'unavailable');
  const safe = await readCurrentView(() => ({ ...identity(), transcript: 'private body' }), { now });
  assert.equal(safe.threadId, 'selected');
  assert.equal(safe.transcript, undefined);
});

test('current-view API follows explicit window identity independently of indexing', async (t) => {
  const dir = await mkdtemp(path.join(tmpdir(), 'current-view-'));
  let selected = identity('A');
  selected.observedAt = new Date().toISOString();
  selected.expiresAt = new Date(Date.now() + 10000).toISOString();
  const server = createUsageServer({ homeDir: dir, databaseFile: path.join(dir, 'index.sqlite'), currentViewProvider: () => selected });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => { await new Promise((resolve) => server.close(resolve)); await rm(dir, { recursive: true, force: true }); });
  const get = () => fetch('http://127.0.0.1:' + server.address().port + '/api/current-view');
  const first = await get();
  assert.equal(first.headers.get('cache-control'), 'no-store');
  assert.equal((await first.json()).threadId, 'A');
  selected = { ...selected, threadId: 'B' };
  assert.equal((await (await get()).json()).threadId, 'B');
  selected = { status: 'not-a-thread', reason: 'home' };
  const home = await (await get()).json();
  assert.equal(home.status, 'not-a-thread');
  assert.equal(home.threadId, null);
  selected = { status: 'ambiguous' };
  assert.equal((await (await get()).json()).status, 'ambiguous');
});

test('pinned card never substitutes latest activity, remote logs or old identities', () => {
  const sessions = [
    { id: 'background', name: 'BACKGROUND_SENTINEL', hasUsage: true, total: { total: 99999 }, lastAt: new Date(now).toISOString() },
    { id: 'selected', name: '<SELECTED>', model: 'test-model', hasUsage: true, total: { total: 1234 }, context: { percentUsed: 12, input: 120, output: 6, window: 1000, at: new Date(now - 100).toISOString() } },
  ];
  const html = renderCurrentSession(identity(), sessions, { now });
  assert.match(html, /&lt;SELECTED&gt;/);
  assert.match(html, /12\.0%/);
  assert.match(html, /1,234/);
  assert.doesNotMatch(html, /BACKGROUND_SENTINEL|<SELECTED>/);
  for (const status of ['unavailable', 'stale', 'ambiguous', 'not-a-thread']) {
    const lost = renderCurrentSession({ ...identity(), status }, sessions, { now });
    assert.doesNotMatch(lost, /SELECTED|12\.0%|1,234/);
  }
  assert.doesNotMatch(renderCurrentSession(identity(), sessions, { now: now + 5000 }), /SELECTED|12\.0%/);
  assert.match(renderCurrentSession(identity('no-log'), sessions, { now }), /暂无本地用量记录/);
  assert.doesNotMatch(renderCurrentSession({ ...identity(), hostId: 'remote' }, sessions, { now }), /SELECTED|12\.0%/);
  assert.match(renderCurrentSession(identity(), sessions, { now, usageFailed: true }), /旧采样/);
});

test('agent grouping relies only on explicit metadata and preserves unknown sessions', () => {
  const rows = [{ id: 'a', name: 'Guardian review' }, { id: 'b', sessionKind: 'agent' }, { id: 'c', sessionKind: 'unknown' }];
  assert.deepEqual(filterContextSessions(rows, { scope: 'main' }).map((s) => s.id), ['a', 'c']);
  assert.deepEqual(filterContextSessions(rows, { scope: 'agents' }).map((s) => s.id), ['b']);
  assert.equal(filterContextSessions(rows).length, 3);
});
