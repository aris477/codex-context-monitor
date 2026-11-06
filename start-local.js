import { spawn } from 'node:child_process';
import { openSync, closeSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Detach without keeping the launcher's redirected pipes alive.
const root = path.dirname(fileURLToPath(import.meta.url));
const output = openSync(path.join(root, 'dashboard.log'), 'a');
const errors = openSync(path.join(root, 'dashboard-error.log'), 'a');
try {
  const child = spawn(process.execPath, ['--no-warnings', path.join(root, 'local-server.js')], {
    cwd: root, detached: true, windowsHide: true, stdio: ['ignore', output, errors],
  });
  child.on('error', (error) => { console.error(error.message); process.exitCode = 1; });
  child.unref();
} finally {
  closeSync(output);
  closeSync(errors);
}
