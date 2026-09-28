import { expect, signInFreshUser, test } from './fixtures.js';
const API = 'http://localhost:5015';
// The image brief is art-directed from the article itself (ADR-085), every run, both engines, with
// the real brief writer and a real render. Quality is judged by eye; what is pinned here is what
// made the old briefs generic: the article's own question and example must reach the brief, the
// colours must come from the brand palette, and the scene must be staged, not atmospheric.
test('The image brief stages the article\'s own example in the brand palette, and renders', async ({ page, request }) => {
    test.setTimeout(10 * 60 * 1000);
    const headers = await signInFreshUser(page, request, 'brief-quality');
    const brand = await request.post(`${API}/api/v1/brands`, { headers, data: {
        name: `Reveal-like ${Date.now()}`,
        styleCard: {
            voice: 'Direct, technical, evidence-led.',
            colors: [
                { role: 'Primary', hex: '#2D2A90' }, { role: 'Accent', hex: '#00B4D8' },
                { role: 'Background', hex: '#EBEBF5' }, { role: 'Highlight', hex: '#FF6B35' },
            ],
            imageStyle: 'Modern SaaS product imagery, crisp UI, soft depth.',
        } } });
    expect(brand.status()).toBe(201);
    const brandId = (await brand.json()).id;
    const campaign = await request.post(`${API}/api/v1/campaigns`, { headers, data: { name: `Brief quality ${Date.now()}`, brief: 'Launch conversational analytics for embedded dashboards.', brandId } });
    const campaignId = (await campaign.json()).id;
    try {
        const markdown = `# Conversational Analytics with Governed Queries and Embedded Dashboards

Business users want answers, not report builders. Conversational analytics lets someone type "What caused the revenue drop last quarter?" directly inside the product they already use, and get a chart and an explanation back in seconds.

## Governed, not guessed
Every question is translated into a query against the semantic layer the data team already governs. Row-level security, approved metrics and certified joins still apply, so the answer a sales manager sees is scoped to their region and uses the same "Net Revenue" definition as finance.

## From question to explanation
The assistant finds the anomaly on the revenue trend, breaks it down by region and product line, and explains the driver in plain English — for example, that East Coast sales fell because a supply-chain delay held back shipments — with a small chart the user can pin to their dashboard.

## Embedded where work happens
Because the chat and the dashboards are embedded in the host application, answers stay connected to the data, the permissions and the workflow. No exports, no separate BI tool.`;
        const blog = await request.post(`${API}/api/v1/campaigns/${campaignId}/artifacts`, { headers, data: { kind: 'blog', title: 'Conversational Analytics with Governed Queries and Embedded Dashboards', contentJson: JSON.stringify({ content: { markdown } }) } });
        const artifactId = (await blog.json()).id;
        const slot = await request.post(`${API}/api/v1/campaigns/${campaignId}/image-slots`, { headers, data: { artifactId, targetWidth: 1600, targetHeight: 840 } });
        expect(slot.status()).toBe(201);
        const slotId = (await slot.json()).id;
        const preview = await request.get(`${API}/api/v1/campaigns/${campaignId}/image-slots/${slotId}/prompt-preview`, { headers, timeout: 180_000 });
        expect(preview.ok()).toBeTruthy();
        const brief = (await preview.json()).visualBrief ?? '';
        // The article's own moment, quoted — not the topic in the abstract.
        expect(brief).toContain('What caused the revenue drop last quarter?');
        expect(brief).toMatch(/East Coast/);
        // Colours only from the brand palette, each named.
        for (const hex of ['#2D2A90', '#00B4D8']) expect(brief.toUpperCase()).toContain(hex);
        expect(brief).not.toMatch(/#(?!2D2A90|00B4D8|EBEBF5|FF6B35)[0-9A-F]{6}\b/i);
        // A staged scene of the promised length, opening with the asset type.
        expect(brief).toMatch(/^An? [^.]*image/i);
        const words = brief.split(/\s+/).length;
        expect(words).toBeGreaterThan(80);
        expect(words).toBeLessThan(320);

        const gen = await request.post(`${API}/api/v1/campaigns/${campaignId}/image-slots/${slotId}/generate`, { headers, data: { variants: 1 }, timeout: 360_000 });
        expect(gen.ok()).toBeTruthy();
        const g = await gen.json();
        expect(g.failures).toEqual([]);
        expect(g.variants).toHaveLength(1);
        const img = await request.get(g.variants[0].url);
        expect(img.ok()).toBeTruthy();
        expect((await img.body()).length).toBeGreaterThan(20_000);
    } finally {
        await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
        await request.delete(`${API}/api/v1/brands/${brandId}`, { headers });
    }
});
