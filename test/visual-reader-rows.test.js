import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

test('native reader merges the same painted row but preserves distinct same-title rows', { skip: process.platform !== 'win32' }, () => {
  const root = fileURLToPath(new URL('../', import.meta.url));
  const framework = path.join(process.env.SystemRoot || 'C:\\Windows', 'Microsoft.NET/Framework64/v4.0.30319');
  const temp = mkdtempSync(path.join(os.tmpdir(), 'codex-painted-row-'));
  try {
    const refs = ['System.Drawing.dll', 'System.Web.Extensions.dll',
      ...['System.Runtime.dll', 'System.Runtime.WindowsRuntime.dll'].map(f => path.join(framework, f)),
      ...['UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll'].map(f => path.join(framework, 'WPF', f)),
      ...['Foundation', 'Globalization', 'Media', 'Graphics', 'Storage'].map(f => path.join(process.env.SystemRoot || 'C:\\Windows', 'System32/WinMetadata', `Windows.${f}.winmd`))];
    const binary = path.join(temp, 'rows.exe');
    execFileSync(path.join(framework, 'csc.exe'), ['/nologo', '/codepage:65001', '/platform:x64', '/main:VisualReaderRowsTests', `/out:${binary}`,
      ...refs.map(f => `/r:${f}`), ...['VisualReader.cs', 'SelectionGeometry.cs', 'CodexForeground.cs'].map(f => path.join(root, 'native', f)),
      path.join(root, 'test/visual-reader-rows.test.cs')], { windowsHide: true });
    assert.match(execFileSync(binary, [], { encoding: 'utf8', windowsHide: true }), /PASS 14 painted-row checks/);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});
