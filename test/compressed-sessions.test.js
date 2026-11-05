import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import * as zlib from 'node:zlib';
import { discoverSessionFiles, parseSessionFile, parseSessionTurns } from '../src/usage-core.js';
import { UsageStore } from '../src/usage-store.js';
import { createUsageServer } from '../src/server.js';
import { exportStaticDashboard } from '../src/static-export.js';

const supported = typeof zlib.zstdCompressSync === 'function';
const jsonl = (input = 12000, total = 650000) => [
  { timestamp: '2026-11-05T03:00:00Z', type: 'session_meta', payload: { id: 'parent', cwd: '/example', source: 'cli' } },
  { timestamp: '2026-11-05T03:00:01Z', type: 'turn_context', payload: { model: 'example-model', effort: 'high' } },
  { timestamp: '2026-11-05T03:00:02Z', type: 'event_msg', payload: { type: 'task_started', turn_id: 'turn-1' } },
  { timestamp: '2026-11-05T03:00:03Z', type: 'event_msg', payload: { type: 'token_count', info: {
    model_context_window: 100000,
    total_token_usage: { input_tokens: total - 1000, output_tokens: 1000, total_tokens: total },
    last_token_usage: { input_tokens: input, output_tokens: 1000, total_tokens: input + 1000 },
  } } },
  { timestamp: '2026-11-05T03:00:04Z', type: 'event_msg', payload: { type: 'task_complete', turn_id: 'turn-1' } },
].map(row => JSON.stringify(row) + '\n').join('');

async function fixture(t) {
  const homeDir = await mkdtemp(path.join(tmpdir(), 'compressed-session-'));
  const home = path.join(homeDir, '.codex');
  const dir = path.join(home, 'sessions');
  await mkdir(dir, { recursive: true });
  const plain = path.join(dir, 'rollout-example.jsonl');
  const compressed = plain + '.zst';
  const store = new UsageStore({ homeDir, env: {}, databaseFile: path.join(homeDir, 'index.sqlite') });
  t.after(async () => { store.close(); await rm(homeDir, { recursive: true, force: true }); });
  return { homeDir, home, plain, compressed, store };
}

test('compressed rollouts restore context and preserve state database fork identity', { skip: !supported }, async t => {
  const { home, plain, compressed, store } = await fixture(t);
  await writeFile(compressed, zlib.zstdCompressSync(jsonl()));
  const db = new DatabaseSync(path.join(home, 'state_5.sqlite'));
  db.exec('CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, rollout_path TEXT)');
  // Codex may keep the uncompressed extended Windows path after compression.
  const canonical = process.platform === 'win32' ? '\\\\?\\' + plain : plain;
  db.prepare('INSERT INTO threads VALUES (?, ?, ?)').run('child', 'Recovered fork', canonical);
  db.close();
  await store.sync();
  const row = store.contextOverview().find(s => s.id === 'child');
  assert.ok(row, 'must not merge copied metadata into the parent');
  assert.equal(row.context.percentUsed, 12);
  assert.equal(row.total.total, 650000);
  assert.equal(store.sessionSource('child').filePath, compressed);
  assert.equal((await store.sync()).updatedFileCount, 0, 'unchanged archives stay cached');
  await writeFile(compressed, zlib.zstdCompressSync(jsonl(28000, 710000)));
  await store.sync();
  assert.ok(Math.abs(store.contextOverview().find(s => s.id === 'child').context.percentUsed - 28) < 1e-9);
  assert.equal(store.contextOverview().find(s => s.id === 'child').total.total, 710000, 'replace archives instead of appending counters');
});

test('plain and compressed copies are counted once, including migration to compressed only', { skip: !supported }, async t => {
  const { home, plain, compressed, store } = await fixture(t);
  await writeFile(plain, jsonl());
  await writeFile(compressed, zlib.zstdCompressSync(jsonl()));
  assert.deepEqual(await discoverSessionFiles(home), [plain]);
  await store.sync();
  assert.equal(store.contextOverview()[0].total.total, 650000);
  await rm(plain);
  await store.sync();
  assert.deepEqual(await discoverSessionFiles(home), [compressed]);
  assert.equal(store.contextOverview()[0].total.total, 650000);
  assert.equal(store.contextOverview()[0].context.percentUsed, 12);
});

test('compressed sessions work in full reports and per-turn detail readers', { skip: !supported }, async t => {
  const { home, plain, compressed } = await fixture(t);
  await writeFile(plain, jsonl());
  await writeFile(compressed, zlib.zstdCompressSync(await readFile(plain)));
  const source = { id: 'test-home', label: 'Test', path: home };
  const compressedReport = await parseSessionFile(compressed, source);
  const plainReport = await parseSessionFile(plain, source);
  assert.equal(compressedReport.session.filePath, compressed);
  compressedReport.session.filePath = plain;
  assert.deepEqual(compressedReport, plainReport);
  const turns = await parseSessionTurns(compressed, source);
  assert.ok(turns.length > 0);
  assert.deepEqual(turns, await parseSessionTurns(plain, source));
});

test('corrupt compressed sessions report a warning and keep healthy sessions readable', { skip: !supported }, async t => {
  const { home, plain, compressed, store } = await fixture(t);
  await writeFile(plain, jsonl());
  await writeFile(path.join(home, 'sessions', 'broken.jsonl.zst'), 'invalid zstd archive');
  await store.sync();
  assert.equal(store.contextOverview()[0].context.percentUsed, 12);
  assert.ok(store.metadata().warnings.some(w => w.includes('broken.jsonl.zst')));
  await writeFile(compressed, 'invalid zstd archive');
  await assert.rejects(parseSessionFile(compressed, { id: 'test-home', label: 'Test', path: home }));
});

test('current rollout replaces old resumed snapshots while explicitly registered forks remain separate', { skip: !supported }, async t => {
  const { home, store } = await fixture(t);
  const id = '11111111-2222-3333-4444-555555555555';
  const forkId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
  const oldPath = path.join(home, 'sessions', `rollout-2026-11-01T00-00-00-${id}.jsonl.zst`);
  const currentPlain = path.join(home, 'sessions', `rollout-2026-11-05T00-00-00-${id}_22222222-3333-4444-5555-666666666666.jsonl`);
  const forkPlain = path.join(home, 'sessions', `rollout-2026-11-04T00-00-00-${id}_${forkId}.jsonl`);
  const body = jsonl().replace('"id":"parent"', `"id":"${id}"`);
  await writeFile(oldPath, zlib.zstdCompressSync(body));
  await writeFile(currentPlain + '.zst', zlib.zstdCompressSync(body));
  await writeFile(forkPlain + '.zst', zlib.zstdCompressSync(body));
  const db = new DatabaseSync(path.join(home, 'state_5.sqlite'));
  db.exec('CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, rollout_path TEXT)');
  db.prepare('INSERT INTO threads VALUES (?, ?, ?)').run(id, 'Current', currentPlain);
  db.prepare('INSERT INTO threads VALUES (?, ?, ?)').run(forkId, 'Fork', forkPlain);
  db.close();
  await store.sync();
  assert.equal(store.contextOverview().length, 2);
  assert.equal(store.contextOverview().find(s => s.id === id).total.total, 650000);
  assert.equal(store.contextOverview().find(s => s.id === forkId).total.total, 650000);
  assert.equal(store.sessionSource(id).filePath, currentPlain + '.zst');
  assert.equal((await store.sync()).updatedFileCount, 0);
});

test('compressed archived sessions work through overview, turn and request APIs and static export', { skip: !supported }, async t => {
  const homeDir = await mkdtemp(path.join(tmpdir(), 'compressed-api-'));
  const dir = path.join(homeDir, '.codex', 'archived_sessions');
  await mkdir(dir, { recursive: true });
  await writeFile(path.join(dir, 'rollout-example.jsonl.zst'), zlib.zstdCompressSync(jsonl()));
  const options = { homeDir, env: {}, databaseFile: path.join(homeDir, 'index.sqlite'),
    importStoreFile: path.join(homeDir, 'imports.json'), turnCacheDir: path.join(homeDir, 'turn-cache') };
  const server = createUsageServer(options);
  t.after(async () => {
    await new Promise(resolve => server.close(resolve));
    await rm(homeDir, { recursive: true, force: true });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const overviewResponse = await fetch(base + '/api/context-overview');
  assert.equal(overviewResponse.status, 200);
  const overview = await overviewResponse.json();
  assert.equal(overview.sessions[0].context.percentUsed, 12);
  assert.equal(overview.sessions[0].archived, true);
  const turnsResponse = await fetch(base + '/api/sessions/parent/turns');
  assert.equal(turnsResponse.status, 200);
  const turns = await turnsResponse.json();
  assert.equal(turns.summary.turnCount, 1);
  const requestsResponse = await fetch(base + `/api/sessions/parent/turns/${encodeURIComponent(turns.turns[0].turnId)}/requests`);
  assert.equal(requestsResponse.status, 200);
  assert.equal((await requestsResponse.json()).total, 1);
  const outFile = path.join(homeDir, 'snapshot.html');
  await exportStaticDashboard({ ...options, sessionId: 'parent', outFile });
  const html = await readFile(outFile, 'utf8');
  assert.match(html, /"total":650000/);
  assert.match(html, /window\.__CODEX_USAGE_TURN_DATA__/);
  assert.doesNotMatch(html, /rollout-example\.jsonl\.zst/);
});
