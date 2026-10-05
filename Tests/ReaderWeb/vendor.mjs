// Recreate the reviewed subset from an already downloaded, pinned upstream tree.
import { readFile, writeFile, mkdir, copyFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const commit = '78914aef4466eb960965702401634c2cb348e9b1';
const source = path.join(here, 'artifacts/upstream', `foliate-js-${commit}`);
const target = path.resolve(here, '../../Assets/Reader/foliate');
await mkdir(path.join(target, 'vendor'), { recursive: true });
const names = ['view.js', 'epub.js', 'epubcfi.js', 'progress.js', 'overlayer.js',
    'text-walker.js', 'paginator.js', 'fixed-layout.js', 'search.js', 'tts.js', 'LICENSE', 'vendor/zip.js'];
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const files = [];
for (const name of names) {
    const original = await readFile(path.join(source, name));
    let content = original.toString('utf8');
    if (name === 'view.js') {
        const start = content.indexOf('const isZip =');
        const end = content.indexOf('class CursorAutohider');
        if (start < 0 || end <= start) throw new Error('Upstream view changed');
        content = content.slice(0, start) + content.slice(end);
        content = content.replace('book = await makeBook(book)',
            "throw new TypeError('Use the bounded EPUB loader')");
        // close() can happen during TOC loading or the renderer's dynamic import.
        // Do not create a renderer/observer in a detached, already closed view.
        content = content.replace('export class View extends HTMLElement {',
            'export class View extends HTMLElement {\n    #closed = false');
        content = content.replace('    async open(book) {',
            "    async open(book) {\n        if (this.#closed) throw new Error('ReaderClosed')");
        content = content.replace('                toc: book.toc ?? [], ids, splitHref, getFragment })',
            '                toc: book.toc ?? [], ids, splitHref, getFragment })\n            if (this.#closed) return');
        content = content.replace('                toc: book.pageList ?? [], ids, splitHref, getFragment })',
            '                toc: book.pageList ?? [], ids, splitHref, getFragment })\n            if (this.#closed) return');
        for (const module of ['fixed-layout', 'paginator'])
            content = content.replace(`            await import('./${module}.js')`,
                `            await import('./${module}.js')\n            if (this.#closed) return`);
        content = content.replace('    close() {', '    close() {\n        this.#closed = true');
    }
    if (name === 'paginator.js' || name === 'fixed-layout.js') {
        content = content.replace(/        \/\/ `allow-scripts`[^\n]*\n        \/\/ https:[^\n]*\n/g,
            '        // Chromium/WebView2: host listeners work without book script permission.\n');
        content = content.replaceAll("'allow-same-origin allow-scripts'", "'allow-same-origin'");
    }
    if (name === 'epub.js') {
        content = content.replace('class Loader {', 'class Loader {\n    #destroyed = false');
        content = content.replace('        const url = URL.createObjectURL(new Blob([newData], { type: newType }))',
            "        if (this.#destroyed) throw new Error('ReaderClosed')\n        const url = URL.createObjectURL(new Blob([newData], { type: newType }))");
        content = content.replace('        for (const url of this.#cache.values()) URL.revokeObjectURL(url)',
            '        this.#destroyed = true\n        for (const url of this.#cache.values()) URL.revokeObjectURL(url)\n        this.#cache.clear()\n        this.#children.clear()\n        this.#refCount.clear()');
    }
    if (name === 'paginator.js') {
        content = content.replace('class View {', 'class View {\n    #destroyed = false');
        content = content.replace('                const doc = this.document\n                afterLoad?.(doc)',
            '                const doc = this.document\n                if (this.#destroyed || !doc?.body) { resolve(); return }\n                afterLoad?.(doc)');
        content = content.replace('        if (!layout) return', '        if (this.#destroyed || !layout || !this.document?.body) return');
        content = content.replace('    render() {\n        if (!this.#view) return',
            '    render() {\n        if (!this.#view?.document?.body) return');
        content = content.replace('    expand() {\n        const { documentElement } = this.document',
            '    expand() {\n        if (this.#destroyed || !this.document?.body) return\n        const { documentElement } = this.document');
        content = content.replace('        if (this.document) this.#observer.unobserve(this.document.body)',
            '        this.#destroyed = true\n        this.#observer.disconnect()');
        content = content.replace('        requestAnimationFrame(() =>\n            this.#background.style.background = getBackground(this.#view.document))',
            '        const view = this.#view\n        requestAnimationFrame(() => {\n            if (this.#view === view && view?.document?.body)\n                this.#background.style.background = getBackground(view.document)\n        })');
        content = content.replace('        this.#view?.document?.fonts?.ready?.then(() => this.#view.expand())',
            '        view?.document?.fonts?.ready?.then(() => {\n            if (this.#view === view) view.expand()\n        })');
        content = content.replaceAll('        this.#view.destroy()', '        this.#view?.destroy()');
        // The observed target is #container, not the custom element itself.
        content = content.replace('        this.#observer.unobserve(this)', '        this.#observer.disconnect()');
    }
    await writeFile(path.join(target, name), content);
    files.push({ path: `foliate/${name}`, upstreamSha256: sha256(original), sha256: sha256(content) });
}
const license = path.join(here, 'node_modules/@zip.js/zip.js/LICENSE');
await copyFile(license, path.join(target, 'vendor/zip-LICENSE'));
files.push({ path: 'foliate/vendor/zip-LICENSE', sha256: sha256(await readFile(license)) });
await writeFile(path.join(target, '../dependencies.json'), JSON.stringify({
    foliate: { repository: 'https://github.com/johnfactotum/foliate-js', commit, retrieved: '2026-10-05' },
    zip: { version: '2.8.22', source: 'https://registry.npmjs.org/@zip.js/zip.js/-/zip.js-2.8.22.tgz',
        bundle: 'foliate-js vendor/zip.js at the pinned commit; version from upstream package-lock.json',
        integrity: 'sha512-0KlzbVR6r8irIX2o3zvUlosBDef62VDl47oUfa1U/qgEs67h4/eGBrX/6HWa1RQbt+J6sAeVmtyFKbTHNdF8qQ==' },
    patches: ['view.js: remove general file/URL/directory detection; require an EPUB book object; prevent late renderer creation after close',
        'paginator.js, fixed-layout.js: remove allow-scripts from chapter iframe sandbox',
        'epub.js: reject late Blob creation after destroy; clear loader maps',
        'paginator.js: ignore detached/destroyed document layout and stale font/animation callbacks; disconnect the container resize observer on destroy'], files,
}, null, 2) + '\n');
