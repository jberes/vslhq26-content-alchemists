import { expect, signInFreshUser, test } from './fixtures.js';

// The real bounded fetch (SourceImportService) against a real marketing page. revealbi.io/ai
// ships ~200 KB of scripts AND server-rendered copy inside a mega-menu with its own <article>
// promo cards; it was once rejected as a "JavaScript-only shell" and, after that, reduced to a
// menu card. SourceImportTests pins both rules against fixtures; this proves them on the live
// page. No model is called; it needs the public internet and runs on every pass.

const API = 'http://localhost:5015';

test.describe('webpage import against the live web', () => {
    test('A script-heavy marketing page imports its main copy, not a menu card or a shell verdict', async ({ page, request }) => {
        const headers = await signInFreshUser(page, request, 'import-live');
        let campaignId = null;
        try {
            const campaign = await request.post(`${API}/api/v1/campaigns`, {
                headers, data: { name: `Live import ${Date.now()}`, brief: 'Live import check.' },
            });
            expect(campaign.status()).toBe(201);
            campaignId = (await campaign.json()).id;

            const imported = await request.post(`${API}/api/v1/campaigns/${campaignId}/sources/import/webpage`, {
                headers, data: { url: 'https://www.revealbi.io/ai' }, timeout: 90_000,
            });
            const body = await imported.json();
            expect(imported.status(), JSON.stringify(body).slice(0, 400)).toBe(200);

            const text = body.blocks.map(block => block.content).join('\n');
            expect(text).not.toMatch(/renders its content with JavaScript/i);
            expect(body.blocks.length).toBeGreaterThan(3);
            expect(text.length).toBeGreaterThan(1500);
            expect(text).toMatch(/Reveal/);
            expect(text).toMatch(/\bAI\b/);
        } finally {
            if (campaignId) await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
        }
    });
});
