import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
const root = new URL('../../Assets/Reader/', import.meta.url);
const manifest = JSON.parse(await readFile(new URL('dependencies.json', root)));
for (const file of manifest.files) {
    const hash = createHash('sha256').update(await readFile(new URL(file.path, root))).digest('hex');
    if (hash !== file.sha256) throw new Error(`Vendor file changed: ${file.path}`);
}
console.log(`Verified ${manifest.files.length} vendored files at ${manifest.foliate.commit}`);
