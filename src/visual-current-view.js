import { execFile, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { access, mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const run = promisify(execFile);
const SOURCE = fileURLToPath(new URL('../native/VisualReader.cs', import.meta.url));
const SOURCE_NAME = 'windows-sidebar-ocr';
// OCR inserts spaces between Chinese characters and occasionally sees a leading
// bracket in the title glyphs. Normalize both UI and stored labels identically;
// all collisions remain ambiguous. Never use fuzzy or prefix-only matching.
export function normalizeVisualTitle(value) {
  return String(value || '').normalize('NFKC').replace(/\s+/gu, '')
    .replace(/^[\p{P}\p{S}]+|[\p{P}\p{S}]+$/gu, '');
}

export function matchVisualSample(sample, candidates, now = Date.now()) {
  const missing = (status, reason) => ({ status, reason, source: SOURCE_NAME });
  if (sample?.status !== 'sample') return missing('unavailable', sample?.reason || '等待本地视觉识别');
  const observed = Date.parse(sample.observedAt);
  if (!sample.windowId || !Number.isFinite(observed) || observed > now + 1000 || now - observed > 4000) return missing('unavailable', '视觉采样已过期');
  const heading = normalizeVisualTitle(sample.title);
  if (!heading) return missing('not-a-thread', '未读取到聊天标题，等待下一次识别');
  const selected = (sample.selectedRows || []).filter((row) => row.contrast >= 5 && normalizeVisualTitle(row.text) === heading);
  if (selected.length !== 1) return missing('ambiguous', '侧栏选中行与聊天标题无法唯一对应');
  const matches = new Map(candidates.filter((s) => normalizeVisualTitle(s.name || s.title) === heading).map((s) => [s.id, s]));
  if (matches.size !== 1) return missing('ambiguous', matches.size ? '存在同名聊天，无法唯一匹配会话 ID' : '标题尚未匹配到本地会话 ID');
  const session = matches.values().next().value;
  return {
    status: 'identified', threadId: session.id, name: session.name || session.title, hostId: 'local', windowId: String(sample.windowId),
    source: SOURCE_NAME, observedAt: new Date(observed).toISOString(), expiresAt: new Date(observed + 5000).toISOString(),
  };
}

async function buildReader(cacheDir) {
  const system = process.env.SystemRoot || 'C:\\Windows';
  const framework = path.join(system, 'Microsoft.NET', 'Framework64', 'v4.0.30319');
  const source = await readFile(SOURCE);
  const digest = createHash('sha256').update(source).digest('hex').slice(0, 16);
  const binary = path.join(cacheDir, `visual-reader-${digest}.exe`);
  try { await access(binary); return binary; } catch { /* Build from included source. */ }
  await mkdir(cacheDir, { recursive: true });
  const references = [
    'System.Drawing.dll', 'System.Web.Extensions.dll',
    path.join(framework, 'System.Runtime.dll'), path.join(framework, 'System.Runtime.WindowsRuntime.dll'),
    ...['UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll'].map((name) => path.join(framework, 'WPF', name)),
    ...['Foundation', 'Globalization', 'Media', 'Graphics', 'Storage'].map((name) => path.join(system, 'System32', 'WinMetadata', `Windows.${name}.winmd`)),
  ];
  await run(path.join(framework, 'csc.exe'), [
    '/nologo', '/codepage:65001', '/target:exe', '/platform:x64', `/out:${binary}`,
    ...references.map((file) => `/r:${file}`), SOURCE,
  ], { windowsHide: true, timeout: 30000, maxBuffer: 32768, env: { ...process.env, TEMP: cacheDir, TMP: cacheDir } });
  return binary;
}

export function createWindowsVisualProvider({ cacheDir, getCandidates, platform = process.platform,
  sampleIntervalMs = 2000, idleMs = 10000, now = Date.now,
  buildExecutable = buildReader, spawnReader = spawn,
}) {
  let child, pending, timer, timeout, idleTimer, closed = false, building = false, latest;
  let reason = platform === 'win32' ? '正在启动本地视觉识别' : '视觉识别仅支持 Windows';
  let nextStart = 0, lastRequest = -Infinity, generation = 0;
  const active = () => !closed && now() - lastRequest < idleMs;
  function lose(message) { latest = null; reason = message; }
  function stopIdle() {
    clearTimeout(timer); clearTimeout(timeout); clearTimeout(idleTimer);
    generation++;
    const previous = child; child = null; pending = false;
    previous?.stdin.end(); previous?.kill();
    lose('面板未使用，视觉识别已休眠');
  }
  function renewLease() {
    lastRequest = now();
    clearTimeout(idleTimer);
    idleTimer = setTimeout(stopIdle, idleMs);
    idleTimer.unref();
  }
  function schedule() {
    clearTimeout(timer);
    if (active()) { timer = setTimeout(sample, sampleIntervalMs); timer.unref(); }
  }
  function sample() {
    if (!active() || !child || pending) return;
    const current = child;
    pending = true;
    timeout = setTimeout(() => {
      if (child !== current) return;
      lose('本地视觉识别超时，正在恢复');
      current.kill();
    }, 6000);
    timeout.unref();
    current.stdin.write('sample\n');
  }
  async function start() {
    if (!active() || child || building || platform !== 'win32' || now() < nextStart) return;
    building = true;
    const startedGeneration = generation;
    try {
      const executable = await buildExecutable(cacheDir);
      if (!active() || generation !== startedGeneration) return;
      const current = spawnReader(executable, [], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
      child = current;
      let buffer = '';
      child.stdout.setEncoding('utf8');
      child.stdout.on('data', (chunk) => {
        if (child !== current) return;
        buffer += chunk;
        if (buffer.length > 65536) { lose('视觉组件返回过大数据'); child?.kill(); return; }
        let end;
        while ((end = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, end).replace(/^\uFEFF/, ''); buffer = buffer.slice(end + 1);
          try { latest = JSON.parse(line); } catch { lose('视觉组件返回无效数据'); }
          clearTimeout(timeout); pending = false; schedule();
        }
      });
      child.stdin.on('error', () => { if (child === current) lose('视觉组件连接中断'); });
      child.on('error', () => { if (child === current) lose('视觉组件启动失败'); });
      child.on('close', () => {
        if (child !== current) return;
        clearTimeout(timeout); clearTimeout(timer); child = null; pending = false;
        lose('视觉组件已退出，等待重试'); nextStart = now() + 5000;
      });
      sample();
    } catch {
      lose('本地 OCR 组件无法启动，请检查 Windows OCR 和 .NET Framework');
      nextStart = now() + 10000;
    } finally { building = false; }
  }
  const provider = () => {
    if (closed) return { status: 'unavailable', reason: '视觉识别已关闭', source: SOURCE_NAME };
    renewLease();
    void start();
    return latest ? matchVisualSample(latest, getCandidates(), now()) : { status: 'unavailable', reason, source: SOURCE_NAME };
  };
  provider.close = () => {
    closed = true; stopIdle();
  };
  return provider;
}
