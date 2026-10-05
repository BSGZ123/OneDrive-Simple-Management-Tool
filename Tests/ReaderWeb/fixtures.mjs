// All prose, artwork, navigation and attack cases are generated here for this project.
// No personal books, network downloads or redistribution of third-party books.
import { ZipWriter, Uint8ArrayWriter, Uint8ArrayReader, configure } from '@zip.js/zip.js';
import { mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { deflateSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
configure({ useWebWorkers: false });
const output = new URL('artifacts/fixtures/', import.meta.url);
const encode = text => new TextEncoder().encode(text);
const escape = text => text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('"', '&quot;');
const container = '<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="EPUB/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>';
const paragraph = 'This is reproducible reading content. 中文排版与位置恢复验证。日本語の縦書き。 ';
const chapter = (n, options) => {
    const writing = options.vertical ? 'writing-mode:vertical-rl;' : '';
    let body = `<h1 id="heading">Chapter ${n + 1} · 阅读测试</h1>`;
    if (options.fixed) body += '<svg xmlns="http://www.w3.org/2000/svg" width="500" height="320"><rect width="500" height="320" fill="#94c2d4"/><circle cx="250" cy="160" r="80" fill="#f6bb65"/></svg>';
    body += `<a id="internal" href="ch${(n + 1) % (options.count ?? 3)}.xhtml#heading">Next chapter</a>
        <a id="external" href="https://example.com/reader-test">External link</a>
        <input id="input" value="Typing must not turn pages"/>`;
    if (options.image) body += '<img src="picture.png" alt="Generated color blocks"/>';
    for (let i = 0; i < (options.paragraphs ?? (options.fixed ? 1 : 40)); i++)
        body += `<p id="p${i}">${i}: ${options.rtl ? 'هذا نص عربي لاختبار اتجاه القراءة. '.repeat(4) : paragraph.repeat(4)}</p>`;
    if (options.attack) {
        const code = "parent.executions.push('executed');parent.host.command('close');parent.postMessage('forged','*');";
        body += `<script>${code}</script><script src="evil.js"></script>
            <script src="${options.probe}/script"></script>
            <img src="${options.probe}/image" onerror="${code}"/>
            <link rel="stylesheet" href="${options.probe}/style"/>
            <style>@import url('${options.probe}/import');p{background-image:url('${options.probe}/css-image')}</style>
            <iframe src="${options.probe}/frame"></iframe>
            <iframe srcdoc="${escape(`<script>${code}</script>`)}"></iframe>
            <a id="javascript" href="javascript:${escape(code)}">Bad link</a>
            <form action="${options.probe}/form"><button type="submit">Bad form</button></form>`;
    }
    return `<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml" lang="${options.rtl ? 'ar' : options.vertical ? 'ja' : 'zh'}" dir="${options.rtl ? 'rtl' : 'ltr'}"><head><title>Chapter ${n}</title>
        <meta name="viewport" content="width=600,height=800"/><style>body{${writing}}img{max-width:100%}</style></head><body>${body}</body></html>`;
};
const crc32 = data => {
    let crc = 0xffffffff;
    for (const b of data) { crc ^= b; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); }
    return (crc ^ 0xffffffff) >>> 0;
};
function png() {
    const chunk = (type, data) => {
        const result = Buffer.alloc(data.length + 12);
        result.writeUInt32BE(data.length); result.write(type, 4); data.copy(result, 8);
        result.writeUInt32BE(crc32(result.subarray(4, -4)), result.length - 4); return result;
    };
    const header = Buffer.alloc(13); header.writeUInt32BE(512); header.writeUInt32BE(512, 4); header[8] = 8; header[9] = 2;
    const pixels = Buffer.alloc(512 * (512 * 3 + 1));
    for (let y = 0; y < 512; y++) for (let x = 0; x < 512; x++) {
        const i = y * 1537 + 1 + x * 3; pixels[i] = x % 256; pixels[i + 1] = y % 256; pixels[i + 2] = 160;
    }
    return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]), chunk('IHDR', header), chunk('IDAT', deflateSync(pixels)), chunk('IEND', Buffer.alloc(0))]);
}
async function book(options = {}) {
    const files = new Map([['mimetype', encode('application/epub+zip')], ['META-INF/container.xml', encode(container)]]);
    const count = options.count ?? 3;
    const chapters = Array.from({ length: count }, (_, i) => i);
    const nav = options.emptyToc ? '' : chapters.map(i => `<li><a href="ch${i}.xhtml${options.tocWithoutFragments ? '' : '#heading'}">Chapter ${i + 1}</a>${i === 0 ? '<ol><li><a href="ch0.xhtml#p1">Nested paragraph</a></li></ol>' : ''}</li>`).join('');
    const toc = `<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol>${nav}</ol></nav></body></html>`;
    files.set('EPUB/nav.xhtml', encode(toc));
    files.set('EPUB/toc.ncx', encode(`<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head/><docTitle><text>EPUB 2 fixture</text></docTitle><navMap>${chapters.map(i => `<navPoint id="n${i}" playOrder="${i + 1}"><navLabel><text>Chapter ${i + 1}</text></navLabel><content src="ch${i}.xhtml#heading"/></navPoint>`).join('')}</navMap></ncx>`));
    files.set('EPUB/picture.png', png());
    files.set('EPUB/evil.js', encode("parent.executions.push('book-script');parent.host.command('close');"));
    const manifest = chapters.map(i => `<item id="ch${i}" href="ch${i}.xhtml" media-type="application/xhtml+xml"/>`).join('');
    files.set('EPUB/package.opf', encode(`<package xmlns="http://www.idpf.org/2007/opf" version="${options.epub2 ? '2.0' : '3.0'}" unique-identifier="id" prefix="rendition: http://www.idpf.org/vocab/rendition/#"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:reader:fixture</dc:identifier><dc:title>Generated reader fixture</dc:title><dc:language>${options.rtl ? 'ar' : options.vertical ? 'ja' : 'zh'}</dc:language>${options.fixed ? '<meta property="rendition:layout">pre-paginated</meta><meta property="rendition:spread">none</meta>' : ''}</metadata><manifest>${manifest}<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" ${options.epub2 ? '' : 'properties="nav"'}/><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/><item id="image" href="picture.png" media-type="image/png"/><item id="evil" href="evil.js" media-type="text/javascript"/></manifest><spine toc="ncx" page-progression-direction="${options.rtl ? 'rtl' : 'ltr'}">${chapters.map(i => `<itemref idref="ch${i}"/>`).join('')}</spine></package>`));
    for (const i of chapters) files.set(`EPUB/ch${i}.xhtml`, encode(chapter(i, options)));
    if (options.bomb) files.set('EPUB/huge.txt', new Uint8Array(33 * 1024 * 1024));
    if (options.drm) files.set('META-INF/encryption.xml', encode('<encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><EncryptedData xmlns="http://www.w3.org/2001/04/xmlenc#"><EncryptionMethod Algorithm="urn:unsupported-drm"/><CipherData><CipherReference URI="EPUB/ch0.xhtml"/></CipherData></EncryptedData></encryption>'));
    if (options.traversal) files.set('../outside.txt', encode('must be rejected'));
    if (options.padding) for (let i = 0; i < 6; i++) files.set(`EPUB/padding${i}.bin`, new Uint8Array(16_000_000));
    if (options.longChapter) files.set('EPUB/ch0.xhtml', encode(chapter(0, { ...options, paragraphs: 1000 })));
    const writer = new ZipWriter(new Uint8ArrayWriter(), { bufferedWrite: true });
    for (const [name, data] of files) await writer.add(name, new Uint8ArrayReader(data), {
        level: name === 'mimetype' || name.includes('padding') ? 0 : 6,
        lastModDate: new Date('2026-01-01T00:00:00Z'), extendedTimestamp: false,
    });
    return { bytes: await writer.close(), expanded: [...files.values()].reduce((sum, x) => sum + x.length, 0), entries: files.size };
}
export async function createFixtures(probe = 'http://127.0.0.1:1', performance = false) {
    await mkdir(output, { recursive: true });
    const matrix = { text: {}, epub2: { epub2: true }, images: { image: true }, fixed: { fixed: true },
        rtl: { rtl: true }, vertical: { vertical: true }, empty: { emptyToc: true },
        attack: { attack: true, probe }, bomb: { bomb: true }, traversal: { traversal: true }, drm: { drm: true },
        chapters: { count: 80, paragraphs: 2 }, long: { longChapter: true }, noFragment: { tocWithoutFragments: true } };
    if (performance) matrix.nearLimit = { padding: true, image: true, count: 80, paragraphs: 2 };
    const manifest = {};
    for (const [id, options] of Object.entries(matrix)) {
        const { bytes, expanded, entries } = await book(options);
        const path = fileURLToPath(new URL(`${id}.epub`, output));
        await writeFile(path, bytes);
        manifest[id] = { path, compressed: bytes.length, expanded, entries,
            sha256: createHash('sha256').update(bytes).digest('hex') };
    }
    // Forge a central-directory size: the output writer must reject actual bytes,
    // rather than treating a small declared size as permission to inflate unboundedly.
    const forged = Buffer.from((await book()).bytes);
    for (let i = 0; i + 46 < forged.length; i++) {
        if (forged.readUInt32LE(i) !== 0x02014b50) continue;
        const length = forged.readUInt16LE(i + 28);
        if (forged.subarray(i + 46, i + 46 + length).toString() === 'EPUB/ch0.xhtml') {
            forged.writeUInt32LE(1, i + 24); break;
        }
    }
    const forgedPath = fileURLToPath(new URL('forged.epub', output));
    await writeFile(forgedPath, forged);
    manifest.forged = { path: forgedPath, compressed: forged.length,
        sha256: createHash('sha256').update(forged).digest('hex') };
    const invalid = fileURLToPath(new URL('invalid.epub', output));
    await writeFile(invalid, 'not a ZIP');
    manifest.invalid = { path: invalid };
    await writeFile(new URL('manifest.json', output), JSON.stringify(manifest, null, 2));
    return manifest;
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
    const manifest = await createFixtures(undefined, process.argv.includes('--performance'));
    console.log(Object.keys(manifest).join(', '));
}
