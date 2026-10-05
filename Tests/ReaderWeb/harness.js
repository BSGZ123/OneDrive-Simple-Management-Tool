import { ReaderAdapter } from './reader/reader-adapter.js';
import { DEFAULT_SETTINGS } from './reader/protocol.js';

// Test-only global host API. This file is excluded from the app's asset set.
let sessionId = location.hash.slice(1);
window.messages = [];
window.violations = [];
window.executions = [];
window.liveBlobs = new Set();
// Count explicit observation lifetimes without retaining elements or observers.
// This catches unobserve(wrongTarget), even when the eventual GC can collect it.
window.activeResizeTargets = 0;
window.ResizeObserver = class extends ResizeObserver {
    #targets = new WeakSet();
    #count = 0;
    observe(target, options) {
        super.observe(target, options);
        if (!this.#targets.has(target)) {
            this.#targets.add(target); this.#count++; window.activeResizeTargets++;
        }
    }
    unobserve(target) {
        super.unobserve(target);
        if (this.#targets.delete(target)) { this.#count--; window.activeResizeTargets--; }
    }
    disconnect() {
        super.disconnect();
        window.activeResizeTargets -= this.#count;
        this.#count = 0; this.#targets = new WeakSet();
    }
};
const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
URL.createObjectURL = value => { const url = create(value); window.liveBlobs.add(url); return url; };
URL.revokeObjectURL = value => { window.liveBlobs.delete(value); revoke(value); };
window.addEventListener('securitypolicyviolation', event => window.violations.push({
    directive: event.effectiveDirective, blocked: event.blockedURI,
}));
const createAdapter = () => new ReaderAdapter({ root: document.getElementById('reader'), sessionId,
    send: message => window.messages.push(message) });
let adapter = createAdapter();
// Frames inside foliate's closed shadow roots are not indexed by window.frames.
// Retain only a weak test reference; asynchronous inspection runs in the trusted
// host realm because scripts/timers in the book's own realm are disabled.
const observedViews = new WeakSet();
new MutationObserver(() => {
    const view = document.querySelector('foliate-view');
    if (!view || observedViews.has(view)) return;
    observedViews.add(view);
    view.addEventListener('load', event => {
        window.currentBookDocument = new WeakRef(event.detail.doc);
    });
}).observe(document.getElementById('reader'), { childList: true });
window.host = {
    receive: value => adapter.receive(value),
    command(type, payload = {}) {
        const requestId = crypto.randomUUID();
        const accepted = adapter.receive({ version: 1, sessionId, requestId, type, payload });
        return { requestId, accepted };
    },
    open: (location = null, settings = {}) => window.host.command('openBook', {
        settings: { ...DEFAULT_SETTINGS, ...settings }, location,
    }),
    close: () => adapter.close(),
    async resetSession(id) {
        await adapter.close();
        sessionId = id;
        window.messages = [];
        adapter = createAdapter();
        adapter.ready();
    },
};
adapter.ready();
