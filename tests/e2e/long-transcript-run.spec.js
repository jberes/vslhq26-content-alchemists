import { expect, test } from './fixtures.js';

// A one-hour webinar arrives as ~1,000 short caption segments, and every segment becomes one
// approved evidence block. Observed 2026-10-10: a 57-minute recording (1,067 segments, 51,722
// characters) passed approval and then failed every generator in the Press Run, because the
// generation guard counted blocks. Every other spec uses a paragraph-sized source, so nothing
// exercised a real recording's shape. This one drives the whole producer path with live models.
const API = 'http://localhost:5015';
const SEGMENTS = 1_100;

test('an hour-long transcript prints every piece in the Press Run', async ({ page, request }, testInfo) => {
    test.setTimeout(40 * 60 * 1000);
    let campaignId = null;
    let accessToken = null;

    try {
        await page.goto('/sign-in');
        await expect(page.getByLabel('Email')).not.toHaveValue('');
        const email = await page.getByLabel('Email').inputValue();
        const password = await page.getByLabel('Password').inputValue();
        const login = await request.post(`${API}/api/v1/auth/login`, { data: { email, password } });
        expect(login.ok()).toBeTruthy();
        accessToken = (await login.json()).accessToken;
        await cleanupPriorRuns(request, accessToken);

        await page.getByRole('button', { name: 'Sign in' }).click();
        await expect(page).toHaveURL(/\/$/);
        await page.goto('/campaigns/new');

        await page.getByLabel('Campaign name').fill(`Long transcript E2E ${Date.now()}`);
        // One input event, as a real paste delivers it: fill() types through Playwright's
        // editability retries, which never settle on a 50,000-character value.
        const captions = webinarCaptions();
        await page.getByLabel('Paste source text').evaluate((element, value) => {
            element.value = value;
            element.dispatchEvent(new Event('input', { bubbles: true }));
        }, captions);
        await expect(page.getByText(`${captions.length} characters`)).toBeVisible();
        const campaignCreated = page.waitForResponse(response =>
            response.url().endsWith('/api/v1/campaigns')
            && response.request().method() === 'POST'
            && response.status() === 201);
        await page.getByRole('button', { name: 'Use pasted text' }).click();
        campaignId = (await (await campaignCreated).json()).id;

        await expect(page.getByRole('heading', { name: 'Choose the campaign intent.' })).toBeVisible();
        const transcript = (await listArtifacts(request, accessToken, campaignId))
            .find(artifact => artifact.kind === 'transcript');
        const stored = await readArtifact(request, accessToken, campaignId, transcript.id);
        expect(JSON.parse(stored.contentJson).segments.length).toBeGreaterThan(1_000);
        await page.getByRole('radio', { name: /^Repurpose / }).click();
        await expect(page.getByRole('heading', { name: 'Set the research context.' }))
            .toBeVisible({ timeout: 180_000 });
        await expect(page.getByLabel('AI-generated audience for the analysis'))
            .not.toHaveValue('', { timeout: 180_000 });
        await page.getByLabel('Site URL').fill('https://www.revealbi.io');

        const deepResponse = page.waitForResponse(response =>
            response.url().endsWith('/api/v1/seo/deep-analysis')
            && response.request().method() === 'POST', { timeout: 10 * 60 * 1000 });
        await page.getByRole('button', { name: 'Build the deep SEO/AEO report' }).click();
        expect((await deepResponse).ok()).toBeTruthy();
        await expect(page.getByRole('heading', { name: 'Review the deep SEO/AEO report.' })).toBeVisible();

        await page.getByRole('button', { name: 'Approve report & build content brief' }).click();
        await expect(page.getByRole('heading', { name: 'Choose the output recipe.' }))
            .toBeVisible({ timeout: 180_000 });
        await expect(page.getByRole('button', { name: 'Press Run' })).toBeEnabled({ timeout: 180_000 });
        await page.getByRole('button', { name: 'Press Run' }).click();
        await expect(page).toHaveURL(new RegExp(`/campaigns/${campaignId}/floor`));

        const summary = page.locator('.cm-press__summary');
        await expect(summary).toBeVisible({ timeout: 30 * 60 * 1000 });

        const items = page.locator('.cm-press__item');
        const results = await items.evaluateAll(rows => rows.map(row => ({
            kind: row.querySelector('.cm-press__kind')?.textContent?.trim(),
            outcome: row.classList.contains('cm-press__item--done') ? 'printed' : 'failed',
            detail: row.querySelector('.cm-meta')?.textContent?.trim(),
        })));
        await testInfo.attach('press-run-items', {
            body: JSON.stringify(results, null, 2),
            contentType: 'application/json',
        });
        expect(results.length).toBeGreaterThan(0);
        expect(results.filter(result => result.outcome === 'failed')).toEqual([]);
        await expect(summary).toContainText(`${results.length} of ${results.length} printed`);
        await expect(summary).not.toContainText('failed');
    } finally {
        if (campaignId && accessToken) {
            await request.delete(`${API}/api/v1/campaigns/${campaignId}`, {
                headers: bearer(accessToken),
                timeout: 120_000,
            });
        }
    }
});

// Caption-length lines, one per segment, the way a speech-to-text service breaks up an hour
// of talk. Varied enough that every generator has something concrete to cite.
function webinarCaptions() {
    const sections = [
        ['embedded analytics', 'the host application', 'a dashboard inside the product'],
        ['agent skills', 'the coding assistant', 'a generated data source'],
        ['row-level security', 'the tenant filter', 'each customer only seeing their own rows'],
        ['the semantic layer', 'shared metric definitions', 'revenue meaning one thing everywhere'],
        ['performance', 'query caching', 'a chart that loads in under a second'],
        ['white-labeling', 'the theme tokens', 'the dashboard matching the host brand'],
        ['self-service authoring', 'the business user', 'a new view without a ticket'],
        ['deployment', 'the CI pipeline', 'promoting a dashboard from staging to production'],
        ['accessibility', 'keyboard navigation', 'screen readers announcing every data point'],
        ['pricing and licensing', 'per-application pricing', 'no surprise cost per viewer'],
        ['the live demo', 'the sample sales app', 'an agent wiring a grid to a chart'],
        ['audience questions', 'a question from the chat', 'what teams should try first'],
    ];
    const frames = [
        (topic) => `So let's talk about ${topic} for a minute.`,
        (topic, actor) => `What we see a lot is that ${actor} has to own ${topic}.`,
        (topic, actor, result) => `And the goal here is ${result}.`,
        (topic) => `This is where most teams get stuck with ${topic}.`,
        (topic, actor) => `If you let ${actor} drive it, it gets much simpler.`,
        (topic, actor, result) => `I'll show you ${result} in just a second.`,
        () => 'Okay, let me switch over to the screen.',
        (topic) => `You can see ${topic} configured right here.`,
        (topic, actor) => `Notice that ${actor} didn't need any custom code for that.`,
        (topic, actor, result) => `That gets you to ${result} on day one.`,
        () => 'Let me pause there and take a quick look at the chat.',
        (topic) => `Someone is asking whether ${topic} works on-premises.`,
        () => 'Yes, and the setup is exactly the same.',
        (topic, actor) => `The key thing to remember is that ${actor} stays in control.`,
    ];
    const lines = [];
    for (let i = 0; i < SEGMENTS; i++) {
        const [topic, actor, result] = sections[Math.floor(i / (SEGMENTS / sections.length))];
        lines.push(frames[i % frames.length](topic, actor, result));
    }
    return lines.join('\n');
}

async function listArtifacts(request, accessToken, campaignId) {
    const response = await request.get(`${API}/api/v1/campaigns/${campaignId}/artifacts`, {
        headers: bearer(accessToken),
    });
    expect(response.ok()).toBeTruthy();
    return response.json();
}

async function readArtifact(request, accessToken, campaignId, artifactId) {
    const response = await request.get(`${API}/api/v1/campaigns/${campaignId}/artifacts/${artifactId}`, {
        headers: bearer(accessToken),
    });
    expect(response.ok()).toBeTruthy();
    return response.json();
}

function bearer(accessToken) {
    return { Authorization: `Bearer ${accessToken}` };
}

async function cleanupPriorRuns(request, accessToken) {
    const headers = bearer(accessToken);
    const campaigns = await request.get(`${API}/api/v1/campaigns`, { headers });
    if (campaigns.ok()) {
        for (const campaign of await campaigns.json()) {
            if (campaign.name?.startsWith('Long transcript E2E ')) {
                await request.delete(`${API}/api/v1/campaigns/${campaign.id}`, { headers });
            }
        }
    }
}
