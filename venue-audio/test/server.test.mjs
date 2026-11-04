import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from '../server.mjs';

test('room authorization, signaling, late joins and broadcaster disconnect', async () => {
  const server = createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const controllers = [];
  const post = (path, body) => fetch(base + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  async function stream(path) {
    const controller = new AbortController(); controllers.push(controller);
    const response = await fetch(base + path, { signal: controller.signal });
    assert.equal(response.status, 200);
    const reader = response.body.getReader(); let buffer = '';
    return { controller, async next() {
      for (;;) {
        const boundary = buffer.indexOf('\n\n');
        if (boundary >= 0) {
          const line = buffer.slice(0, boundary); buffer = buffer.slice(boundary + 2);
          if (line.startsWith('data: ')) return JSON.parse(line.slice(6));
          continue;
        }
        const result = await reader.read();
        if (result.done) throw new Error('Unexpected stream end');
        buffer += new TextDecoder().decode(result.value);
      }
    } };
  }
  try {
    const { room, token } = await (await post('/rooms', {})).json();
    const root = `/rooms/${room}`;
    assert.equal((await fetch(base + root + '/events?id=host&token=wrong')).status, 403);
    const listener = await stream(root + '/events?id=listener');
    assert.equal((await listener.next()).type, 'ready');
    const host = await stream(root + `/events?id=host&token=${token}`);
    assert.equal((await host.next()).type, 'ready');
    assert.deepEqual(await host.next(), { type: 'join', from: 'listener' });
    assert.equal((await post(root + '/signal?id=listener', { to: 'other-listener', type: 'offer' })).status, 403);
    assert.equal((await post(root + '/signal?id=stranger', { to: 'host', type: 'answer' })).status, 403);
    assert.equal((await post(root + `/signal?id=host&token=${token}`, { to: 'listener', type: 'offer', data: { sdp: 'test' } })).status, 200);
    assert.deepEqual(await listener.next(), { type: 'offer', from: 'host', data: { sdp: 'test' } });
    assert.equal((await post(root + '/signal?id=listener', { to: 'host', type: 'answer', data: { sdp: 'reply' } })).status, 200);
    assert.equal((await host.next()).type, 'answer');
    assert.equal((await post(root + '/signal?id=listener', { to: 'host', type: 'bogus' })).status, 400);
    host.controller.abort();
    assert.equal((await listener.next()).type, 'offline');
    assert.equal((await fetch(base + '/app.js')).status, 200);
    assert.equal((await fetch(base + '/../../package.json')).status, 404);
  } finally {
    controllers.forEach(c => c.abort()); server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
});
