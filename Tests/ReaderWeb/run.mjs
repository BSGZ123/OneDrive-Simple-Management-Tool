import assert from 'node:assert/strict';
import http from 'node:http';
import { randomUUID, createHash } from 'node:crypto';
import { writeFile, mkdir, readFile } from 'node:fs/promises';
import os from 'node:os';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';
import { createFixtures } from './fixtures.mjs';
import { startServer } from './server.mjs';
import './verify-vendor.mjs';

const performanceRun = process.argv.includes('--performance');
const results = [], consoleErrors = [], pageErrors = [], network = [], probes = [];
const probe = http.createServer((req, res) => { probes.push(req.url); res.writeHead(200).end('probe'); });
await new Promise(resolve => probe.listen(0, '127.0.0.1', resolve));
const server = await startServer();
const fixtures = await createFixtures(`http://127.0.0.1:${probe.address().port}`, performanceRun);
let browser;
const report = { sourceBase: 'e1ef6d0', engine: '78914aef4466eb960965702401634c2cb348e9b1',
    dateUtc: new Date().toISOString(), os: `${os.platform()} ${os.release()} ${os.arch()}`,
    node: process.version, results, fixtures: Object.fromEntries(Object.entries(fixtures).map(([id, { path, ...info }]) => [id, info])),
    network, consoleErrors, pageErrors, probes };
report.sourceHashes = {};
for (const file of ['fixtures.mjs', 'run.mjs', 'policy.mjs', '../../Assets/Reader/reader-adapter.js',
    '../../Assets/Reader/epub-loader.js', '../../Assets/Reader/protocol.js'])
    report.sourceHashes[file] = createHash('sha256').update(await readFile(new URL(file, import.meta.url))).digest('hex');
async function test(name, action) {
    const started = Date.now();
    try { const detail = await action(); results.push({ name, passed: true, ms: Date.now() - started, detail }); console.log(`PASS ${name}`); }
    catch (error) { results.push({ name, passed: false, ms: Date.now() - started, error: error.stack }); console.error(`FAIL ${name}: ${error.message}`); }
}
async function pageFor(id, overrides = {}) {
    const sessionId = randomUUID();
    server.sessions.set(sessionId, { path: fixtures[id].path, ...overrides });
    const page = await browser.newPage({ viewport: { width: 1000, height: 760 } });
    page.setDefaultTimeout(8000);
    page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text().slice(0, 500)); });
    page.on('pageerror', error => pageErrors.push(error.name));
    page.on('request', request => {
        if (!request.url().startsWith(server.origin) && !request.url().startsWith('blob:') && !request.url().startsWith('data:'))
            network.push({ url: request.url(), type: request.resourceType() });
    });
    await page.goto(`${server.origin}/test.html#${sessionId}`);
    await page.waitForFunction(() => window.host && window.messages.some(x => x.type === 'ready'));
    return { page, sessionId };
}
async function command(page, type, payload = {}) {
    const { requestId, accepted } = await page.evaluate(({ type, payload }) => host.command(type, payload), { type, payload });
    assert.equal(accepted, true);
    await page.waitForFunction(id => messages.some(x => x.requestId === id && ['commandCompleted', 'readerError'].includes(x.type)), requestId, { timeout: 25000 });
    const result = await page.evaluate(id => messages.find(x => x.requestId === id && ['commandCompleted', 'readerError'].includes(x.type)), requestId);
    assert.notEqual(result.type, 'readerError', JSON.stringify(result.payload));
}
async function open(page, location = null, settings = {}) {
    const { requestId } = await page.evaluate(({ location, settings }) => host.open(location, settings), { location, settings });
    await page.waitForFunction(id => messages.some(x => x.requestId === id && ['opened', 'readerError'].includes(x.type)), requestId, { timeout: 25000 });
    const result = await page.evaluate(id => messages.find(x => x.requestId === id && ['opened', 'readerError'].includes(x.type)), requestId);
    assert.equal(result.type, 'opened', JSON.stringify(result.payload));
    return result.payload;
}
const location = page => page.evaluate(() => messages.filter(x => x.type === 'locationChanged').at(-1)?.payload);
async function dispose(page) {
    await page.evaluate(() => host.close());
    await page.waitForFunction(() => liveBlobs.size === 0, null, { timeout: 5000 });
    assert.equal(await page.locator('foliate-view').count(), 0);
    await page.waitForFunction(() => activeResizeTargets === 0);
    await page.close();
}
async function processSample() {
    if (process.platform !== 'win32') return null;
    const session = await browser.newBrowserCDPSession();
    const { processInfo } = await session.send('SystemInfo.getProcessInfo');
    await session.detach();
    const ids = processInfo.map(x => x.id).filter(Number.isSafeInteger);
    const { stdout } = await promisify(execFile)('powershell.exe', ['-NoProfile', '-Command',
        `Get-Process -Id ${ids.join(',')} -ErrorAction SilentlyContinue | Select-Object Id,WorkingSet64,PrivateMemorySize64,HandleCount | ConvertTo-Json -Compress`], { windowsHide: true });
    const processes = JSON.parse(stdout);
    return (Array.isArray(processes) ? processes : [processes]).reduce((sum, x) => ({
        workingBytes: sum.workingBytes + x.WorkingSet64, privateBytes: sum.privateBytes + x.PrivateMemorySize64,
        handles: sum.handles + x.HandleCount, processes: sum.processes + 1,
    }), { workingBytes: 0, privateBytes: 0, handles: 0, processes: 0 });
}
try {
    browser = await chromium.launch({ channel: process.env.READER_BROWSER_CHANNEL ?? 'msedge', headless: true });
    report.browser = browser.version();
    await test('protocol rejects malformed, stale, oversized and unsupported messages', async () => {
        const { page, sessionId } = await pageFor('text');
        const accepted = await page.evaluate(async sessionId => {
            const { parseCommand, validLocation, mapToc, tocChunks } = await import('/reader/protocol.js');
            const base = { version: 1, sessionId, requestId: crypto.randomUUID(), type: 'applySettings', payload: {} };
            const bad = [null, '{', { ...base, version: 2 }, { ...base, sessionId: 'stale-session-id-000' },
                { ...base, type: 'executeScript' }, { ...base, payload: { fontSize: 999 } },
                { ...base, payload: { width: '720; background:red' } },
                { ...base, type: 'restoreLocation', payload: { fraction: 1.1 } },
                { ...base, type: 'restoreLocation', payload: { cfi: 'x'.repeat(40000) } },
                { ...base, type: 'openBook', payload: { settings: {}, url: 'file:///private' } }];
            let oversizedToc = false;
            try { mapToc(Array.from({ length: 1025 }, () => ({ label: 'node' }))); } catch { oversizedToc = true; }
            const { nodes } = mapToc(Array.from({ length: 200 }, () => ({ label: '目录'.repeat(128), href: 'chapter.xhtml' })));
            const chunks = tocChunks(nodes);
            return { rejected: bad.every(x => !parseCommand(x, sessionId)),
                finite: !validLocation({ fraction: Infinity }) && !validLocation({ fraction: NaN }), oversizedToc,
                boundedUtf8: chunks.every(chunk => new TextEncoder().encode(JSON.stringify(chunk)).length <= 32768 - 512)
                    && chunks.flatMap(x => x.nodes).length === 200 };
        }, sessionId);
        assert.deepEqual(accepted, { rejected: true, finite: true, oversizedToc: true, boundedUtf8: true });
        await dispose(page);
    });
    for (const id of ['text', 'epub2', 'images', 'fixed', 'rtl', 'vertical', 'empty', 'chapters', 'long']) {
        await test(`${id}: visible body, bounded TOC, next page, close/revoke`, async () => {
            const { page } = await pageFor(id);
            const opened = await open(page);
            assert.ok(opened.location.cfi);
            assert.equal(opened.fixedLayout, id === 'fixed');
            const chapter = page.frames().find(frame => frame.url().startsWith('blob:'));
            assert.ok(chapter);
            assert.match(await chapter.locator('body').innerText(), /Chapter/);
            if (id === 'images') assert.equal(await chapter.locator('img').evaluate(img => img.complete && img.naturalWidth === 512), true);
            if (['images', 'fixed', 'rtl', 'vertical'].includes(id)) await page.screenshot({
                path: new URL(`artifacts/reader-${id}.png`, import.meta.url).pathname.replace(/^\/(\w:)/, '$1'),
            });
            const chunks = await page.evaluate(() => messages.filter(x => x.type === 'tocChunk').map(x => x.payload));
            assert.ok(chunks.every(x => x.nodes.length <= 64));
            assert.equal(chunks.flatMap(x => x.nodes).length, chunks[0].total);
            if (id === 'empty') assert.equal(chunks[0].total, 0);
            if (id === 'chapters') assert.equal(chunks.length, 2);
            const before = await location(page);
            await command(page, 'turn', { direction: 'next' });
            const after = await location(page);
            assert.notEqual(before.cfi, after.cfi);
            await dispose(page);
            return opened;
        });
    }
    await test('TOC, internal link and chapter keyboard navigation', async () => {
        const { page } = await pageFor('text');
        await open(page);
        await command(page, 'navigateToToc', { id: 'toc-2' });
        let chapter = page.frames().find(frame => frame.url().startsWith('blob:'));
        assert.match(await chapter.locator('h1').innerText(), /Chapter 2/);
        await chapter.locator('#internal').click();
        await page.waitForFunction(() => messages.filter(x => x.type === 'locationChanged').at(-1)?.payload.cfi.includes('/6/6'));
        chapter = page.frames().find(frame => frame.url().startsWith('blob:'));
        assert.match(await chapter.locator('h1').innerText(), /Chapter 3/);
        const before = await location(page);
        await chapter.locator('#input').focus();
        await page.keyboard.press('ArrowRight');
        assert.equal((await location(page)).cfi, before.cfi);
        await chapter.locator('body').click({ position: { x: 20, y: 30 } });
        await page.keyboard.press('PageDown');
        await page.waitForFunction(before => messages.filter(x => x.type === 'locationChanged').at(-1)?.payload.cfi !== before, before.cfi);
        await dispose(page);
    });
    await test('TOC targets without fragments navigate to the start of the correct chapter', async () => {
        const { page } = await pageFor('noFragment');
        await open(page);
        await command(page, 'restoreLocation', { fraction: 0.6 });
        await command(page, 'navigateToToc', { id: 'toc-0' });
        let frame = page.frames().find(x => x.url().startsWith('blob:'));
        assert.match(await frame.locator('h1').innerText(), /Chapter 1/);
        assert.ok((await location(page)).fraction < 0.1);
        await command(page, 'navigateToToc', { id: 'toc-2' });
        frame = page.frames().find(x => x.url().startsWith('blob:'));
        assert.match(await frame.locator('h1').innerText(), /Chapter 2/);
        await dispose(page);
    });
    await test('CFI restore across reopen, settings, scroll and resize; corrupt CFI falls back', async () => {
        const first = await pageFor('text');
        await open(first.page);
        await command(first.page, 'restoreLocation', { fraction: 0.5 });
        const saved = await location(first.page);
        assert.ok(saved.fraction > 0.3);
        await dispose(first.page);
        const { page } = await pageFor('text');
        assert.equal((await open(page, saved)).restoredBy, 'cfi');
        await command(page, 'applySettings', { fontSize: 28, lineHeight: 1.9, width: 560, theme: 'dark', flow: 'scrolled' });
        assert.ok(Math.abs((await location(page)).fraction - saved.fraction) < 0.1);
        await page.setViewportSize({ width: 540, height: 650 });
        await command(page, 'restoreLocation', saved);
        assert.ok(Math.abs((await location(page)).fraction - saved.fraction) < 0.1);
        await command(page, 'restoreLocation', { cfi: 'epubcfi(/999999/999999!)', fraction: 0.5 });
        assert.equal(await page.evaluate(() => messages.filter(x => x.type === 'restored').at(-1).payload.restoredBy), 'fraction');
        await command(page, 'restoreLocation', { cfi: 'epubcfi(invalid)' });
        assert.equal(await page.evaluate(() => messages.filter(x => x.type === 'restored').at(-1).payload.restoredBy), 'start');
        await page.screenshot({ path: new URL('artifacts/reader-dark-narrow.png', import.meta.url).pathname.replace(/^\/(\w:)/, '$1') });
        await dispose(page);
    });
    await test('fixed layout zoom and restore use supported renderer capabilities', async () => {
        const { page } = await pageFor('fixed');
        await open(page);
        await command(page, 'turn', { direction: 'next' });
        const saved = await location(page);
        await command(page, 'applySettings', { zoom: 'fit-width', fontSize: 30 });
        assert.equal((await location(page)).cfi, saved.cfi);
        await command(page, 'applySettings', { zoom: 1.5 });
        await dispose(page);
    });
    await test('malicious EPUB cannot execute scripts, request external resources or call host', async () => {
        const { page } = await pageFor('attack');
        await open(page);
        const chapter = page.frames().find(frame => frame.url().startsWith('blob:'));
        await chapter.locator('#javascript').click();
        await chapter.locator('#external').click();
        await page.waitForFunction(() => messages.some(x => x.type === 'externalLinkRequested'));
        assert.deepEqual(await page.evaluate(() => executions), []);
        assert.equal(await page.evaluate(() => messages.filter(x => x.type === 'externalLinkRequested').length), 1);
        assert.equal(probes.length, 0);
        assert.equal(await page.locator('foliate-view').count(), 1);
        // Plain window messages are not host commands even when their fields are plausible.
        await page.evaluate(() => window.postMessage({ version: 1, type: 'close', sessionId: location.hash.slice(1), requestId: crypto.randomUUID(), payload: {} }, '*'));
        assert.equal(await page.locator('foliate-view').count(), 1);
        await dispose(page);
    });
    await test('CSP inheritance blocks inline, blob/data scripts and external requests even with script sandbox permission', async () => {
        const { page } = await pageFor('text');
        const marker = await page.evaluate(async probe => {
            const code = "parent.executions.push('CSP bypass')";
            const script = URL.createObjectURL(new Blob([code], { type: 'text/javascript' }));
            const markup = `<html><body><script>${code}<\/script><script src="${script}"><\/script><script src="data:text/javascript,${encodeURIComponent(code)}"><\/script><script src="${probe}/script"><\/script><img src="${probe}/image" onerror="${code}"><style>@import url('${probe}/style');</style></body></html>`;
            const blob = URL.createObjectURL(new Blob([markup], { type: 'text/html' }));
            const frame = document.createElement('iframe');
            frame.setAttribute('sandbox', 'allow-same-origin allow-scripts');
            const loaded = new Promise(resolve => frame.onload = resolve);
            frame.src = blob; document.body.append(frame); await loaded;
            const value = [...executions];
            frame.remove(); URL.revokeObjectURL(blob); URL.revokeObjectURL(script);
            return value;
        }, `http://127.0.0.1:${probe.address().port}`);
        assert.deepEqual(marker, []);
        assert.equal(probes.length, 0);
        assert.ok(consoleErrors.some(x => /Content Security Policy|content security policy/.test(x)));
        await dispose(page);
    });
    for (const [id, overrides, expected] of [
        ['invalid', {}, 'LoadFailed'], ['bomb', {}, 'BookLimit'], ['traversal', {}, 'InvalidBook'],
        ['forged', {}, 'InvalidBook'],
        ['drm', {}, 'UnsupportedEncryption'],
        ['text', { declared: 100_000_001 }, 'BookLimit'], ['text', { mime: 'text/html' }, 'LoadFailed'],
        ['text', { redirect: '/reader/index.html' }, 'LoadFailed'],
    ]) await test(`reject ${id} ${JSON.stringify(overrides)}`, async () => {
        const { page } = await pageFor(id, overrides);
        await page.evaluate(() => host.open());
        await page.waitForFunction(() => messages.some(x => x.type === 'readerError'), null, { timeout: 25000 });
        assert.equal(await page.evaluate(() => messages.find(x => x.type === 'readerError').payload.code), expected);
        assert.equal(await page.evaluate(() => messages.some(x => x.type === 'opened')), false);
        await dispose(page);
    });
    await test('chunked downloads enforce actual byte limit and cancel the response stream', async () => {
        const { page } = await pageFor('text');
        const result = await page.evaluate(async () => {
            const { fetchBook } = await import('/reader/epub-loader.js');
            const original = window.fetch;
            let cancelled = false, sent = 0;
            const chunk = new Uint8Array(1_000_000);
            window.fetch = async () => new Response(new ReadableStream({
                pull(controller) { controller.enqueue(chunk); if (++sent === 110) controller.close(); },
                cancel() { cancelled = true; },
            }), { headers: { 'Content-Type': 'application/epub+zip' } });
            let error;
            try { await fetchBook('/book/mock.epub', new AbortController().signal); }
            catch (e) { error = e.message; }
            finally { window.fetch = original; }
            return { error, cancelled };
        });
        assert.deepEqual(result, { error: 'BookLimit', cancelled: true });
        await dispose(page);
    });
    await test('close during fetch is idempotent and ignores old session work', async () => {
        const { page } = await pageFor('images', { slow: true, chunked: true });
        await page.evaluate(() => { host.open(); host.close(); host.close(); });
        await page.waitForTimeout(300);
        assert.deepEqual(await page.evaluate(() => messages.filter(x => ['opened', 'locationChanged', 'readerError'].includes(x.type))), []);
        assert.equal(await page.evaluate(() => liveBlobs.size), 0);
        await dispose(page);
    });
    await test('close during renderer initialization cannot publish or leak late book resources', async () => {
        const { page } = await pageFor('long');
        await page.evaluate(() => {
            const observer = new MutationObserver(() => {
                if (document.querySelector('foliate-view')) { observer.disconnect(); void host.close(); }
            });
            observer.observe(document.getElementById('reader'), { childList: true });
            host.open();
        });
        await page.waitForTimeout(500);
        assert.deepEqual(await page.evaluate(() => messages.filter(x => ['opened', 'locationChanged', 'readerError'].includes(x.type))), []);
        assert.equal(await page.evaluate(() => liveBlobs.size), 0);
        await dispose(page);
    });
    await test('unknown routes, encoded paths and invalid methods are refused', async () => {
        for (const path of ['/reader/../appsettings.json', '/reader/%2e%2e/appsettings.json', '/book/stale.epub', '/reader/index.html?other']) {
            const response = await fetch(server.origin + path);
            assert.equal(response.status, 404);
        }
        assert.equal((await fetch(server.origin + '/reader/index.html', { method: 'POST' })).status, 403);
    });
    await test('five normal reading sessions release layout observers and ignore resize after close', async () => {
        const { page } = await pageFor('images');
        for (let i = 0; i < 5; i++) {
            const id = randomUUID();
            server.sessions.set(id, { path: fixtures[i % 2 ? 'fixed' : 'images'].path });
            await page.evaluate(id => host.resetSession(id), id);
            await open(page);
            assert.ok(await page.evaluate(() => activeResizeTargets > 0));
            await command(page, 'turn', { direction: 'next' });
            await command(page, 'applySettings', { theme: i % 2 ? 'dark' : 'light', fontSize: 24 });
            await page.evaluate(() => host.close());
            await page.waitForFunction(() => liveBlobs.size === 0 && activeResizeTargets === 0);
            assert.equal(await page.locator('foliate-view').count(), 0);
            const messagesBeforeResize = await page.evaluate(() => messages.length);
            await page.setViewportSize({ width: 900 + i * 10, height: 720 });
            await page.waitForTimeout(100);
            assert.equal(await page.evaluate(() => messages.length), messagesBeforeResize);
        }
        await dispose(page);
    });
    if (performanceRun) {
        await test('96 MB book: bounded Blob path, first body and cancellation', async () => {
            const { page } = await pageFor('nearLimit');
            const cdp = await page.context().newCDPSession(page);
            await cdp.send('Performance.enable');
            const before = await processSample();
            const opened = await open(page);
            const metrics = await cdp.send('Performance.getMetrics');
            const ready = await processSample();
            await dispose(page);
            const cancelled = await pageFor('nearLimit', { slow: true, chunked: true });
            await cancelled.page.evaluate(() => host.open());
            await cancelled.page.waitForTimeout(150);
            const start = Date.now();
            await dispose(cancelled.page);
            return { opened, before, ready, metrics: metrics.metrics.filter(x => ['JSHeapUsedSize', 'JSHeapTotalSize', 'Documents', 'Nodes'].includes(x.name)), cancelMs: Date.now() - start };
        });
        await test('30 browser open/close cycles release every book Blob URL', async () => {
            const samples = [];
            const { page } = await pageFor('images');
            const cdp = await page.context().newCDPSession(page);
            for (let i = 1; i <= 30; i++) {
                const id = randomUUID();
                server.sessions.set(id, { path: fixtures[i % 3 === 0 ? 'fixed' : 'images'].path });
                await page.evaluate(id => host.resetSession(id), id);
                await open(page);
                await page.evaluate(() => host.close());
                await page.waitForFunction(() => liveBlobs.size === 0);
                if (i % 5 === 0) samples.push({ cycle: i, dom: await cdp.send('Memory.getDOMCounters'), processes: await processSample() });
            }
            await dispose(page);
            return samples;
        });
    }
    await test('raster headers, SVG complexity, CSS effects and Data images obey resource budgets', async () => {
        const { page } = await pageFor('text');
        const result = await page.evaluate(async () => {
            const { rasterPixels, checkSvg, checkCss } = await import('/reader/resource-budget.js');
            const fails = action => { try { action(); return false; } catch (e) { return e.message === 'BookLimit'; } };
            const png = new Uint8Array(45); const view = new DataView(png.buffer);
            png.set([137,80,78,71,13,10,26,10]); view.setUint32(8, 13); png.set([73,72,68,82], 12);
            view.setUint32(16, 512); view.setUint32(20, 512); png.set([73,69,78,68], 37);
            const normal = rasterPixels(png) === 512 * 512;
            view.setUint32(16, 1_000_000); const huge = fails(() => rasterPixels(png)); view.setUint32(16, 512);
            png.set([97,99,84,76], 37); const animation = fails(() => rasterPixels(png));
            const svg = new DOMParser().parseFromString('<svg xmlns="http://www.w3.org/2000/svg"><filter/></svg>', 'application/xml');
            const svgBlocked = fails(() => checkSvg(svg.documentElement));
            const cssBlocked = fails(() => checkCss('*{filter:blur(999px)}'));
            const refused = await new Promise(resolve => {
                const image = new Image(); image.onload = () => resolve(false); image.onerror = () => resolve(true);
                image.src = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';
            });
            return { normal, huge, animation, svgBlocked, cssBlocked, refused };
        });
        assert.ok(Object.values(result).every(Boolean), JSON.stringify(result));
        await dispose(page);
    });
    await test('chapter layout and close produce no uncaught browser errors', async () => {
        assert.deepEqual(pageErrors, []);
    });
} finally {
    await browser?.close(); await server.close(); await new Promise(resolve => probe.close(resolve));
    await mkdir(new URL('artifacts/', import.meta.url), { recursive: true });
    await writeFile(new URL('artifacts/results.json', import.meta.url), JSON.stringify(report, null, 2));
}
if (results.some(x => !x.passed)) process.exitCode = 1;
console.log(`${results.filter(x => x.passed).length}/${results.length} checks passed`);
