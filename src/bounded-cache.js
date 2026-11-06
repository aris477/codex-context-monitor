// LRU bounds retention, not data availability: callers rebuild evicted entries
// from the existing disk cache or source log.
export class BoundedCache {
  constructor({ maxEntries = 8, maxBytes = 16 * 1024 * 1024 } = {}) {
    this.maxEntries = maxEntries;
    this.maxBytes = maxBytes;
    this.bytes = 0;
    this.entries = new Map();
  }

  get(key) {
    const entry = this.entries.get(key);
    if (!entry) return undefined;
    this.entries.delete(key);
    this.entries.set(key, entry);
    return entry.value;
  }

  set(key, value, bytes) {
    const previous = this.entries.get(key);
    if (previous) { this.bytes -= previous.bytes; this.entries.delete(key); }
    // JSON size is a retention budget, not a claim about JS heap object size.
    bytes ??= Buffer.byteLength(JSON.stringify(value));
    if (!Number.isFinite(bytes) || bytes < 0 || bytes > this.maxBytes || this.maxEntries < 1) return;
    this.entries.set(key, { value, bytes }); this.bytes += bytes;
    while (this.entries.size > this.maxEntries || this.bytes > this.maxBytes) {
      const oldest = this.entries.keys().next().value;
      this.bytes -= this.entries.get(oldest).bytes;
      this.entries.delete(oldest);
    }
  }

  clear() { this.entries.clear(); this.bytes = 0; }
}
