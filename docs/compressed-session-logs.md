# Compressed session logs

The monitor reads both `.jsonl` and Zstandard-compressed `.jsonl.zst` files under
the discovered `sessions` and `archived_sessions` directories. Compressed logs
require Node.js 24 or another Node.js runtime exposing `zlib.createZstdDecompress`.
Decompression streams directly into the parser; it does not write an unpacked
copy or modify the original session files.

If a plain and compressed copy share the same path before the `.zst` suffix, the
plain copy is counted once. Changed compressed files are parsed from the start;
unchanged archives retain their cached statistics. A corrupt archive produces a
scan warning while healthy files continue to be indexed.

The state database may retain a rollout path ending in `.jsonl` after compression,
including a Windows extended path beginning with `\\?\`. The monitor normalizes
these paths when assigning chat IDs. Where the state database identifies an
available current rollout, older resumed snapshots with that original chat ID in
their filenames are excluded to avoid adding repeated cumulative counters.
Explicitly registered forks retain their separate identities.

An identified chat can still have unknown reference occupancy if its readable logs
do not contain both the latest request input token count and a positive reported
context window. The occupancy is a historical log sample, not a live measurement
of model memory or verification of a third-party provider's actual context limit.
