import { configure, ZipReader, BlobReader } from './foliate/vendor/zip.js';
import { EPUB } from './foliate/epub.js';
import { LIMITS } from './protocol.js';

configure({ useWebWorkers: false, useCompressionStream: true, chunkSize: 64 * 1024 });
const check = (condition, code = 'BookLimit') => { if (!condition) throw new Error(code); };
const validPath = name => typeof name === 'string' && name.length <= 1024
    && !/[\\\x00-\x1f?#:]/.test(name) && !name.startsWith('/')
    && !name.split('/').some(x => x === '..' || x === '.');

export async function fetchBook(url, signal) {
    const response = await fetch(url, { signal, credentials: 'omit', redirect: 'error', cache: 'no-store' });
    check(response.ok && response.headers.get('content-type')?.split(';')[0] === 'application/epub+zip', 'LoadFailed');
    const declared = response.headers.get('content-length');
    if (declared !== null) check(Number.isSafeInteger(Number(declared)) && Number(declared) >= 0
        && Number(declared) <= LIMITS.bookBytes);
    const reader = response.body.getReader();
    const chunks = [];
    let size = 0;
    try {
        while (true) {
            signal.throwIfAborted();
            const { done, value } = await reader.read();
            if (done) break;
            size += value.byteLength;
            check(size <= LIMITS.bookBytes);
            chunks.push(value);
        }
        check(size > 0 && (declared === null || size === Number(declared)), 'InvalidBook');
        return new Blob(chunks, { type: 'application/epub+zip' });
    } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
}

// Bound the actual decompressor output as well as ZIP declarations. No extraction to disk.
export async function openEpub(blob, signal) {
    check(blob.size > 0 && blob.size <= LIMITS.bookBytes);
    const zip = new ZipReader(new BlobReader(blob), { checkSignature: true });
    const entries = new Map();
    const expandedSizes = new Map();
    let declaredTotal = 0, expandedTotal = 0, book, failure;
    const guard = operation => async (...args) => {
        try { return await operation(...args); }
        catch (error) {
            const controlled = signal.aborted || ['BookLimit', 'InvalidBook', 'UnsupportedEncryption'].includes(error.message)
                ? error : new Error('InvalidBook');
            failure ??= controlled;
            throw controlled;
        }
    };
    const close = async () => { book?.destroy(); await zip.close(); };
    try {
        for await (const entry of zip.getEntriesGenerator()) {
            signal.throwIfAborted();
            check(entries.size < LIMITS.entries && validPath(entry.filename) && !entries.has(entry.filename), 'InvalidBook');
            check(!entry.encrypted, 'UnsupportedEncryption');
            check(Number.isSafeInteger(entry.uncompressedSize) && entry.uncompressedSize >= 0
                && entry.uncompressedSize <= LIMITS.entryBytes);
            declaredTotal += entry.uncompressedSize;
            check(declaredTotal <= LIMITS.expandedBytes);
            entries.set(entry.filename, entry);
        }
        const read = guard(async (name, limit, type) => {
            signal.throwIfAborted();
            const entry = entries.get(name);
            if (!entry || entry.directory) return null;
            check(entry.uncompressedSize <= limit);
            const chunks = [];
            let size = 0;
            await entry.getData(new WritableStream({ write(chunk) {
                signal.throwIfAborted();
                size += chunk.byteLength;
                const previous = expandedSizes.get(name) ?? 0;
                expandedTotal += Math.max(0, size - previous);
                expandedSizes.set(name, Math.max(previous, size));
                check(size <= limit && size <= entry.uncompressedSize && expandedTotal <= LIMITS.expandedBytes);
                chunks.push(chunk);
            } }), { signal, checkSignature: true });
            check(size === entry.uncompressedSize, 'InvalidBook');
            return new Blob(chunks, { type });
        });
        const loadText = guard(async name => {
            const value = await read(name, LIMITS.textBytes, 'text/plain');
            if (!value) return null;
            const text = await value.text();
            // Reject entity declarations, excessive DOM size/depth before upstream recursive navigation parsing.
            if (/\.(?:xml|opf|ncx|xhtml|html|svg)$/i.test(name)) {
                check(!/<!ENTITY|<!DOCTYPE[^>]*\[/i.test(text), 'InvalidBook');
                const doc = new DOMParser().parseFromString(text, /\.html$/i.test(name) ? 'text/html' : 'application/xml');
                check(!doc.querySelector('parsererror'), 'InvalidBook');
                const walker = doc.createTreeWalker(doc, NodeFilter.SHOW_ELEMENT);
                let count = 0, node;
                while ((node = walker.nextNode())) {
                    check(++count <= 50000);
                    let depth = 0, parent = node;
                    while ((parent = parent.parentElement)) check(++depth <= 64);
                }
            }
            return text;
        });
        check(await loadText('mimetype') === 'application/epub+zip', 'InvalidBook');
        const encryption = await loadText('META-INF/encryption.xml');
        if (encryption) {
            const doc = new DOMParser().parseFromString(encryption, 'application/xml');
            for (const method of doc.getElementsByTagNameNS('http://www.w3.org/2001/04/xmlenc#', 'EncryptionMethod'))
                check(['http://www.idpf.org/2008/embedding', 'http://ns.adobe.com/pdf/enc#RC']
                    .includes(method.getAttribute('Algorithm')), 'UnsupportedEncryption');
        }
        book = await new EPUB({ loadText, loadBlob: (name, type) => read(name, LIMITS.entryBytes, type),
            getSize: name => entries.get(name)?.uncompressedSize ?? 0 }).init();
        signal.throwIfAborted();
        check(book.sections.length > 0 && book.sections.length <= LIMITS.tocNodes, 'InvalidBook');
        book.transformTarget.addEventListener('load', event => {
            if (event.detail.isScript) event.detail.allow = false;
        });
        return { book, close, throwIfFailed: () => { if (failure) throw failure; },
            stats: { entries: entries.size, declaredTotal, compressedBytes: blob.size } };
    } catch (error) { await close(); throw error; }
}
