// The bundle budget from story 5.1: < 250 KB gzip. A budget nobody measures is a wish, and
// the editor is the one place in this client where a careless import pulls in a framework.
//
// Runs against the built asset, so `npm run build` must have happened — CI does both in the
// editor-interop job.

import { describe, expect, it } from 'vitest';
import { gzipSync } from 'node:zlib';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const BUDGET_BYTES = 250 * 1024;

const bundle = fileURLToPath(
    new URL('../../src/Castmill.UI/wwwroot/js/castmill-editor.js', import.meta.url));
const apexTreeBundle = fileURLToPath(
    new URL('../../src/Castmill.UI/wwwroot/js/castmill-apextree.js', import.meta.url));
const apexTreeInterop = fileURLToPath(
    new URL('../../src/editor-interop/src/apextree.js', import.meta.url));
const viewsCss = fileURLToPath(
    new URL('../../src/Castmill.UI/wwwroot/css/views.css', import.meta.url));

describe('editor bundle', () => {
    it('has been built', () => {
        expect(existsSync(bundle), `expected a built bundle at ${bundle} — run \`npm run build\``).toBe(true);
    });

    it('fits the 250 KB gzip budget', () => {
        const gzipped = gzipSync(readFileSync(bundle)).length;

        // Reported either way: the number is the useful part when it fails.
        console.log(`editor bundle: ${(gzipped / 1024).toFixed(1)} KB gzip of ${(BUDGET_BYTES / 1024).toFixed(0)} KB budget`);

        expect(gzipped).toBeLessThan(BUDGET_BYTES);
    });

    it('carries no framework runtime', () => {
        // ADR-F03's "vanilla JS/DOM only". A React or Vue runtime slipping in through a
        // TipTap sub-dependency is the specific failure this guards.
        const source = readFileSync(bundle, 'utf8');

        for (const marker of ['react-dom', 'createElement:', '__vue__', 'Vue.createApp']) {
            expect(source).not.toContain(marker);
        }
    });
});

describe('ApexTree bundle', () => {
    it('is built as its own lazy asset', () => {
        expect(existsSync(apexTreeBundle),
            `expected a built bundle at ${apexTreeBundle} — run \`npm run build\``).toBe(true);

        const editorSource = readFileSync(bundle, 'utf8');
        expect(editorSource).not.toContain('Campaign content hierarchy');
    });

    it('stays below 100 KB gzip', () => {
        const gzipped = gzipSync(readFileSync(apexTreeBundle)).length;
        console.log(`ApexTree bundle: ${(gzipped / 1024).toFixed(1)} KB gzip of 100 KB budget`);
        expect(gzipped).toBeLessThan(100 * 1024);
    });

    it('uses explicit text-safe semantic colors for status badges', () => {
        const interopSource = readFileSync(apexTreeInterop, 'utf8');
        const css = readFileSync(viewsCss, 'utf8');

        expect(interopSource).toContain('color: badgeColor');
        expect(interopSource).toContain('palette.accentStrong');
        expect(interopSource).toContain('palette.success');
        expect(interopSource).toContain('color:${palette.onAccent}');
        expect(interopSource).toContain('color:${palette.muted}');
        expect(interopSource).toContain('escapeHtml(content.badge.text)');
        expect(css).not.toContain('--apex-tree-badge-color');
    });

    it('leaves fitted SVG dimensions under ApexTree control', () => {
        const interopSource = readFileSync(apexTreeInterop, 'utf8');

        expect(interopSource).toContain('graph.fitScreen()');
        expect(interopSource).toContain('const nextWidth = width / 0.7');
        expect(interopSource).toContain('graph.updateViewBox');
        expect(interopSource).not.toContain('graph.zoom(');
        expect(interopSource).not.toContain("svg.setAttribute('width'");
        expect(interopSource).not.toContain("svg.setAttribute('height'");
    });
});
