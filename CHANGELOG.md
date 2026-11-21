# Changelog

All notable changes to this project will be documented in this file. This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and semantic versioning.

## [Unreleased]

### Fixed

- Recognize full sidebar ListItem bounds in newer Codex builds, whose inner chat buttons expose ExpandCollapse instead of Invoke. Keep legacy button layouts and the independent geometry/title checks. Retry temporarily missing layout/title accessibility reads with bounded fresh checks, and merge only same-title candidates whose painted bounds differ by at most two DPI-scaled pixels; distinct same-named rows remain ambiguous. Add 14 native painted-row regressions.

- Distinguish official Codex package identity, main/child windows, owned popups, inaccessible process queries, and other foreground applications. Retry transient missing HWND/process metadata without reusing an old chat identity.
- When a small floating layered/tool window temporarily takes focus, inspect the first ordinary visible window beneath it. Continue only for that verified Codex window; pause for another app, large overlays, hidden/minimized windows, and ambiguous desktop state. Add native foreground transition regressions.

- Replace RGB fill-distance selection with relative grayscale geometry: paired edges, side boundaries, rounded-corner recovery, local noise, and peer-row comparison. Locate the sidebar from accessible control bounds when available, and verify visible selection candidates against the live title so hovering another row cannot steal identity. Remove frame identity caches.
- Compile and hash both native sources. Add Windows-native geometry regressions for light/dark fills, gradients, noise, DPI scales, touching hover rows, and background false positives.

- Improve sidebar selection detection on translucent backgrounds using local color contrast, selection bands, and row OCR fallback. Keep the default layout path; no wallpaper or theme dependency is added.

- Prefer the accessible toolbar title before OCR. Never reuse a full-frame cache for an unverified OCR title, and recheck the live accessible title before reusing a verified frame, so a transient title error during navigation cannot persist for 60 seconds.

- Keep identifying a unique complete accessible chat-toolbar title when its selected sidebar row scrolls out of view. OCR titles retain the selected-row cross-check; duplicate titles and stale samples remain rejected.

- Read the leading chat-title dropdown button in newer Codex toolbars through Windows accessibility. Keep the selected sidebar row and complete title cross-check, without treating the dropdown arrow as OCR title text.

## [0.2.0] - 2026-08-21

### Added

- Per-session selection and URL binding with `sessionId`.
- Native per-turn token, cache, API-equivalent cost, and context-window analysis.
- Per-turn dashboard table and session insight cards.
- `codex-usage turn` and Codex notify-hook snapshot support.
- Versioned GPT-5.6 Sol pricing rules and long-context multipliers.
- Idempotent Windows LAN installer, scheduled-task supervisor, firewall setup, rollback, and deployment verifier.
- Windows CI coverage on Node.js 22 and 24.

### Changed

- Growing Codex JSONL files now resume from a persisted byte offset and parser state instead of reparsing their historical contents.
- SQLite hourly rollups keep dashboard refreshes responsive for multi-gigabyte histories.
- Codex notify integration preserves and chains an existing notification command.

### Security

- Windows firewall rules default to the Private profile and `LocalSubnet` only.
- Hook failures are logged without storing notification payloads, and deployment scripts never read `auth.json`.
