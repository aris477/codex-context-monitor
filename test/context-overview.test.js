import assert from 'node:assert/strict';
import { appendFile, mkdir, mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { DatabaseSync } from 'node:sqlite';
import { UsageStore } from '../src/usage-store.js';
import { createUsageServer } from '../src/server.js';
import { filterContextSessions, renderContextRows } from '../public/context-overview.js';

const line = (row) => JSON.stringify(row) + '\n';
const sample = (input, window = 100000) => ({
  timestamp: '2026-11-05T04:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: {
    model_context_window: window,
    total_token_usage: { input_tokens: 600000, total_tokens: 650000, output_tokens: 50000 },
    last_token_usage: { input_tokens: input, cached_input_tokens: 5000, output_tokens: 1000, total_tokens: input + 1000 },
  } },
});

async function fixture(t) {
  const homeDir = await mkdtemp(path.join(tmpdir(), 'context-overview-'));
  const dir = path.join(homeDir, '.codex', 'sessions');
  await mkdir(dir, { recursive: true });
  const file = path.join(dir, 'rollout-a.jsonl');
  await writeFile(file, [
    { timestamp: '2026-11-05T03:00:00Z', type: 'session_meta', payload: { id: 'alpha', source: 'cli', cwd: '/project/a' } },
    { timestamp: '2026-11-05T03:00:01Z', type: 'turn_context', payload: { model: 'test-model', effort: 'high' } },
    sample(12000),
  ].map(line).join(''));
  await writeFile(path.join(dir, 'rollout-empty.jsonl'), line({ timestamp: '2026-11-05T03:00:00Z', type: 'session_meta', payload: { id: 'empty', cwd: '/project/b' } }));
  const options = { homeDir, env: {}, databaseFile: path.join(homeDir, 'cache.sqlite'), importStoreFile: path.join(homeDir, 'imports.json') };
  return { options, file, homeDir };
}

test('overview uses last input including cache, preserves unknown sessions and incrementally updates zero-delta samples', async (t) => {
  const { options, file, homeDir } = await fixture(t);
  const store = new UsageStore(options);
  t.after(async () => { store.close(); await rm(homeDir, { recursive: true, force: true }); });
  await store.sync();
  let rows = store.contextOverview();
  assert.equal(rows.length, 2);
  const alpha = rows.find((s) => s.id === 'alpha');
  assert.equal(alpha.total.total, 650000);
  assert.equal(alpha.context.percentUsed, 12);
  assert.equal(alpha.context.input, 12000);
  assert.equal(alpha.reasoningEffort, 'high');
  assert.equal(rows.find((s) => s.id === 'empty').context, null);
  assert.equal(rows.find((s) => s.id === 'empty').hasUsage, false);

  // Codex may repeat cumulative counters while updating the context sample.
  await appendFile(file, line(sample(25000)));
  await store.sync();
  rows = store.contextOverview();
  assert.equal(rows.find((s) => s.id === 'alpha').context.percentUsed, 25);
  assert.equal(rows.find((s) => s.id === 'alpha').total.total, 650000);

  await appendFile(file, line({ timestamp: '2026-11-05T04:01:00Z', type: 'turn_context', payload: { model: 'other-model', effort: 'low' } }));
  await store.sync();
  const switched = store.contextOverview().find((s) => s.id === 'alpha');
  assert.equal(switched.model, 'other-model');
  assert.equal(switched.context.model, 'test-model');
  assert.equal(switched.context.staleModel, true);

  await appendFile(file, line(sample(30000, undefined)).replace('"model_context_window":100000,', ''));
  await store.sync();
  assert.equal(store.contextOverview().find((s) => s.id === 'alpha').context.percentUsed, null);
});

test('overview API indexes logs and returns reference metrics without transcript bodies', async (t) => {
  const { options, file, homeDir } = await fixture(t);
  await appendFile(file, line({ type: 'response_item', payload: { text: 'PRIVATE_SENTINEL_BODY' } }));
  const server = createUsageServer(options);
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => {
    await new Promise((resolve) => server.close(resolve));
    await rm(homeDir, { recursive: true, force: true });
  });
  const response = await fetch(`http://127.0.0.1:${server.address().port}/api/context-overview`);
  assert.equal(response.status, 200);
  const body = await response.text();
  assert.doesNotMatch(body, /PRIVATE_SENTINEL_BODY/);
  const data = JSON.parse(body);
  assert.ok(data.checkedAt);
  assert.equal(data.sessions.find((s) => s.id === 'alpha').context.percentUsed, 12);
  assert.equal(data.sessions.length, 2);
});

test('rendering escapes titles and sorts known context ahead of unknown values', () => {
  const rows = [
    { id: 'a', name: '<img src=x onerror=alert(1)>', context: null, project: 'needle', lastAt: '2026-11-05T00:00:00Z' },
    { id: 'b', hasUsage: true, context: { percentUsed: 150, input: 150, window: 100 }, lastAt: '2026-11-04T00:00:00Z' },
  ];
  assert.deepEqual(filterContextSessions(rows, { sort: 'context' }).map((s) => s.id), ['b', 'a']);
  assert.equal(filterContextSessions(rows, { query: 'NEEDLE' }).length, 1);
  assert.equal(filterContextSessions(rows, { scope: 'recent', now: Date.parse('2026-11-05T12:00:00Z') }).length, 1);
  const html = renderContextRows(rows);
  assert.doesNotMatch(html, /<img/);
  assert.match(html, /&lt;img/);
  assert.match(html, /150\.0%/);
  assert.match(html, /width:100%/);
  assert.match(html, /未知/);
});

test('forks with copied session metadata use the state database identity instead of merging into their parent', async (t) => {
  const { options, file, homeDir } = await fixture(t);
  const fork = path.join(path.dirname(file), 'rollout-fork.jsonl');
  await writeFile(fork, await readFile(file));
  const db = new DatabaseSync(path.join(homeDir, '.codex', 'state_5.sqlite'));
  db.exec('CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, rollout_path TEXT)');
  db.prepare('INSERT INTO threads VALUES (?, ?, ?)').run('alpha', 'Original', file);
  db.prepare('INSERT INTO threads VALUES (?, ?, ?)').run('fork-id', 'Fork title', fork);
  db.close();
  const store = new UsageStore(options);
  t.after(async () => { store.close(); await rm(homeDir, { recursive: true, force: true }); });
  await store.sync();
  const rows = store.contextOverview();
  assert.equal(rows.length, 3);
  assert.equal(rows.find((s) => s.id === 'alpha').total.total, 650000);
  const child = rows.find((s) => s.id === 'fork-id');
  assert.equal(child.title, 'Fork title');
  assert.equal(child.context.percentUsed, 12);
  assert.equal(child.includesInheritedHistory, true);
  assert.equal(store.sessionSource('fork-id').filePath, fork);
  // A late database entry must also correct an already indexed, unchanged log.
  const state = new DatabaseSync(path.join(homeDir, '.codex', 'state_5.sqlite'));
  state.prepare('UPDATE threads SET id = ? WHERE id = ?').run('renamed-fork-id', 'fork-id');
  state.close();
  await store.sync();
  assert.equal(store.contextOverview().some((s) => s.id === 'fork-id'), false);
  assert.equal(store.contextOverview().find((s) => s.id === 'renamed-fork-id').context.percentUsed, 12);
});
