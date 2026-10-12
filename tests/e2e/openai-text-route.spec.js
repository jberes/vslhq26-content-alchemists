import { expect, test } from './fixtures.js';

// ADR-086: with Settings → Models → Text generation on "OpenAI direct", every piece of text is
// written by that one model at max effort. Drives the real producer path with live calls and
// then reads the AI log: every model call must have been answered by gpt-6.1-sol at OpenAI.
// Needs an OpenAI key for the demo account: Ai:OpenAI:ApiKey in the gitignored dev config.
const API = 'http://localhost:5015';
const MODEL = 'gpt-6.1-sol';
const ROUTE_KEYS = ['ai.text.provider', 'ai.text.model', 'ai.text.effort', 'ai.text.max-output-tokens'];

test('OpenAI direct writes every piece of a Press Run at max effort', async ({ page, request }, testInfo) => {
    test.setTimeout(3 * 60 * 60 * 1000);
    let campaignId = null;
    let accessToken = null;
    let credentials = null;
    // Access tokens live 15 minutes and a max-effort run lasts far longer: sign in again
    // before every API call made after the run.
    const freshToken = async () => {
        const login = await request.post(`${API}/api/v1/auth/login`, { data: credentials });
        expect(login.ok()).toBeTruthy();
        return (await login.json()).accessToken;
    };

    try {
        await page.goto('/sign-in');
        await expect(page.getByLabel('Email')).not.toHaveValue('');
        const email = await page.getByLabel('Email').inputValue();
        const password = await page.getByLabel('Password').inputValue();
        credentials = { email, password };
        accessToken = await freshToken();
        await page.getByRole('button', { name: 'Sign in' }).click();
        await expect(page).toHaveURL(/\/$/);

        const switchedAt = Date.now();
        await page.goto('/settings');
        await page.getByRole('tab', { name: 'Models' }).click();
        const card = page.getByRole('article', { name: 'Which model writes every piece of text' });
        await card.getByLabel('Text generation provider', { exact: true }).selectOption('openai');
        await expect(card.getByLabel('Text generation model', { exact: true })).toHaveValue(MODEL);
        await expect(card.getByLabel('Reasoning effort', { exact: true })).toHaveValue('max');
        await expect(card.getByLabel('Max output tokens', { exact: true })).toHaveValue('128000');
        await card.getByRole('button', { name: 'Save text model' }).click();
        await expect(card.getByRole('status'))
            .toContainText(`Now: OpenAI · ${MODEL} · Max effort · 128,000 output tokens`);
        await expect(card.getByRole('status')).toContainText('READY');
        await expect(card.getByRole('status')).not.toContainText('NOT READY');

        await page.goto('/campaigns/new');
        await page.getByLabel('Campaign name').fill(`OpenAI route E2E ${Date.now()}`);
        const source = webinarCaptions();
        await page.getByLabel('Paste source text').evaluate((element, value) => {
            element.value = value;
            element.dispatchEvent(new Event('input', { bubbles: true }));
        }, source);
        const campaignCreated = page.waitForResponse(response =>
            response.url().endsWith('/api/v1/campaigns')
            && response.request().method() === 'POST'
            && response.status() === 201);
        await page.getByRole('button', { name: 'Use pasted text' }).click();
        campaignId = (await (await campaignCreated).json()).id;

        await expect(page.getByRole('heading', { name: 'Choose the campaign intent.' })).toBeVisible();
        await page.getByRole('radio', { name: /^Repurpose / }).click();
        await expect(page.getByRole('heading', { name: 'Set the research context.' }))
            .toBeVisible({ timeout: 30 * 60 * 1000 });
        await expect(page.getByLabel('AI-generated audience for the analysis'))
            .not.toHaveValue('', { timeout: 30 * 60 * 1000 });
        await page.getByLabel('Site URL').fill('https://www.revealbi.io');

        const deepResponse = page.waitForResponse(response =>
            response.url().endsWith('/api/v1/seo/deep-analysis')
            && response.request().method() === 'POST', { timeout: 60 * 60 * 1000 });
        await page.getByRole('button', { name: 'Build the deep SEO/AEO report' }).click();
        expect((await deepResponse).ok()).toBeTruthy();
        await page.getByRole('button', { name: 'Approve report & build content brief' }).click();
        await expect(page.getByRole('heading', { name: 'Choose the output recipe.' }))
            .toBeVisible({ timeout: 60 * 60 * 1000 });
        await expect(page.getByRole('button', { name: 'Press Run' })).toBeEnabled({ timeout: 60 * 60 * 1000 });
        await page.getByRole('button', { name: 'Press Run' }).click();
        await expect(page).toHaveURL(new RegExp(`/campaigns/${campaignId}/floor`));

        const summary = page.locator('.cm-press__summary');
        await expect(summary).toBeVisible({ timeout: 2 * 60 * 60 * 1000 });
        const results = await page.locator('.cm-press__item').evaluateAll(rows => rows.map(row => ({
            kind: row.querySelector('.cm-press__kind')?.textContent?.trim(),
            outcome: row.classList.contains('cm-press__item--done') ? 'printed' : 'failed',
            detail: row.querySelector('.cm-meta')?.textContent?.trim(),
        })));
        await testInfo.attach('press-run-items', { body: JSON.stringify(results, null, 2), contentType: 'application/json' });
        expect(results.length).toBeGreaterThan(0);
        expect(results.filter(result => result.outcome === 'failed')).toEqual([]);
        await expect(summary).toContainText(`${results.length} of ${results.length} printed`);

        const log = await request.get(`${API}/api/v1/ai/log`, { headers: bearer(await freshToken()) });
        expect(log.ok()).toBeTruthy();
        const calls = (await log.json()).filter(entry => Date.parse(entry.at) >= switchedAt);
        await testInfo.attach('model-calls', {
            body: JSON.stringify(calls.map(({ kind, modelAlias, model, success, durationMs }) =>
                ({ kind, modelAlias, model, success, durationMs })), null, 2),
            contentType: 'application/json',
        });
        expect(calls.length).toBeGreaterThan(results.length);
        for (const call of calls) {
            expect(call.model, `${call.kind} (${call.modelAlias})`).toBe(`${MODEL} @ api.openai.com`);
        }
        expect(calls.some(call => call.modelAlias === 'chat-audit')).toBe(true);
    } finally {
        if (credentials) {
            accessToken = await freshToken();
            for (const key of ROUTE_KEYS) {
                await request.delete(`${API}/api/v1/settings/${key}`, { headers: bearer(accessToken), timeout: 120_000 });
            }
            if (campaignId) {
                await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers: bearer(accessToken), timeout: 120_000 });
            }
        }
    }
});

// About ten minutes of webinar: enough for every generator to have something concrete to cite,
// short enough that a max-effort run is measured in tens of minutes rather than hours.
function webinarCaptions() {
    const lines = [
        "Welcome everyone, today we're building an embedded analytics feature end to end.",
        'The host application is a sales portal that already has thousands of daily users.',
        'Our goal is a dashboard inside the product, not a separate BI tool behind an iframe.',
        'First we let an AI coding agent wire the data source using the SDK skills.',
        'The agent generated the connection, the semantic model and the first three charts.',
        'Row-level security comes from the tenant claim, so each customer sees only their rows.',
        'We measured load time at under one second because queries are cached per tenant.',
        'White-labeling uses the same theme tokens as the host app, so it matches the brand.',
        'Business users can author a new view themselves without filing a ticket.',
        'Deployment is a normal CI pipeline step, promoting dashboards from staging to production.',
        'Keyboard navigation and screen reader labels work on every chart out of the box.',
        "Pricing is per application, so there's no surprise cost per viewer as usage grows.",
        'Someone asked whether this works on-premises, and yes, the setup is exactly the same.',
        'The key thing to remember is that the host application stays in control of data access.',
        'To try it, start with one high-traffic page and one metric your customers already ask for.',
    ];
    const captions = [];
    for (let i = 0; i < 120; i++) {
        captions.push(lines[i % lines.length]);
    }
    return captions.join('\n');
}

function bearer(accessToken) {
    return { Authorization: `Bearer ${accessToken}` };
}
