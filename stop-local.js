import { readFile } from 'node:fs/promises';

try {
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
