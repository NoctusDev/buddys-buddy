import http from 'node:http';
import { randomBytes } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

export function createServer() {
  const rooms = new Map();
  const send = (res, code, value) => { res.writeHead(code, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(JSON.stringify(value)); };
  const emit = (client, value) => client?.res?.write(`data: ${JSON.stringify(value)}\n\n`);
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://localhost');
      res.setHeader('X-Content-Type-Options', 'nosniff');
      res.setHeader('Referrer-Policy', 'no-referrer');
      if (req.method === 'GET' && ['/', '/app.js', '/style.css'].includes(url.pathname)) {
        const name = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
        const data = await readFile(new URL(`./public/${name}`, import.meta.url));
        res.writeHead(200, { 'Content-Type': name.endsWith('.js') ? 'text/javascript' : name.endsWith('.css') ? 'text/css' : 'text/html', 'Cache-Control': 'no-store' });
        return res.end(data);
      }
      if (req.method === 'POST' && url.pathname === '/rooms') {
        if (rooms.size >= 100) return send(res, 503, { error: 'Room limit reached' });
        const id = randomBytes(6).toString('hex');
        const token = randomBytes(24).toString('hex');
        rooms.set(id, { token, clients: new Map(), created: Date.now() });
        return send(res, 201, { room: id, token });
      }
      const match = url.pathname.match(/^\/rooms\/([a-f0-9]{12})\/(events|signal)$/);
      const room = match && rooms.get(match[1]);
      if (!room) return send(res, 404, { error: 'Room not found' });
      const id = url.searchParams.get('id');
      const host = id === 'host';
      if (!id || !/^[a-zA-Z0-9-]{1,50}$/.test(id)) return send(res, 400, { error: 'Invalid client' });
      if (host && url.searchParams.get('token') !== room.token) return send(res, 403, { error: 'Invalid broadcast key' });
      if (match[2] === 'events' && req.method === 'GET') {
        if (room.clients.has(id)) return send(res, 409, { error: 'Client already connected' });
        if (room.clients.size >= 17) return send(res, 409, { error: 'Prototype room is full' });
        res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store', Connection: 'keep-alive', 'X-Accel-Buffering': 'no' });
        res.write(': connected\n\n');
        const client = { res };
        room.clients.set(id, client);
        emit(client, { type: 'ready' });
        if (host) for (const peer of room.clients.keys()) { if (peer !== 'host') emit(client, { type: 'join', from: peer }); }
        else emit(room.clients.get('host'), { type: 'join', from: id });
        const timer = setInterval(() => res.write(': heartbeat\n\n'), 15000);
        req.on('close', () => {
          clearInterval(timer);
          room.clients.delete(id);
          if (host) for (const other of room.clients.values()) emit(other, { type: 'offline' });
          else emit(room.clients.get('host'), { type: 'leave', from: id });
        });
        return;
      }
      if (match[2] === 'signal' && req.method === 'POST') {
        if (!room.clients.has(id)) return send(res, 403, { error: 'Connect first' });
        // Browser requests must originate from this server (also rejects cross-site form posts).
        if (!req.headers['content-type']?.startsWith('application/json') ||
            (req.headers.origin && req.headers.origin !== `${req.socket.encrypted ? 'https' : 'http'}://${req.headers.host}`))
          return send(res, 403, { error: 'Invalid origin or content type' });
        let body = '';
        for await (const chunk of req) { body += chunk; if (body.length > 65536) return send(res, 413, { error: 'Signal too large' }); }
        const message = JSON.parse(body);
        if ((!host && message.to !== 'host') || (host && message.to === 'host')) return send(res, 403, { error: 'Invalid destination' });
        if (!['offer', 'answer', 'candidate'].includes(message.type)) return send(res, 400, { error: 'Invalid signal' });
        emit(room.clients.get(message.to), { type: message.type, data: message.data, from: id });
        return send(res, 200, { ok: true });
      }
      send(res, 405, { error: 'Method not allowed' });
    } catch { if (!res.headersSent) send(res, 400, { error: 'Invalid request' }); else res.end(); }
  });
  const cleanup = setInterval(() => {
    for (const [id, room] of rooms) if (!room.clients.size && Date.now() - room.created > 3600000) rooms.delete(id);
  }, 60000);
  cleanup.unref();
  server.on('close', () => clearInterval(cleanup));
  return server;
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const port = Number(process.env.PORT || 8788);
  createServer().listen(port, process.env.HOST || '127.0.0.1', () => console.log(`Venue Audio: http://localhost:${port}`));
}
