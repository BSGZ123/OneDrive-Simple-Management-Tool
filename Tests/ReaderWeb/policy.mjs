export const CSP = [
    "default-src 'none'", "script-src 'self'", "script-src-attr 'none'",
    "style-src 'self' 'unsafe-inline' blob:", "img-src blob:",
    "font-src blob: data:", "connect-src 'self'", "frame-src blob:",
    "worker-src 'none'", "media-src 'none'", "object-src 'none'",
    "base-uri 'none'", "form-action 'none'", "frame-ancestors 'none'",
].join('; ');

// Explicit allowlist: never turn a request URL into a filesystem path.
export const ASSETS = [
    'index.html', 'reader.css', 'bootstrap.js', 'reader-adapter.js', 'epub-loader.js', 'resource-budget.js', 'protocol.js',
    'foliate/view.js', 'foliate/epub.js', 'foliate/epubcfi.js', 'foliate/progress.js',
    'foliate/overlayer.js', 'foliate/text-walker.js', 'foliate/paginator.js',
    'foliate/fixed-layout.js', 'foliate/search.js', 'foliate/tts.js', 'foliate/vendor/zip.js',
];
