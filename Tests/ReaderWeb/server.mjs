import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { CSP, ASSETS } from './policy.mjs';

export async function startServer() {
    const files = new Map(ASSETS.map(name => [`/reader/${name}`, new URL(`../../Assets/Reader/${name}`, import.meta.url)]));
    for (const name of ['test.html', 'harness.js']) files.set(`/${name}`, new URL(name, import.meta.url));
    const sessions = new Map(), requests = [];
    const server = http.createServer(async (request, response) => {
        requests.push({ method: request.method, url: request.url });
        response.setHeader('Content-Security-Policy', CSP);
        response.setHeader('X-Content-Type-Options', 'nosniff');
        response.setHeader('Cache-Control', 'no-store');
        response.setHeader('Referrer-Policy', 'no-referrer');
        if (request.headers.host !== `127.0.0.1:${server.address().port}` || request.method !== 'GET') {
            response.writeHead(403).end(); return;
        }
        const file = files.get(request.url);
        if (file) {
            const extension = file.pathname.split('.').pop();
            response.setHeader('Content-Type', { js: 'text/javascript', html: 'text/html', css: 'text/css' }[extension]);
            response.end(await readFile(file)); return;
        }
        const session = /^\/book\/([a-zA-Z0-9-]{16,80})\.epub$/.exec(request.url)?.[1];
        const book = sessions.get(session);
        if (!book) { response.writeHead(404).end(); return; }
        response.setHeader('Content-Type', book.mime ?? 'application/epub+zip');
        if (book.redirect) { response.writeHead(302, { Location: book.redirect }).end(); return; }
        const info = await stat(book.path);
        if (!book.chunked) response.setHeader('Content-Length', book.declared ?? info.size);
        const stream = createReadStream(book.path, { highWaterMark: 64 * 1024 });
        response.once('close', () => stream.destroy());
        if (book.slow) {
            try {
                for await (const chunk of stream) {
                    if (response.destroyed) break;
                    response.write(chunk);
                    await new Promise(resolve => setTimeout(resolve, 30));
                }
                response.end();
            } catch (error) {
                if (!response.destroyed) response.destroy(error);
            }
        } else stream.pipe(response);
    });
    server.on('clientError', (_, socket) => socket.destroy());
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    return { origin: `http://127.0.0.1:${server.address().port}`, sessions, requests,
        close: () => new Promise(resolve => { server.closeAllConnections(); server.close(resolve); }) };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    const { createFixtures } = await import('./fixtures.mjs');
    const { randomUUID } = await import('node:crypto');
    const server = await startServer();
    const fixtures = await createFixtures();
    const id = randomUUID();
    server.sessions.set(id, { path: fixtures.text.path });
    console.log(`${server.origin}/test.html#${id}`);
    console.log('Test-only host; press Ctrl+C to stop.');
}
