export const VERSION = 1;
export const LIMITS = Object.freeze({
    message: 32768, cfi: 4096, tocNodes: 1024, tocDepth: 16, tocChunk: 64, label: 256,
    bookBytes: 100_000_000, expandedBytes: 256 * 1024 * 1024,
    entryBytes: 32 * 1024 * 1024, textBytes: 8 * 1024 * 1024, entries: 10000,
    openMs: 20000, navigationMs: 5000,
});
export const DEFAULT_SETTINGS = Object.freeze({
    fontSize: 20, lineHeight: 1.6, width: 720, flow: 'paginated', theme: 'light', zoom: 'fit-page',
});
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const only = (value, keys) => record(value) && Object.keys(value).every(key => keys.includes(key));
const bounded = (value, min, max) => Number.isFinite(value) && value >= min && value <= max;
export const validId = value => typeof value === 'string' && /^[a-zA-Z0-9-]{16,80}$/.test(value);
export function validLocation(value) {
    return only(value, ['cfi', 'fraction'])
        && (value.cfi == null || typeof value.cfi === 'string'
            && value.cfi.length <= LIMITS.cfi && /^epubcfi\(.+\)$/.test(value.cfi))
        && (value.fraction == null || bounded(value.fraction, 0, 1));
}
export function validSettings(value) {
    return only(value, Object.keys(DEFAULT_SETTINGS))
        && (value.fontSize === undefined || bounded(value.fontSize, 12, 40))
        && (value.lineHeight === undefined || bounded(value.lineHeight, 1, 2.5))
        && (value.width === undefined || bounded(value.width, 320, 1400))
        && (value.flow === undefined || ['paginated', 'scrolled'].includes(value.flow))
        && (value.theme === undefined || ['light', 'dark', 'sepia'].includes(value.theme))
        && (value.zoom === undefined || ['fit-page', 'fit-width'].includes(value.zoom)
            || bounded(value.zoom, 0.25, 3));
}
export function parseCommand(input, sessionId) {
    try {
        const json = typeof input === 'string' ? input : JSON.stringify(input);
        if (new TextEncoder().encode(json).length > LIMITS.message) return null;
        const message = JSON.parse(json);
        if (!only(message, ['version', 'sessionId', 'requestId', 'type', 'payload'])
            || message.version !== VERSION || message.sessionId !== sessionId
            || !validId(message.requestId)) return null;
        const p = message.payload;
        switch (message.type) {
            case 'openBook':
                if (!only(p, ['settings', 'location']) || !validSettings(p.settings)
                    || p.location != null && !validLocation(p.location)) return null;
                break;
            case 'applySettings': if (!validSettings(p)) return null; break;
            case 'restoreLocation': if (!validLocation(p)) return null; break;
            case 'navigateToToc':
                if (!only(p, ['id']) || typeof p.id !== 'string' || !/^toc-\d{1,4}$/.test(p.id)) return null;
                break;
            case 'turn':
                if (!only(p, ['direction']) || !['next', 'prev', 'left', 'right'].includes(p.direction)) return null;
                break;
            case 'close': if (!only(p, [])) return null; break;
            default: return null;
        }
        return message;
    } catch { return null; }
}
export function locationDto(location) {
    const cfi = typeof location?.cfi === 'string' && location.cfi.length <= LIMITS.cfi
        && /^epubcfi\(.+\)$/.test(location.cfi) ? location.cfi : null;
    const fraction = bounded(location?.fraction, 0, 1) ? location.fraction : null;
    return { cfi, fraction };
}
export function mapToc(toc) {
    const nodes = [], targets = new Map();
    const stack = [...(toc ?? [])].reverse().map(node => ({ node, parentId: null, depth: 0 }));
    while (stack.length) {
        const { node, parentId, depth } = stack.pop();
        if (nodes.length >= LIMITS.tocNodes || depth >= LIMITS.tocDepth) throw new Error('BookLimit');
        const id = `toc-${nodes.length}`;
        nodes.push({ id, parentId, label: String(node.label ?? '').slice(0, LIMITS.label) });
        if (typeof node.href === 'string' && node.href.length <= LIMITS.cfi) targets.set(id, node.href);
        const children = node.subitems ?? [];
        if (stack.length + children.length + nodes.length > LIMITS.tocNodes) throw new Error('BookLimit');
        for (const child of [...children].reverse()) stack.push({ node: child, parentId: id, depth: depth + 1 });
    }
    return { nodes, targets };
}
export function tocChunks(nodes) {
    const chunks = [];
    let current = [], offset = 0;
    const bytes = value => new TextEncoder().encode(JSON.stringify(value)).length;
    for (const node of nodes) {
        const candidate = [...current, node];
        // Reserve room for the version, IDs, type and envelope. Count UTF-8 bytes,
        // not just nodes or UTF-16 characters (e.g. long Chinese TOC labels).
        if (current.length && (candidate.length > LIMITS.tocChunk
            || bytes({ offset, total: nodes.length, nodes: candidate }) > LIMITS.message - 512)) {
            chunks.push({ offset, total: nodes.length, nodes: current });
            offset += current.length;
            current = [];
        }
        current.push(node);
    }
    if (current.length || !chunks.length) chunks.push({ offset, total: nodes.length, nodes: current });
    return chunks;
}
