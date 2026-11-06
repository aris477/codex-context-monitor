import { mkdir, readFile, writeFile } from 'node:fs/promises';

try {
  // A manual stop lasts until Codex exits, or the user starts the panel again.
  await mkdir(new URL('./.codex-usage/', import.meta.url), { recursive: true });
  await writeFile(new URL('./.codex-usage/companion-paused', import.meta.url), 'manual-stop\n');
  const identity = JSON.parse(await readFile(new URL('./.codex-usage/server.json', import.meta.url), 'utf8'));
  const response = await fetch(`http://127.0.0.1:${identity.port}/api/monitor-health`, { signal: AbortSignal.timeout(3000) });
  const live = await response.json();
  if (live.app !== 'codex-context-monitor' || live.instance !== identity.instance || live.pid !== identity.pid) {
    throw new Error('Server identity changed. No process was stopped.');
  }
  process.kill(identity.pid);
  console.log('Codex Context Monitor stopped.');
} catch (error) {
  console.log(`Monitor is not running, or could not be verified: ${error.message}`);
}
