import test from 'node:test';
import assert from 'node:assert/strict';
import { BoundedCache } from '../src/bounded-cache.js';

test('retention budget evicts least recently used detail without losing recent entries', () => {
  const cache = new BoundedCache({maxEntries: 2, maxBytes: 10});
  cache.set('A', {turns:[1]}, 4); cache.set('B', {turns:[2]}, 4);
  assert.deepEqual(cache.get('A'), {turns:[1]});
  cache.set('C', {turns:[3]}, 4);
  assert.equal(cache.get('B'), undefined);
  assert.deepEqual(cache.get('A'), {turns:[1]});
  assert.deepEqual(cache.get('C'), {turns:[3]});
  cache.set('D', {turns:[4]}, 7);
  assert.equal(cache.get('A'), undefined); assert.equal(cache.get('C'), undefined);
  assert.equal(cache.bytes, 7);
});

test('oversized detail can be returned by caller but is not retained; replacement resets size', () => {
  const cache = new BoundedCache({maxEntries: 2, maxBytes: 8});
  cache.set('A', {turns:[1]}, 3);
  cache.set('A', {turns:[2]}, 9);
  assert.equal(cache.get('A'), undefined); assert.equal(cache.bytes, 0);
  cache.set('A', {turns:[]}, 2); cache.set('A', {turns:[3]}, 4);
  assert.equal(cache.bytes, 4); assert.deepEqual(cache.get('A'), {turns:[3]});
  cache.clear(); assert.equal(cache.bytes, 0); assert.equal(cache.get('A'), undefined);
});
