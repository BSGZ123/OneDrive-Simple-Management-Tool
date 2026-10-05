import { ReaderAdapter } from './reader-adapter.js';
import { validId } from './protocol.js';

// Only a native WebView2 host may supply commands. No window.postMessage forwarding.
// The browser regression imports ReaderAdapter through a separate, test-only page.
const sessionId = location.hash.slice(1);
if (window === window.top && validId(sessionId) && window.chrome?.webview) {
    const adapter = new ReaderAdapter({ root: document.getElementById('reader'), sessionId,
        send: message => window.chrome.webview.postMessage(message) });
    const receive = event => adapter.receive(event.data);
    window.chrome.webview.addEventListener('message', receive);
    window.addEventListener('pagehide', () => {
        window.chrome.webview.removeEventListener('message', receive);
        void adapter.close();
    }, { once: true });
    adapter.ready();
}
