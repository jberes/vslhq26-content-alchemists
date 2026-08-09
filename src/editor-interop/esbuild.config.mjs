// Builds the JS assets supplied by the repo's one pinned npm workspace. Output lands in
// Castmill.UI/wwwroot/js and is gitignored. The editor keeps its independent <250 KB gzip
// budget; ApexTree is lazy-loaded only by the SEO content-cluster component.

import { build, context } from 'esbuild';
import { fileURLToPath } from 'node:url';

const outdir = fileURLToPath(new URL('../Castmill.UI/wwwroot/js', import.meta.url));

/** @type {import('esbuild').BuildOptions} */
const options = {
    entryPoints: {
        'castmill-editor': fileURLToPath(new URL('src/index.js', import.meta.url)),
        'castmill-apextree': fileURLToPath(new URL('src/apextree.js', import.meta.url)),
    },
    outdir,
    bundle: true,
    format: 'esm',
    target: 'es2022',
    platform: 'browser',
    sourcemap: true,
    minify: true,
    legalComments: 'linked',
    logLevel: 'info',
};

if (process.argv.includes('--watch')) {
    const ctx = await context(options);
    await ctx.watch();
    console.log('watching editor-interop…');
} else {
    await build(options);
}
