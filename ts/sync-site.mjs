import { copyFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = new URL('../', import.meta.url);
copyFileSync(new URL('js/fic.js', root), new URL('site/fic.js', root));
