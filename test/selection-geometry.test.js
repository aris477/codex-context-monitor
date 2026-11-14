import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

test('Windows selection geometry rejects backgrounds and detects relative row shapes', { skip: process.platform !== 'win32' }, () => {
  const root = fileURLToPath(new URL('../', import.meta.url));
  const csc = path.join(process.env.SystemRoot || 'C:\\Windows', 'Microsoft.NET/Framework64/v4.0.30319/csc.exe');
  assert.ok(existsSync(csc), 'Windows .NET Framework compiler is required');
  const temp = mkdtempSync(path.join(os.tmpdir(), 'codex-geometry-'));
  try {
    const binary = path.join(temp, 'geometry.exe');
    execFileSync(csc, ['/nologo', '/r:System.Drawing.dll', '/out:' + binary,
      path.join(root, 'native/SelectionGeometry.cs'), path.join(root, 'test/selection-geometry.test.cs')], { windowsHide: true });
    assert.match(execFileSync(binary, [], { encoding: 'utf8', windowsHide: true }), /PASS 119 geometry checks/);
  } finally { rmSync(temp, { recursive: true, force: true }); }
});
