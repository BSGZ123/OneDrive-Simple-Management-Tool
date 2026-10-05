// Inspect image headers before any browser decoder sees the resource.
// First-release raster scope: JPEG, PNG and single-frame GIF. SVG has a separate DOM budget.
const check = value => { if (!value) throw new Error('BookLimit'); };
const pixels = (width, height) => {
    check(width > 0 && height > 0 && width <= 8192 && height <= 8192 && width * height <= 16_000_000);
    return width * height;
};
export function rasterPixels(bytes) {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const ascii = (offset, length) => String.fromCharCode(...bytes.subarray(offset, offset + length));
    if (bytes.length >= 33 && ascii(1, 3) === 'PNG' && bytes[0] === 137) {
        check(ascii(12, 4) === 'IHDR' && view.getUint32(8) === 13);
        const count = pixels(view.getUint32(16), view.getUint32(20));
        let offset = 8, ended = false;
        while (offset + 12 <= bytes.length) {
            const size = view.getUint32(offset), type = ascii(offset + 4, 4);
            check(offset + 12 + size <= bytes.length && type !== 'acTL' && (type !== 'IHDR' || offset === 8));
            offset += size + 12;
            if (type === 'IEND') { ended = true; break; }
        }
        check(ended); return count;
    }
    if (bytes[0] === 255 && bytes[1] === 216) {
        let offset = 2, count;
        while (offset + 4 <= bytes.length) {
            check(bytes[offset++] === 255);
            while (bytes[offset] === 255) offset++;
            const marker = bytes[offset++];
            if (marker === 0xd9 || marker === 0xda) break;
            const size = view.getUint16(offset);
            check(size >= 2 && offset + size <= bytes.length);
            if ([0xc0, 0xc1, 0xc2].includes(marker)) { check(size >= 8 && !count); count = pixels(view.getUint16(offset + 5), view.getUint16(offset + 3)); }
            else check(![0xc3,0xc5,0xc6,0xc7,0xc9,0xca,0xcb,0xcd,0xce,0xcf].includes(marker));
            offset += size;
        }
        check(count); return count;
    }
    if (bytes.length >= 13 && ['GIF87a', 'GIF89a'].includes(ascii(0, 6))) {
        const count = pixels(view.getUint16(6, true), view.getUint16(8, true));
        let offset = 13 + ((bytes[10] & 128) ? 3 * 2 ** ((bytes[10] & 7) + 1) : 0), frames = 0, ended = false;
        const blocks = () => { let size; do { check(offset < bytes.length); size = bytes[offset++]; offset += size; check(offset <= bytes.length); } while (size); };
        while (offset < bytes.length) {
            const marker = bytes[offset++];
            if (marker === 0x3b) { ended = true; break; }
            if (marker === 0x21) { offset++; blocks(); }
            else {
                check(marker === 0x2c && ++frames === 1 && offset + 9 <= bytes.length);
                pixels(view.getUint16(offset + 4, true), view.getUint16(offset + 6, true));
                const flags = bytes[offset + 8]; offset += 9 + ((flags & 128) ? 3 * 2 ** ((flags & 7) + 1) : 0);
                offset++; blocks();
            }
        }
        check(ended && frames === 1); return count;
    }
    throw new Error('BookLimit');
}
export function checkSvg(root) {
    const nodes = [root, ...root.querySelectorAll('*')];
    check(nodes.length <= 2000);
    let paths = 0;
    for (const node of nodes) {
        check(!['filter', 'foreignObject', 'use', 'animate', 'animateMotion', 'animateTransform', 'set', 'pattern', 'mask'].includes(node.localName));
        paths += (node.getAttribute('d') ?? '').length + (node.getAttribute('points') ?? '').length;
        check(paths <= 256_000);
        if (node.localName === 'svg') {
            for (const name of ['width', 'height']) {
                const value = node.getAttribute(name);
                if (value) check(/^(?:\d+(?:\.\d+)?)(?:px|pt|em|rem|%)?$/.test(value) && parseFloat(value) <= 8192);
            }
            const box = node.getAttribute('viewBox')?.trim().split(/[ ,]+/).map(Number);
            if (box) check(box.length === 4 && box.every(Number.isFinite) && pixels(box[2], box[3]));
        }
    }
}
export function checkCss(text) {
    check(text.length <= 256_000);
    const sheet = new CSSStyleSheet(); sheet.replaceSync(text);
    let count = 0;
    const inspect = (rules, depth = 0) => {
        check(depth <= 16);
        for (const rule of rules) {
            check(++count <= 2000);
            if (rule.style) for (const property of rule.style) {
                const value = rule.style.getPropertyValue(property);
                check(value.length <= 16_384);
                if (['filter', 'backdrop-filter', 'animation', 'animation-name'].includes(property)) check(value === 'none');
                if (property.endsWith('shadow')) check(value.split(',').length <= 16);
            }
            if (rule.cssRules) inspect(rule.cssRules, depth + 1);
        }
    };
    inspect(sheet.cssRules);
}
