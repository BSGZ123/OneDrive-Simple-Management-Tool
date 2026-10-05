// Opt-in verification of a user-supplied EPUB. Never copy it to fixtures or log
// its path, title, text, URLs, CFI or raw exceptions. Screenshots stay ignored.
import { chromium } from 'playwright';
import { readFile, stat, mkdir, writeFile } from 'node:fs/promises';
import { createHash, randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import os from 'node:os';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { startServer } from './server.mjs';
import './verify-vendor.mjs';

const argument = process.argv.indexOf('--book');
if (argument < 0 || !process.argv[argument + 1]) throw new Error('Use --book <local EPUB path>');
const bookPath = path.resolve(process.argv[argument + 1]);
const size = (await stat(bookPath)).size;
if (size > 100_000_000 || !/\.epub$/i.test(bookPath)) throw new Error('Unsupported input');
const hash = async file => createHash('sha256').update(await readFile(file)).digest('hex');
const sourceHash = await hash(bookPath);
const cycleArgument = process.argv.indexOf('--cycles');
const cycles = cycleArgument < 0 ? 5 : Number(process.argv[cycleArgument + 1]);
if (!Number.isInteger(cycles) || cycles < 0 || cycles > 120) throw new Error('Invalid cycle count');
const output = new URL('artifacts/local-book/', import.meta.url);
await mkdir(output, { recursive: true });
const results = [];
const report = { checkedAtUtc: new Date().toISOString(), environment: {
    os: `${os.platform()} ${os.release()} ${os.arch()}`, node: process.version,
}, sample: { id: 'local-book-01', sha256: sourceHash, bytes: size }, sourceHashes: {},
    cycles, results, diagnostics: { consoleErrors: 0, pageErrors: 0, pageErrorDetails: [], blockedExternalRequests: 0 } };
for (const file of ['verify-book.mjs', 'policy.mjs', 'harness.js', '../../Assets/Reader/dependencies.json', '../../Assets/Reader/reader-adapter.js',
    '../../Assets/Reader/epub-loader.js', '../../Assets/Reader/protocol.js'])
    report.sourceHashes[file] = await hash(new URL(file, import.meta.url));
const dependencies = JSON.parse(await readFile(new URL('../../Assets/Reader/dependencies.json', import.meta.url)));
report.engine = { commit: dependencies.foliate.commit, zipVersion: dependencies.zip.version, patches: dependencies.patches };
const server = await startServer();
let browser, summary, navigation, sectionLocations, saved, activeCheck;
const ensure = (value, code) => { if (!value) throw new Error(code); };
const knownCode = error => /^(?:Check|Book|Invalid|Load|Timed|Unsupported|No|Image|Position|Command|Unexpected|Empty|Layout|Restore|Font|Resource|Text|Toc|Sample|External|Closed)[A-Za-z0-9]*$/.test(error.message)
    ? error.message : error.name === 'TimeoutError' ? 'TimedOut' : 'OperationFailed';
async function test(name, action) {
    activeCheck = name;
    const start = Date.now();
    try { const detail = await action(); results.push({ name, passed: true, elapsedMs: Date.now() - start, detail }); console.log(`PASS ${name}`); }
    catch (error) { const code = knownCode(error); results.push({ name, passed: false, elapsedMs: Date.now() - start, code }); console.log(`FAIL ${name}: ${code}`); }
}
function register(options = {}) {
    const id = randomUUID();
    server.sessions.set(id, { path: bookPath, ...options });
    return id;
}
async function newPage(options = {}) {
    const id = register(options);
    const page = await browser.newPage({ viewport: { width: 1000, height: 760 } });
    // Defense in depth for private samples: allow only this isolated loopback server.
    await page.route('**/*', async route => {
        const url = route.request().url();
        if (url.startsWith('blob:') || url.startsWith('data:') || new URL(url).origin === server.origin) await route.continue();
        else { report.diagnostics.blockedExternalRequests++; await route.abort(); }
    });
    page.on('console', message => { if (message.type() === 'error') report.diagnostics.consoleErrors++; });
    page.on('pageerror', error => {
        report.diagnostics.pageErrors++;
        report.diagnostics.pageErrorDetails.push({ check: activeCheck, type: error.name,
            message: /^Cannot read properties of (undefined|null) \(reading '[a-zA-Z0-9_]+'\)$/.test(error.message) ? error.message : 'RuntimeError',
            source: error.stack?.match(/reader\/(?:foliate\/)?[a-z-]+\.js:\d+:\d+/g) ?? [] });
    });
    page.setDefaultTimeout(8000);
    await page.goto(`${server.origin}/test.html#${id}`);
    await page.waitForFunction(() => window.host);
    return page;
}
async function closeReader(page) {
    await page.evaluate(() => host.close());
    await page.waitForFunction(() => liveBlobs.size === 0);
    ensure(await page.locator('foliate-view').count() === 0, 'ClosedViewStillPresent');
    await page.waitForFunction(() => activeResizeTargets === 0);
}
async function withPage(action, options) {
    const page = await newPage(options);
    try { return await action(page); }
    finally { await page.close(); }
}
async function command(page, type, payload = {}) {
    const sent = await page.evaluate(({ type, payload }) => host.command(type, payload), { type, payload });
    ensure(sent.accepted, 'CommandRejected');
    await page.waitForFunction(id => messages.some(x => x.requestId === id
        && ['commandCompleted', 'readerError'].includes(x.type)), sent.requestId, { timeout: 25000 });
    const error = await page.evaluate(id => messages.find(x => x.requestId === id && x.type === 'readerError')?.payload.code, sent.requestId);
    if (error) throw new Error(error);
}
async function open(page, location = null) {
    await command(page, 'openBook', { settings: {}, location });
    const info = await page.evaluate(() => {
        const { title, location, ...rest } = messages.find(x => x.type === 'opened').payload;
        return rest;
    });
    return info;
}
const position = page => page.evaluate(() => messages.filter(x => x.type === 'locationChanged').at(-1)?.payload);
const chapter = page => page.frames().find(x => x.url().startsWith('blob:'));
async function documentStats(page) {
    // Execute callbacks in the trusted host realm. Book frames intentionally have
    // no script permission, so timers/event callbacks created in that realm may
    // never run even when DevTools can synchronously inspect the document.
    return page.evaluate(async () => {
        const document = window.currentBookDocument?.deref();
        if (!document) throw new Error('EmptyDocument');
        const bounded = async promise => {
            let timer;
            try { return await Promise.race([promise, new Promise((_, reject) => {
                timer = setTimeout(() => reject(new Error('ResourceTimedOut')), 5000);
            })]); } finally { clearTimeout(timer); }
        };
        await bounded(document.fonts.ready);
        await bounded(Promise.all([...document.images].map(image => image.complete ? null
            : new Promise(resolve => { image.addEventListener('load', resolve, { once: true }); image.addEventListener('error', resolve, { once: true }); }))));
        const svgImages = [...document.querySelectorAll('svg image')];
        const svgResults = await Promise.all(svgImages.map(element => new Promise(resolve => {
            const source = element.href.baseVal;
            if (!/^(blob:|data:)/.test(source)) { resolve(false); return; }
            const image = new Image(), timer = setTimeout(() => resolve(false), 5000);
            image.onload = () => { clearTimeout(timer); resolve(image.naturalWidth > 0); };
            image.onerror = () => { clearTimeout(timer); resolve(false); };
            image.src = source;
        })));
        const text = document.body?.innerText ?? '';
        const paragraph = [...document.querySelectorAll('p')].find(x => x.textContent.trim().length > 20);
        const style = paragraph ? getComputedStyle(paragraph) : null;
        return { characters: text.length, chineseCharacters: (text.match(/[\u3400-\u9fff]/g) ?? []).length,
            images: document.images.length,
            svgImages: svgImages.length,
            vectors: document.querySelectorAll('svg path,svg rect,svg circle,svg polygon').length,
            brokenImages: [...document.images].filter(image => !image.complete || !image.naturalWidth).length + svgResults.filter(loaded => !loaded).length,
            loadedFonts: [...document.fonts].filter(font => font.status === 'loaded').length,
            failedFonts: [...document.fonts].filter(font => font.status === 'error').length,
            fontSize: style ? parseFloat(style.fontSize) : null, lineHeight: style ? parseFloat(style.lineHeight) : null,
            color: style?.color, background: getComputedStyle(document.body).backgroundColor,
            writingMode: style?.writingMode,
        };
    });
}
async function sampleProcesses() {
    if (process.platform !== 'win32') return null;
    const session = await browser.newBrowserCDPSession();
    const { processInfo } = await session.send('SystemInfo.getProcessInfo');
    await session.detach();
    const ids = processInfo.map(item => item.id).filter(Number.isSafeInteger);
    const { stdout } = await promisify(execFile)('powershell.exe', ['-NoProfile', '-Command',
        `Get-Process -Id ${ids.join(',')} -ErrorAction SilentlyContinue | Select-Object Id,WorkingSet64,PrivateMemorySize64,HandleCount | ConvertTo-Json -Compress`], { windowsHide: true });
    const values = JSON.parse(stdout);
    const rows = Array.isArray(values) ? values : [values];
    const total = rows.reduce((sum, item) => ({
        workingBytes: sum.workingBytes + item.WorkingSet64, privateBytes: sum.privateBytes + item.PrivateMemorySize64,
        handles: sum.handles + item.HandleCount, processes: sum.processes + 1,
    }), { workingBytes: 0, privateBytes: 0, handles: 0, processes: 0 });
    total.privateBytesByType = {};
    for (const row of rows) {
        const type = processInfo.find(info => info.id === row.Id)?.type ?? 'other';
        total.privateBytesByType[type] = (total.privateBytesByType[type] ?? 0) + row.PrivateMemorySize64;
    }
    return total;
}

try {
    browser = await chromium.launch({ channel: process.env.READER_BROWSER_CHANNEL ?? 'msedge', headless: true });
    report.environment.browser = browser.version();
    await test('archive and all spine documents', () => withPage(async page => {
        const data = await page.evaluate(async () => {
            const { fetchBook, openEpub } = await import('/reader/epub-loader.js');
            const { mapToc } = await import('/reader/protocol.js');
            const CFI = await import('/reader/foliate/epubcfi.js');
            const signal = new AbortController().signal;
            const loaded = await openEpub(await fetchBook(`/book/${location.hash.slice(1)}.epub`, signal), signal);
            try {
                const { book } = loaded;
                const { nodes, targets } = mapToc(book.toc);
                const docs = [], sectionLocations = [];
                for (const section of book.sections) {
                    const doc = await section.createDocument();
                    if (!doc.documentElement || doc.querySelector('parsererror')) throw new Error('InvalidBook');
                    docs.push({ characters: doc.documentElement.textContent.length, elements: doc.querySelectorAll('*').length });
                    const range = doc.createRange();
                    range.selectNodeContents(doc.body ?? doc.documentElement); range.collapse(true);
                    sectionLocations.push({ cfi: CFI.joinIndir(section.cfi, CFI.fromRange(range)) });
                }
                const manifest = book.resources.manifest;
                return { summary: { ...loaded.stats, epubVersion: book.resources.opf.documentElement.getAttribute('version'),
                    sections: book.sections.length, tocNodes: nodes.length,
                    layout: book.rendition?.layout ?? 'reflowable', direction: book.dir ?? 'ltr',
                    imageResources: manifest.filter(x => x.mediaType?.startsWith('image/')).length,
                    fontResources: manifest.filter(x => /font|woff|opentype/.test(x.mediaType ?? '') || /\.(?:otf|ttf|woff2?)$/i.test(x.href)).length,
                    cssResources: manifest.filter(x => x.mediaType === 'text/css').length,
                    mediaResources: manifest.filter(x => /^(audio|video)\//.test(x.mediaType ?? '')).length,
                    encryption: !!await book.loadText('META-INF/encryption.xml'),
                    maxChapterCharacters: Math.max(...docs.map(x => x.characters)),
                    maxChapterElements: Math.max(...docs.map(x => x.elements)) },
                    sectionLocations, navigation: nodes.map(node => {
                        const target = targets.get(node.id);
                        const resolved = target ? book.resolveHref(target) : null;
                        return { id: node.id, index: resolved?.index ?? null, hasFragment: target?.includes('#') ?? false };
                    }) };
            } finally { await loaded.close(); }
        });
        summary = data.summary; navigation = data.navigation; sectionLocations = data.sectionLocations;
        report.sample.structure = summary;
        ensure(navigation.every(x => x.index !== null && x.index >= 0), 'TocUnresolved');
        return summary;
    }));
    await test('initial body and cover resources', () => withPage(async page => {
        const info = await open(page);
        const stats = await documentStats(page);
        ensure(stats.characters > 0 || stats.images > 0, 'EmptyDocument');
        ensure(stats.brokenImages === 0 && stats.failedFonts === 0, 'ResourceFailed');
        await page.screenshot({ path: fileURLToPath(new URL('cover.png', output)) });
        await closeReader(page);
        return { ...info, document: stats };
    }));
    await test('every table of contents target renders and releases', () => withPage(async page => {
        ensure(navigation?.length, 'NoToc');
        await open(page);
        let images = 0, fonts = 0, targets = 0;
        for (const node of navigation) {
            await command(page, 'navigateToToc', { id: node.id });
            const stats = await documentStats(page);
            ensure(stats.characters > 0 || stats.images > 0, 'EmptyDocument');
            ensure(stats.brokenImages === 0 && stats.failedFonts === 0, 'ResourceFailed');
            const actualIndex = await page.evaluate(async () => {
                const { fake, parse } = await import('/reader/foliate/epubcfi.js');
                const parts = parse(messages.filter(x => x.type === 'locationChanged').at(-1).payload.cfi);
                return fake.toIndex((parts.parent ?? parts).shift());
            });
            ensure(actualIndex === node.index, 'TocWrongChapter');
            targets++; images += stats.images; fonts = Math.max(fonts, stats.loadedFonts);
        }
        await closeReader(page);
        return { targets, imageOccurrences: images, loadedFonts: fonts };
    }));
    await test('pagination and keyboard', () => withPage(async page => {
        await open(page);
        await command(page, 'restoreLocation', { fraction: 0.45 });
        const before = await position(page);
        for (let i = 0; i < 5; i++) await command(page, 'turn', { direction: 'next' });
        const after = await position(page);
        ensure(after.cfi !== before.cfi && after.fraction > before.fraction, 'PositionDidNotAdvance');
        const frame = chapter(page);
        await frame.locator('body').click({ position: { x: 10, y: 30 } });
        await page.keyboard.press('PageDown');
        await page.waitForFunction(before => messages.filter(x => x.type === 'locationChanged').at(-1)?.payload.cfi !== before, after.cfi);
        await closeReader(page);
        return { forwardPages: 6 };
    }));
    await test('every spine section renders with no broken images', () => withPage(async page => {
        ensure(sectionLocations?.length, 'NoSections');
        await open(page);
        let imageOccurrences = 0;
        for (const [index, target] of sectionLocations.entries()) {
            await command(page, 'restoreLocation', target);
            ensure(await page.evaluate(() => messages.filter(x => x.type === 'restored').at(-1).payload.restoredBy) === 'cfi', 'RestoreFallback');
            const stats = await documentStats(page);
            if (!stats.characters && !stats.images && !stats.svgImages && !stats.vectors) {
                report.diagnostics.emptySection = { index, stats };
                await page.screenshot({ path: fileURLToPath(new URL(`section-${index}-empty.png`, output)) });
            }
            ensure(stats.characters > 0 || stats.images > 0 || stats.svgImages > 0 || stats.vectors > 0, 'EmptyDocument');
            ensure(stats.brokenImages === 0 && stats.failedFonts === 0, 'ResourceFailed');
            imageOccurrences += stats.images + stats.svgImages;
            if ((index + 1) % 10 === 0) console.log(`  local-book section ${index + 1}/${sectionLocations.length}`);
        }
        await closeReader(page);
        return { sections: sectionLocations.length, imageOccurrences };
    }));
    await test('font size line height theme scroll and narrow layout', () => withPage(async page => {
        await open(page);
        await command(page, 'restoreLocation', { fraction: 0.5 });
        const original = await position(page), before = await documentStats(page);
        await page.screenshot({ path: fileURLToPath(new URL('text-light.png', output)) });
        await command(page, 'applySettings', { fontSize: 28, lineHeight: 1.9, theme: 'dark', width: 560, flow: 'scrolled' });
        await page.setViewportSize({ width: 540, height: 650 });
        await command(page, 'restoreLocation', original);
        const after = await documentStats(page);
        ensure(after.fontSize > before.fontSize, 'FontSizeIgnored');
        ensure(Math.abs(after.lineHeight / after.fontSize - 1.9) < 0.05, 'LayoutLineHeightIgnored');
        ensure(after.color === 'rgb(232, 234, 237)', 'LayoutThemeIgnored');
        ensure(Math.abs((await position(page)).fraction - original.fraction) < 0.02, 'PositionDrift');
        await page.screenshot({ path: fileURLToPath(new URL('text-dark-narrow.png', output)) });
        await command(page, 'applySettings', { fontSize: 18, lineHeight: 1.5, theme: 'sepia', flow: 'paginated' });
        ensure((await documentStats(page)).fontSize < after.fontSize, 'FontSizeIgnored');
        await closeReader(page);
        return { before, after };
    }));
    await test('CFI persists across an entirely new browser process', async () => {
        await withPage(async page => {
            await open(page); await command(page, 'restoreLocation', { fraction: 0.63 });
            saved = await position(page); await closeReader(page);
        });
        await browser.close();
        browser = await chromium.launch({ channel: process.env.READER_BROWSER_CHANNEL ?? 'msedge', headless: true });
        return withPage(async page => {
            const info = await open(page, saved), restored = await position(page);
            ensure(info.restoredBy === 'cfi', 'RestoreFallback');
            ensure(Math.abs(restored.fraction - saved.fraction) < 0.01, 'PositionDrift');
            const stats = await documentStats(page);
            ensure(stats.chineseCharacters > 0, 'TextMissing');
            await closeReader(page);
            return { restoredBy: info.restoredBy, fractionDelta: Math.abs(restored.fraction - saved.fraction) };
        });
    });
    await test('invalid CFI falls back to fraction and beginning', () => withPage(async page => {
        await open(page);
        await command(page, 'restoreLocation', { cfi: 'epubcfi(/999999/999999!)', fraction: 0.4 });
        ensure(await page.evaluate(() => messages.filter(x => x.type === 'restored').at(-1).payload.restoredBy) === 'fraction', 'RestoreFallback');
        await command(page, 'restoreLocation', { cfi: 'epubcfi(invalid)' });
        ensure(await page.evaluate(() => messages.filter(x => x.type === 'restored').at(-1).payload.restoredBy) === 'start', 'RestoreFallback');
        await closeReader(page);
    }));
    await test('cancel during slow download', () => withPage(async page => {
        await page.evaluate(() => host.open());
        await page.waitForTimeout(80);
        const start = Date.now(); await closeReader(page);
        await page.waitForTimeout(250);
        ensure(await page.evaluate(() => !messages.some(x => ['opened', 'locationChanged', 'readerError'].includes(x.type))), 'UnexpectedLateResult');
        return { closeAndObservationMs: Date.now() - start };
    }, { slow: true, chunked: true }));
    if (cycles) await test(`${cycles} same-page open close cycles`, () => withPage(async page => {
        const cdp = await page.context().newCDPSession(page), samples = [];
        const snapshot = async label => ({ label, dom: await cdp.send('Memory.getDOMCounters'), processes: await sampleProcesses() });
        samples.push(await snapshot('before'));
        for (let i = 1; i <= cycles; i++) {
            const id = register();
            await page.evaluate(id => host.resetSession(id), id);
            await open(page);
            await command(page, 'restoreLocation', { fraction: 0.5 });
            await closeReader(page);
            if (cycles <= 10 || i % 10 === 0 || i === cycles) { samples.push(await snapshot(`closed-${i}`)); console.log(`  local-book cycle ${i}/${cycles}`); }
        }
        await page.waitForTimeout(2000);
        samples.push(await snapshot('idle-2s'));
        ensure(await page.evaluate(() => liveBlobs.size === 0), 'ResourceLeak');
        return samples;
    }));
    await test('source remains unchanged', async () => { ensure(await hash(bookPath) === sourceHash, 'SampleChanged'); });
    await test('no uncaught browser errors or external requests', async () => {
        ensure(report.diagnostics.pageErrors === 0, 'UnexpectedBrowserError');
        ensure(report.diagnostics.blockedExternalRequests === 0, 'ExternalRequestAttempt');
    });
} finally {
    await browser?.close(); await server.close();
    await writeFile(new URL('results.json', output), JSON.stringify(report, null, 2));
}
console.log(`${results.filter(x => x.passed).length}/${results.length} local-book checks passed`);
if (results.some(x => !x.passed)) process.exitCode = 1;
