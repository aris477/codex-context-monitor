import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { mkdir, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { createUsageServer } from './src/server.js';

const root = path.dirname(fileURLToPath(import.meta.url));
const data = path.join(root, '.codex-usage');
const port = Number(process.env.CODEX_CONTEXT_PORT || 3765);
const identity = { app: 'codex-context-monitor', pid: process.pid, port, instance: randomUUID() };
const server = createUsageServer({
  visualTracking: process.env.CODEX_CONTEXT_VISUAL !== '0',
  monitorIdentity: identity,
  databaseFile: path.join(data, 'usage-index.sqlite'),
  importStoreFile: path.join(data, 'imports.json'),
  turnCacheDir: path.join(data, 'turn-cache'),
  // Respect an explicitly configured Codex data root, in addition to normal discovery.
  extraHomes: process.env.CODEX_HOME || '',
});
server.on('error', (error) => {
  console.error(error.code === 'EADDRINUSE' ? `Port ${port} is already in use. Open http://127.0.0.1:${port} or set CODEX_CONTEXT_PORT.` : error.message);
  process.exitCode = 1;
  // Also stop the visual helper when binding the HTTP port fails.
  server.close();
});
server.listen(port, '127.0.0.1', async () => {
  try {
    await mkdir(data, { recursive: true });
    await writeFile(path.join(data, 'server.json'), JSON.stringify(identity));
    console.log(`Codex Context Monitor: http://127.0.0.1:${port}`);
  } catch (error) { console.error(error.message); server.close(); process.exitCode = 1; }
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.close(() => process.exit(0)));
