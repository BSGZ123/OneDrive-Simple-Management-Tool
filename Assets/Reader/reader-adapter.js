import './foliate/view.js';
import { fetchBook, openEpub } from './epub-loader.js';
import { VERSION, LIMITS, DEFAULT_SETTINGS, validId, parseCommand, locationDto, mapToc, tocChunks } from './protocol.js';

const errorCodes = new Set(['BookLimit', 'InvalidBook', 'UnsupportedEncryption', 'LoadFailed', 'TimedOut', 'InvalidTarget']);
const nextFrame = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));

export class ReaderAdapter {
    #root; #sessionId; #send; #abort = new AbortController(); #view; #loaded;
    #state = 'idle'; #chain = Promise.resolve(); #pending = 0; #seen = new Set();
    #settings = { ...DEFAULT_SETTINGS }; #toc = new Map(); #relocations = 0; #closing;
    constructor({ root, sessionId, send }) {
        if (!validId(sessionId)) throw new TypeError('Invalid session');
        this.#root = root;
        this.#sessionId = sessionId;
        this.#send = send;
    }
    #emit(type, payload, requestId = null) {
        if (this.#state === 'closed' && type !== 'commandCompleted') return;
        this.#send({ version: VERSION, sessionId: this.#sessionId, requestId, type, payload });
    }
    ready() { this.#emit('ready', {}); }
    receive(input) {
        const command = parseCommand(input, this.#sessionId);
        if (!command || this.#state === 'closed' || this.#state === 'failed'
            && command.type !== 'close' || this.#seen.has(command.requestId)
            || this.#pending >= 8 && command.type !== 'close') return false;
        this.#seen.add(command.requestId);
        if (this.#seen.size > 256) this.#seen.delete(this.#seen.values().next().value);
        if (command.type === 'close') {
            void this.close().then(() => this.#emit('commandCompleted', { command: 'close' }, command.requestId));
            return true;
        }
        this.#pending++;
        this.#chain = this.#chain.then(async () => {
            if (this.#abort.signal.aborted) return;
            try {
                await this.#deadline(() => this.#execute(command),
                    command.type === 'openBook' ? LIMITS.openMs : LIMITS.navigationMs);
                this.#abort.signal.throwIfAborted();
                this.#emit('commandCompleted', { command: command.type }, command.requestId);
            } catch (error) {
                if (this.#state === 'closed') return;
                this.#state = 'failed';
                this.#emit('readerError', { code: errorCodes.has(error.message) ? error.message : 'LoadFailed' }, command.requestId);
                this.#abort.abort();
                await this.#release();
            }
        }).finally(() => { this.#pending--; });
        return true;
    }
    async #deadline(operation, ms) {
        let timer;
        const pending = operation();
        try {
            return await Promise.race([pending, new Promise((_, reject) => {
                timer = setTimeout(() => reject(new Error('TimedOut')), ms);
            })]);
        } finally {
            clearTimeout(timer);
            // A timeout does not cancel a promise. Aborted loaders check their signal and
            // release again when their outstanding operation actually settles.
            void pending.finally(() => this.#abort.signal.aborted ? this.#release() : undefined).catch(() => {});
        }
    }
    async #execute({ type, payload, requestId }) {
        if (type === 'openBook') {
            if (this.#state !== 'idle') throw new Error('InvalidTarget');
            await this.#open(payload, requestId);
            return;
        }
        if (this.#state !== 'ready') throw new Error('InvalidTarget');
        switch (type) {
            case 'applySettings': {
                const location = locationDto(this.#view.lastLocation);
                this.#state = 'restoring';
                this.#applySettings(payload);
                await nextFrame();
                await this.#restore(location);
                this.#state = 'ready';
                this.#reportLocation();
                break;
            }
            case 'restoreLocation': {
                this.#state = 'restoring';
                const restoredBy = await this.#restore(payload);
                this.#state = 'ready';
                this.#emit('restored', { restoredBy, location: locationDto(this.#view.lastLocation) }, requestId);
                this.#reportLocation();
                break;
            }
            case 'navigateToToc': {
                const href = this.#toc.get(payload.id);
                if (!href || !await this.#navigate(href)) throw new Error('InvalidTarget');
                break;
            }
            case 'turn': {
                const methods = { next: 'next', prev: 'prev', left: 'goLeft', right: 'goRight' };
                await this.#view[methods[payload.direction]]();
                break;
            }
        }
    }
    async #open({ settings, location }, requestId) {
        this.#state = 'opening';
        const signal = this.#abort.signal;
        const started = performance.now();
        const blob = await fetchBook(new URL(`/book/${this.#sessionId}.epub`, window.location.href), signal);
        const loaded = await openEpub(blob, signal);
        if (signal.aborted) { await loaded.close(); signal.throwIfAborted(); }
        this.#loaded = loaded;
        const { nodes, targets } = mapToc(loaded.book.toc);
        mapToc(loaded.book.pageList);
        this.#toc = targets;
        const view = document.createElement('foliate-view');
        this.#view = view;
        this.#root.replaceChildren(view);
        view.addEventListener('load', event => this.#onDocument(event.detail.doc), { signal });
        view.addEventListener('relocate', () => {
            this.#relocations++;
            if (this.#state === 'ready') this.#reportLocation();
        }, { signal });
        view.addEventListener('external-link', event => {
            event.preventDefault();
            const href = event.detail.href_;
            try {
                const url = new URL(href);
                if (this.#state === 'ready' && ['http:', 'https:'].includes(url.protocol)
                    && !url.username && !url.password && url.href.length <= 2048)
                    this.#emit('externalLinkRequested', { url: url.href });
            } catch { /* Never execute arbitrary URL schemes. */ }
        }, { signal });
        view.addEventListener('keydown', event => this.#onKey(event), { signal });
        await view.open(loaded.book);
        signal.throwIfAborted();
        this.#applySettings(settings);
        this.#state = 'restoring';
        const restoredBy = await this.#restore(location);
        signal.throwIfAborted();
        this.#state = 'ready';
        for (const chunk of tocChunks(nodes)) this.#emit('tocChunk', chunk, requestId);
        this.#emit('opened', {
            title: String(loaded.book.metadata?.title ?? '').slice(0, 512),
            fixedLayout: view.isFixedLayout, direction: loaded.book.dir === 'rtl' ? 'rtl' : 'ltr',
            restoredBy, location: locationDto(view.lastLocation),
            elapsedMs: Math.round(performance.now() - started), ...loaded.stats,
        }, requestId);
        this.#reportLocation();
    }
    async #navigate(target) {
        const view = this.#view;
        const resolved = await view.resolveNavigation(target);
        this.#abort.signal.throwIfAborted();
        if (!resolved || !Number.isInteger(resolved.index)
            || resolved.index < 0 || resolved.index >= view.book.sections.length) return false;
        // Upstream goTo catches some errors. Validate the anchor against actual chapter
        // content before navigation; completion also requires a visible document + relocate.
        if (!view.isFixedLayout && typeof resolved.anchor === 'function') {
            const doc = await view.book.sections[resolved.index].createDocument();
            try {
                const anchor = resolved.anchor(doc);
                // A chapter URL without a fragment resolves to the numeric start
                // position 0. It is valid even though it is not a DOM anchor.
                if (typeof anchor === 'number') {
                    if (!Number.isFinite(anchor) || anchor < 0 || anchor > 1) return false;
                } else if (!anchor || !(anchor.nodeType || anchor.startContainer)) return false;
            } catch { return false; }
        }
        const before = this.#relocations;
        await view.goTo(target);
        this.#loaded?.throwIfFailed();
        await nextFrame();
        this.#abort.signal.throwIfAborted();
        const contents = view.renderer.getContents();
        if (view.isFixedLayout) {
            // FixedLayout.getContents has no index, and navigating to the current
            // spread intentionally emits no relocation. Verify its visible spread.
            return view.renderer.index === resolved.index
                && view.lastLocation?.section?.current === resolved.index
                && contents.some(content => content.doc?.documentElement)
                && !!locationDto(view.lastLocation).cfi;
        }
        return this.#relocations > before && contents.some(content => content.index === resolved.index
            && content.doc?.documentElement) && !!locationDto(view.lastLocation).cfi;
    }
    async #restore(location) {
        if (location?.cfi && await this.#navigate(location.cfi)) return 'cfi';
        if (Number.isFinite(location?.fraction) && await this.#navigate({ fraction: location.fraction })) return 'fraction';
        const index = this.#view.book.sections.findIndex(section => section.linear !== 'no');
        if (!await this.#navigate(Math.max(index, 0))) throw new Error('InvalidTarget');
        return 'start';
    }
    #applySettings(settings) {
        Object.assign(this.#settings, settings);
        const { fontSize, lineHeight, width, flow, theme, zoom } = this.#settings;
        document.documentElement.dataset.theme = theme;
        const renderer = this.#view.renderer;
        if (this.#view.isFixedLayout) { renderer.setAttribute('zoom', zoom); return; }
        renderer.setAttribute('flow', flow);
        renderer.setAttribute('max-inline-size', String(width));
        renderer.setAttribute('max-column-count', '1');
        const [background, foreground] = theme === 'dark' ? ['#202124', '#e8eaed']
            : theme === 'sepia' ? ['#f5ecd8', '#493d2a'] : ['#ffffff', '#222222'];
        renderer.setStyles(`html { font-size: ${fontSize}px !important; }
            body { font-size: 1rem !important; line-height: ${lineHeight} !important;
                background: ${background} !important; color: ${foreground} !important; }
            p, li { line-height: ${lineHeight} !important; }
            a:any-link { color: ${theme === 'dark' ? '#8ab4f8' : '#1659a5'} !important; }`);
    }
    #reportLocation() {
        if (this.#state !== 'ready' || this.#abort.signal.aborted) return;
        const location = locationDto(this.#view.lastLocation);
        if (location.cfi || location.fraction !== null) this.#emit('locationChanged', location);
    }
    #onDocument(doc) {
        if (this.#abort.signal.aborted || !doc) return;
        doc.addEventListener('keydown', event => this.#onKey(event), { signal: this.#abort.signal });
        // Nested browsing contexts and refresh navigation have no first-version reading role.
        // Sandbox/CSP are already active before this post-load cleanup runs.
        for (const element of doc.querySelectorAll('iframe, frame, object, embed, meta[http-equiv="refresh"]')) element.remove();
    }
    #onKey(event) {
        if (this.#state !== 'ready' || event.defaultPrevented || event.isComposing
            || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey
            || event.target?.closest?.('input, textarea, select, [contenteditable]:not([contenteditable="false"])')
            || event.target?.ownerDocument?.getSelection()?.toString()) return;
        const methods = { ArrowLeft: 'goLeft', ArrowRight: 'goRight', PageUp: 'prev', PageDown: 'next' };
        if (methods[event.key]) {
            event.preventDefault();
            // Use the same bounded serial queue as host commands; do not race relocation.
            this.receive({ version: VERSION, sessionId: this.#sessionId, requestId: crypto.randomUUID(),
                type: 'turn', payload: { direction: { goLeft: 'left', goRight: 'right', prev: 'prev', next: 'next' }[methods[event.key]] } });
        } else if (event.key === 'Escape') {
            event.preventDefault();
            this.#emit('hostCommand', { command: 'back' });
        }
    }
    async #release() {
        const view = this.#view, loaded = this.#loaded;
        this.#view = null;
        this.#loaded = null;
        try { view?.close(); } finally {
            view?.remove();
            await loaded?.close();
        }
    }
    close() {
        if (this.#closing) return this.#closing;
        this.#state = 'closed';
        this.#abort.abort();
        this.#toc.clear();
        this.#root.replaceChildren();
        this.#closing = this.#release().catch(() => {});
        return this.#closing;
    }
}
