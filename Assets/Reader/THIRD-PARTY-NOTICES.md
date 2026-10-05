# EPUB reader dependencies

This directory contains an EPUB-only subset of [foliate-js](https://github.com/johnfactotum/foliate-js/tree/78914aef4466eb960965702401634c2cb348e9b1), retrieved on 2026-10-05 at commit `78914aef4466eb960965702401634c2cb348e9b1`.

- foliate-js: Copyright John Factotum; MIT license in [foliate/LICENSE](foliate/LICENSE).
- zip.js: Copyright Gildas Lormeau; BSD-3-Clause license in [foliate/vendor/zip-LICENSE](foliate/vendor/zip-LICENSE). The committed bundle is copied from that exact foliate-js commit. Its upstream lockfile identifies zip.js 2.8.22; the official 2.8.22 package supplies its license and the test fixture writer.

[dependencies.json](dependencies.json) records upstream and distributed SHA-256 hashes, source URLs, the complete shipped vendor file list and local patches. No CDN or npm install is required to run these assets. Test tooling is isolated in `Tests/ReaderWeb` and has its own lockfile.

Local patches remove general-purpose file detection from `view.js`, require an already validated EPUB book, remove `allow-scripts` from both chapter iframe renderers, prevent late Blob URL and renderer creation after close, disconnect the pagination container's resize observer, and guard pagination against detached documents and stale font/animation callbacks. They target Chromium/WebView2; WebKit compatibility is outside this integration's scope. `Tests/ReaderWeb/vendor.mjs` reproduces these patches from the pinned upstream source.

The upstream view's shared search/text/TTS modules are retained to keep its module graph intact, but no search or speech commands are exposed. MOBI, fflate, FB2, comic-book support, PDF.js and the upstream demo UI are not distributed.
