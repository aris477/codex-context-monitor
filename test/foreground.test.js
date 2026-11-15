import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

test('foreground activation distinguishes app roots, popups, query failures and other applications', { skip: process.platform !== 'win32' }, () => {
  const root = fileURLToPath(new URL('../', import.meta.url));
  const csc = path.join(process.env.SystemRoot || 'C:\\Windows', 'Microsoft.NET/Framework64/v4.0.30319/csc.exe');
  const temp = mkdtempSync(path.join(os.tmpdir(), 'codex-foreground-'));
  try {
    const binary = path.join(temp, 'foreground.exe');
    execFileSync(csc, ['/nologo', '/out:' + binary,
      path.join(root, 'native/CodexForeground.cs'), path.join(root, 'test/foreground.test.cs')], { windowsHide: true });
    assert.match(execFileSync(binary, [], { encoding: 'utf8', windowsHide: true }), /PASS 33 foreground assertions/);
  } finally { rmSync(temp, { recursive: true, force: true }); }
});
