import { copyFileSync } from 'node:fs';
const root = new URL('../', import.meta.url);
copyFileSync(new URL('js/fic.js', root), new URL('site/fic.js', root));
copyFileSync(new URL('js/fic.LICENSES.txt', root), new URL('site/fic.LICENSES.txt', root));
