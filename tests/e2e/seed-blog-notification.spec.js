import { expect, signInFreshUser, test } from './fixtures.js';

// "Seed blog from this angle" on the SEO page is background work (SeedAngleNotificationTests
// pins the logic; this proves it in a real browser). The click returns at once, the card says
// the blog is being written, and when it lands the producer is told — from whichever view they
// are on — with a sticky toast whose link opens the new piece in Focus mode. Only the metered
// generation call is stubbed; the campaign, the report and the blog are real rows.

const API = 'http://localhost:5015';
const ANGLE = 'Ask your dashboards questions in plain English';
const KEYWORD = 'conversational analytics';

function report(generatedAt) {
    return {
        reportArtifactId: '00000000-0000-0000-0000-000000000000',
        generatedAt,
        research: {
            keywords: [{ term: KEYWORD, volume: 390, difficulty: 22, opportunity: 12.2, source: 'provider' }],
            questions: [{ question: 'What is conversational analytics?', source: 'paa' }],
            hasProviderMetrics: true,
            notes: [],
        },
        serp: {
            keyword: KEYWORD,
            aiOverview: null,
            featuredSnippet: null,
            organicResults: [{ rank: 1, title: 'Leader', url: 'https://leader.example/a', domain: 'leader.example' }],
        },
        recommendations: ['Lead with a direct answer.'],
        siteUrl: 'https://www.revealbi.io',
        insights: {
            aeo: { visibilityPercent: null, enginesSucceeded: 0, enginesCitingDomain: 0, engines: [] },
            keywordGaps: [],
            rankedKeywords: [],
            siteAuthority: null,
            competitors: [],
            contentAngles: [{
                angle: ANGLE,
                audienceNeed: 'Analysts who want answers without building a report',
                suggestedAsset: 'Tutorial',
                targetKeyword: KEYWORD,
                rationale: 'Competitors describe the feature but never show a worked question.',
            }],
            sections: [],
            anglesGeneratedAt: generatedAt,
        },
    };
}

async function seedCampaign(request, headers) {
    const campaign = await request.post(`${API}/api/v1/campaigns`, {
        headers, data: { name: `Seed notify ${Date.now()}`, brief: 'Seeded angle notification.' },
    });
    expect(campaign.status()).toBe(201);
    const campaignId = (await campaign.json()).id;
    const post = (kind, title, contentJson) => request.post(`${API}/api/v1/campaigns/${campaignId}/artifacts`, {
        headers, data: { kind, title, contentJson },
    });
    const transcript = await post('transcript', 'Source transcript', JSON.stringify({ text: 'Reveal answers questions about your data in plain English.' }));
    expect(transcript.status()).toBe(201);
    const seo = await post('seo-report', 'SEO/AEO report', JSON.stringify(report(new Date().toISOString())));
    expect(seo.status()).toBe(201);
    // The piece the stubbed generation "wrote" — a real row, so Focus can open it.
    const blog = await post('blog', 'Plain-English questions for your dashboards',
        JSON.stringify({ content: { markdown: '# Plain-English questions for your dashboards\n\nBody.' } }));
    expect(blog.status()).toBe(201);
    return { campaignId, blogId: (await blog.json()).id };
}

// Holds the generation POST until the test releases it, then answers with `result`.
async function holdGeneration(page, result) {
    let release;
    const released = new Promise(resolve => { release = resolve; });
    const bodies = [];
    await page.route(/\/api\/v1\/ai\/campaigns\/[^/]+\/runs\/latest/, route =>
        route.fulfill({ status: 404, contentType: 'application/json', body: '{}' }));
    await page.route(/\/api\/v1\/ai\/campaigns\/[^/]+\/generate(\/blog)?$/, async route => {
        if (route.request().method() !== 'POST') return route.fallback();
        bodies.push(route.request().postData() ?? '');
        await released;
        const item = result();
        const single = route.request().url().endsWith('/generate/blog');
        await route.fulfill({
            status: 200, contentType: 'application/json',
            body: JSON.stringify(single ? item : {
                runId: '5eed0000-0000-0000-0000-000000000001',
                succeeded: item.success ? 1 : 0, failed: item.success ? 0 : 1, results: [item],
            }),
        });
    });
    return { release: () => release(), bodies };
}

test('Seeding a blog runs in the background and notifies with a link into Focus mode', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'seed-notify');
    let campaignId = null;
    try {
        const seeded = await seedCampaign(request, headers);
        campaignId = seeded.campaignId;
        const generation = await holdGeneration(page, () => ({
            kind: 'blog', success: true, artifactId: seeded.blogId, error: null, validationWarnings: null, durationMs: 61_000,
        }));

        await page.goto(`/campaigns/${campaignId}/seo`);
        const seed = page.getByRole('button', { name: 'Seed blog from this angle' });
        await expect(seed).toBeVisible();
        await seed.click();

        // The click returns at once: the page says it is writing in the background.
        await expect(page.locator('.cm-toast', { hasText: `Writing the blog from “${ANGLE}” in the background` })).toBeVisible();
        const writing = page.getByRole('button', { name: 'Writing in the background…' });
        await expect(writing).toBeDisabled();
        await expect(writing).toHaveAttribute('aria-busy', 'true');
        await expect(page.locator('.cm-report-angle__seeding')).toContainText('notification with a link');
        await expect.poll(() => generation.bodies.length).toBe(1);
        expect(generation.bodies[0]).toContain(ANGLE);
        expect(generation.bodies[0]).toContain(KEYWORD);

        // The producer keeps working elsewhere; the notification finds them there. In-app
        // navigation — a page.goto would reload the app and end the session's run with it.
        await page.getByRole('tab', { name: 'Mill Floor' }).click();
        await expect(page).toHaveURL(new RegExp(`/campaigns/${campaignId}/floor`));
        generation.release();

        const ready = page.locator('.cm-toast', { hasText: `The blog from “${ANGLE}” is ready.` });
        await expect(ready).toBeVisible();
        const open = ready.getByRole('link', { name: 'Open in Focus mode' });
        await expect(open).toHaveAttribute('href', `campaigns/${campaignId}/focus?artifact=${seeded.blogId}`);
        // Sticky: it outlives an ordinary toast (2.6 s) and can be dismissed explicitly.
        await page.waitForTimeout(4000);
        await expect(ready).toBeVisible();
        await expect(ready.getByRole('button', { name: 'Dismiss' })).toBeVisible();

        await open.click();
        await expect(page).toHaveURL(new RegExp(`/campaigns/${campaignId}/focus\\?artifact=${seeded.blogId}`));
        await expect(page.locator(`#cm-focus-item-${seeded.blogId.replaceAll('-', '')}`)).toBeVisible();
        await expect(ready).toHaveCount(0);
    } finally {
        if (campaignId) {
            await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
        }
    }
});

test('A seeded blog that fails says so and why, and the angle can be seeded again', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'seed-fail');
    let campaignId = null;
    try {
        const seeded = await seedCampaign(request, headers);
        campaignId = seeded.campaignId;
        const generation = await holdGeneration(page, () => ({
            kind: 'blog', success: false, artifactId: null, error: 'The model refused the brief.', validationWarnings: null, durationMs: 9_000,
        }));

        await page.goto(`/campaigns/${campaignId}/seo`);
        await page.getByRole('button', { name: 'Seed blog from this angle' }).click();
        await expect(page.getByRole('button', { name: 'Writing in the background…' })).toBeDisabled();
        generation.release();

        await expect(page.locator('.cm-toast--error', {
            hasText: `The blog from “${ANGLE}” could not be written: The model refused the brief.`,
        })).toBeVisible();
        await expect(page.getByRole('button', { name: 'Seed blog from this angle' })).toBeEnabled();
        await expect(page.locator('.cm-report-angle__seeding')).toHaveCount(0);
    } finally {
        if (campaignId) {
            await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
        }
    }
});
