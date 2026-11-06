# Start the monitor with the Codex app (Windows)

This optional local companion starts the dashboard when the official Windows Store Codex app is running. It detects the application process, not account sign-in, and never reads authentication data. It does not modify Codex application files or install a Codex plugin.

Run `Setup-CodexCompanion.ps1` with Windows PowerShell to compile the helper and install a shortcut in the current user's Startup folder. Node.js 24 or newer is required. When the Visual Studio C++ toolchain is installed, setup prefers the small native C++ listener built by `Build-NativeCompanion.ps1`; otherwise it falls back to the installed .NET Framework C# compiler. The helper and runtime data stay under `.codex-usage/`; no downloaded executable or administrator service is needed.

The helper checks every 15 seconds. It keeps the dashboard stopped while Codex is absent, allows 30 seconds for an application restart before stopping the managed dashboard, and retries a failed dashboard launch at most once per minute. It reuses a verified running dashboard. Stop decisions require the current health response and local server identity to match; unrelated processes are not stopped. Only the official Codex WindowsApps executable qualifies, excluding ordinary ChatGPT and the Codex CLI.

`stop-local.js` (or the Stop shortcut) pauses automatic restart until Codex fully exits, or until `Start-Dashboard.ps1` is run manually. `Start-Dashboard.ps1 -NoBrowser` starts the service without opening a browser. The companion itself never opens a browser. Status and child exit information are recorded in `.codex-usage/companion.log` with a size limit.

Run `Disable-CodexCompanion.ps1` to remove this installation's Startup shortcut and let its listener exit within 15 seconds. This does not stop an already running dashboard; use Stop separately if desired.

The listener remains running after Windows sign-in so that launching Codex from its original shortcut is detected. A computer shutdown or user sign-out ends both processes. If the project directory moves, run setup again from the new directory.

### Memory and foreground detection

In one Windows measurement, the startup listener working set decreased from about 43 MiB (.NET) to 12 MiB (native C++). This is a listener-only comparison, not a claim about total dashboard memory. OCR buffers avoid duplicate full-image managed allocations, and turn-detail memory retention is limited to eight entries with a 16 MiB serialized-JSON budget. That budget is not a JavaScript heap limit. Evicted detail remains available from the existing disk cache or source logs.

Start the companion from the normal Windows desktop, not a restricted tool sandbox: the visual reader needs access to the interactive foreground window. The reader distinguishes an inaccessible desktop from another foreground application and uses limited process-query access to check the executable path. A genuine non-Codex foreground window still pauses identification.

This feature was mainly generated with OpenAI Codex. Validation covered 19 native controller/parser/path checks, live recovery after dashboard termination, manual Stop/resume, short and truncated long chat titles, analysis loading, and turn-detail retrieval after cache eviction. Windows sign-in, closing the active Codex app, and the C# fallback on a machine without a C++ toolchain were not exercised in the final desktop pass.
