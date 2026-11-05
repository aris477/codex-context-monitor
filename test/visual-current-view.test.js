import assert from 'node:assert/strict';
import test from 'node:test';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { matchVisualSample, normalizeVisualTitle, createWindowsVisualProvider } from '../src/visual-current-view.js';
import { readCurrentView } from '../src/current-view.js';

const now = Date.parse('2026-11-05T09:30:00Z');
const candidates = [{ id: 'A', name: '上下文插件1' }, { id: 'B', name: '鹈鹕测试' }];
const sample = (title = '上 下 文 插 件 1') => ({
  status: 'sample', windowId: '123', observedAt: new Date(now - 200).toISOString(), title,
  selectedRows: [{ text: title, contrast: 12 }],
});

test('visual identity follows A to B to A using both selected row and title', async () => {
  for (const [title, expected] of [['上 下 文 插 件 1', 'A'], ['鹈 鹕 测 试', 'B'], ['上下文插件1', 'A']]) {
    const result = matchVisualSample(sample(title), candidates, now);
    assert.equal(result.threadId, expected);
    assert.equal(result.source, 'windows-sidebar-ocr');
    const safe = await readCurrentView(() => result, { now });
    assert.equal(safe.status, 'identified');
    assert.equal(safe.threadId, expected);
    assert.equal(safe.name, candidates.find((c) => c.id === expected).name);
  }
});

test('OCR normalization accepts spacing and edge glyphs but refuses collisions and truncation', () => {
  assert.equal(normalizeVisualTitle('[ 上 下 文 插 件 １'), '上下文插件1');
  const s = { ...sample(), title: '[ 上 下 文 插 件 1' };
  assert.equal(matchVisualSample(s, candidates, now).threadId, 'A');
  assert.equal(matchVisualSample(s, [...candidates, { id: 'C', name: '[上下文插件1]' }], now).status, 'ambiguous');
  assert.equal(matchVisualSample(sample('上下文插件…'), candidates, now).status, 'ambiguous');
  assert.equal(matchVisualSample(sample('不存在'), candidates, now).status, 'ambiguous');
  assert.equal(matchVisualSample(s, [...candidates, candidates[0]], now).threadId, 'A');
});

test('hover, hidden sidebar and title mismatch never substitute another active thread', () => {
  const s = sample();
  s.selectedRows.push({ text: '鹈鹕测试', contrast: 14 });
  assert.equal(matchVisualSample(s, candidates, now).threadId, 'A');
  s.selectedRows = [{ text: '鹈鹕测试', contrast: 14 }];
  assert.equal(matchVisualSample(s, candidates, now).status, 'ambiguous');
  s.selectedRows = [];
  assert.equal(matchVisualSample(s, candidates, now).threadId, undefined);
  s.selectedRows = [{ text: s.title, contrast: 2 }];
  assert.equal(matchVisualSample(s, candidates, now).threadId, undefined);
  s.selectedRows = [{ text: s.title, contrast: 12 }, { text: s.title, contrast: 13 }];
  assert.equal(matchVisualSample(s, candidates, now).status, 'ambiguous');
  assert.equal(matchVisualSample(sample(''), candidates, now).status, 'not-a-thread');
});

test('old frames, missing windows and other foreground apps clear visual identity', () => {
  for (const s of [
    { ...sample(), observedAt: new Date(now - 4001).toISOString() },
    { ...sample(), observedAt: 'invalid' },
    { ...sample(), observedAt: new Date(now + 2000).toISOString() },
    { ...sample(), windowId: '' },
    { status: 'unavailable', reason: 'Codex not foreground' },
  ]) {
    const r = matchVisualSample(s, candidates, now);
    assert.equal(r.status, 'unavailable');
    assert.equal(r.threadId, undefined);
  }
});

test('non-Windows providers stay unavailable without starting a screenshot process', () => {
  const provider = createWindowsVisualProvider({ cacheDir: '.', getCandidates: () => candidates, platform: 'linux' });
  assert.equal(provider().status, 'unavailable');
  provider.close();
});

function fakeReader() {
  const reader = new EventEmitter();
  reader.stdout = new PassThrough();
  reader.stdin = new PassThrough();
  reader.killed = false;
  reader.kill = () => { reader.killed = true; };
  return reader;
}

test('visual reader starts on demand, expires when unused, and wakes without retaining old identity', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout', 'Date'], now });
  const readers = [];
  const provider = createWindowsVisualProvider({ cacheDir: '.', platform: 'win32', getCandidates: () => candidates,
    now: () => Date.now(), buildExecutable: async () => 'fake-reader',
    spawnReader: () => {
      const reader = fakeReader(); readers.push(reader);
      reader.stdin.on('data', () => reader.stdout.write(JSON.stringify({ ...sample(), observedAt: new Date(Date.now()).toISOString() }) + '\n'));
      return reader;
    },
  });
  t.after(() => provider.close());
  await new Promise(setImmediate);
  assert.equal(readers.length, 0, 'server startup must not allocate an OCR process');
  provider();
  await new Promise(setImmediate);
  assert.equal(readers.length, 1);
  readers[0].stdout.write(JSON.stringify(sample()) + '\n');
  assert.equal(provider().threadId, 'A');
  t.mock.timers.tick(9000);
  provider(); // A visible page keeps the process alive.
  t.mock.timers.tick(9000);
  assert.equal(readers[0].killed, false);
  t.mock.timers.tick(1001);
  assert.equal(readers[0].killed, true, 'closing/pausing all pages must release OCR memory');
  assert.equal(provider().threadId, undefined, 'waking must clear the previous frame');
  await new Promise(setImmediate);
  assert.equal(readers.length, 2);
  readers[1].stdout.write(JSON.stringify({ ...sample('鹈鹕测试'), observedAt: new Date(Date.now()).toISOString() }) + '\n');
  readers[0].emit('close'); // An old process can finish closing after the new one starts.
  assert.equal(provider().threadId, 'B');
  provider.close();
  assert.equal(readers[1].killed, true);
  assert.equal(provider().status, 'unavailable');
});

test('an OCR build finishing after the last page closes does not start a process', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout', 'Date'], now });
  let finishBuild, spawned = false;
  const provider = createWindowsVisualProvider({ cacheDir: '.', platform: 'win32', getCandidates: () => candidates,
    now: () => Date.now(), buildExecutable: () => new Promise((resolve) => { finishBuild = resolve; }),
    spawnReader: () => { spawned = true; return fakeReader(); },
  });
  t.after(() => provider.close());
  provider();
  t.mock.timers.tick(10001);
  finishBuild('fake-reader');
  await new Promise(setImmediate);
  assert.equal(spawned, false);
});
