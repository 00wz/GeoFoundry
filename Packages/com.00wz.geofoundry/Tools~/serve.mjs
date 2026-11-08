#!/usr/bin/env node
// Development server only. Node 18+, no dependencies. Does not modify/compress archive bytes.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export function createRangeServer(rootPath, corsOrigin) {
  const root = fs.realpathSync(rootPath);
  return http.createServer((req, res) => {
    res.setHeader('Cross-Origin-Opener-Policy', 'same-origin');
    res.setHeader('Cross-Origin-Embedder-Policy', 'require-corp');
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('Accept-Ranges', 'bytes');
    if (corsOrigin) {
      res.setHeader('Access-Control-Allow-Origin', corsOrigin);
      res.setHeader('Access-Control-Allow-Methods', 'GET, HEAD, OPTIONS');
      res.setHeader('Access-Control-Allow-Headers', 'Range, If-Range');
      res.setHeader('Access-Control-Expose-Headers', 'Content-Range, Content-Length, Content-Encoding, ETag');
    }
    if (req.method === 'OPTIONS') { res.writeHead(204).end(); return; }
    if (req.method !== 'GET' && req.method !== 'HEAD') { res.writeHead(405, { Allow: 'GET, HEAD, OPTIONS' }).end(); return; }
    let target;
    try {
      const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
      target = path.resolve(root, '.' + pathname);
      if (!target.startsWith(root + path.sep) && target !== root) throw new Error('Path traversal');
      if (fs.statSync(target).isDirectory()) target = path.join(target, 'index.html');
      target = fs.realpathSync(target); // Do not follow links outside the served root.
      if (!target.startsWith(root + path.sep)) throw new Error('Outside root');
      if (!fs.statSync(target).isFile()) throw new Error('Not a regular file');
    } catch { res.writeHead(404).end('Not found'); return; }
    const stat = fs.statSync(target), total = stat.size;
    let start = 0, end = total - 1, partial = false;
    if (req.headers.range) {
      const match = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range);
      if (!match || (!match[1] && !match[2])) { res.writeHead(416, { 'Content-Range': `bytes */${total}` }).end(); return; }
      if (!match[1]) start = Math.max(0, total - Number(match[2]));
      else { start = Number(match[1]); if (match[2]) end = Math.min(end, Number(match[2])); }
      if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || start > end || start >= total) {
        res.writeHead(416, { 'Content-Range': `bytes */${total}` }).end(); return;
      }
      partial = true;
      res.setHeader('Content-Range', `bytes ${start}-${end}/${total}`);
    }
    const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css',
      '.wasm': 'application/wasm', '.pmtiles': 'application/vnd.pmtiles', '.png': 'image/png', '.svg': 'image/svg+xml' };
    const ext = path.extname(target);
    if (ext === '.br' || ext === '.gz') {
      res.setHeader('Content-Encoding', ext === '.br' ? 'br' : 'gzip');
      res.setHeader('Content-Type', types[path.extname(target.slice(0, -ext.length))] || 'application/octet-stream');
    } else res.setHeader('Content-Type', types[ext] || 'application/octet-stream');
    res.setHeader('Content-Length', total === 0 ? 0 : end - start + 1);
    res.statusCode = partial ? 206 : 200;
    if (req.method === 'HEAD' || total === 0) { res.end(); return; }
    const stream = fs.createReadStream(target, { start, end });
    stream.on('error', () => res.destroy());
    res.on('close', () => stream.destroy());
    stream.pipe(res);
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const option = (name, fallback) => { const index = args.indexOf(name); return index < 0 ? fallback : args[index + 1]; };
  const root = path.resolve(option('--root', '.'));
  const host = option('--host', '127.0.0.1'), port = Number(option('--port', '8080'));
  const server = createRangeServer(root, option('--cors', null));
  server.on('error', error => { console.error(error.message); process.exitCode = 1; });
  server.listen(port, host, () => {
    console.log(`GeoFoundry range server: http://${host}:${server.address().port}/\nRoot: ${root}\nDevelopment only. Ctrl+C to stop.`);
  });
}
