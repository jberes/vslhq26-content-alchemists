import { expect, test } from '@playwright/test';

const live = process.env.CASTMILL_E2E_LIVE === '1';

test.describe('analysis-first campaign production', () => {
    test.skip(!live,
        'Set CASTMILL_E2E_LIVE=1 to run the metered DataForSEO and answer-engine scenario.');

    test('deep report gates production and renders ApexCharts plus ApexTree', async ({ page, request }) => {
        let campaignId = null;
        let brandId = null;
        let accessToken = null;

        try {
            await page.goto('/sign-in');
            await expect(page.getByRole('heading', { name: 'Sign in.' })).toBeVisible();

            const email = await page.getByLabel('Email').inputValue();
            const password = await page.getByLabel('Password').inputValue();
            expect(email).not.toBe('');
            expect(password).not.toBe('');

            const login = await request.post('http://localhost:5005/api/v1/auth/login', {
                data: { email, password },
            });
            expect(login.ok()).toBeTruthy();
            accessToken = (await login.json()).accessToken;
            await cleanupPriorE2eRows(request, accessToken);

            const brandName = `SEO E2E Brand ${Date.now()}`;
            const brandVoice = 'Direct, technical, evidence-led, and practical';
            const brand = await request.post('http://localhost:5005/api/v1/brands', {
                headers: bearer(accessToken),
                data: { name: brandName, styleCard: { voice: brandVoice } },
            });
            expect(brand.status()).toBe(201);
            brandId = (await brand.json()).id;

            await page.getByRole('button', { name: 'Sign in' }).click();
            await expect(page).toHaveURL(/\/$/);
            await page.goto('/campaigns/new');

            const runName = `SEO E2E ${Date.now()}`;
            await page.getByLabel('Campaign name').fill(runName);
            await page.getByLabel('Paste a transcript').fill(
                'This briefing explains how engineering leaders evaluate embedded analytics, '
                + 'compare build versus buy, improve application performance, and create accessible '
                + 'data experiences. It includes deployment guidance, governance, security, and '
                + 'practical measurement for software teams.');

            const campaignCreated = page.waitForResponse(response =>
                response.url().endsWith('/api/v1/campaigns')
                && response.request().method() === 'POST'
                && response.status() === 201);
            await page.getByRole('button', { name: 'Transcribe pasted text' }).click();
            campaignId = (await (await campaignCreated).json()).id;

            await expect(page.getByRole('heading', { name: 'Set the research context.' }))
                .toBeVisible({ timeout: 180_000 });
            const audience = page.getByLabel('AI-generated audience for the analysis');
            await expect(audience).not.toHaveValue('', { timeout: 180_000 });
            await page.getByRole('combobox').selectOption({ label: brandName });
            const voice = page.getByLabel('Brand voice — from selected Brand', { exact: true });
            await expect(voice).toHaveValue(brandVoice);
            await expect(voice).toHaveAttribute('readonly', '');
            await page.getByLabel('Site URL').fill('https://www.revealbi.io');

            const transcriptId = await resolveTranscriptId(request, accessToken, campaignId);
            const blocked = await request.post(
                `http://localhost:5005/api/v1/ai/campaigns/${campaignId}/generate/newsletter`, {
                    headers: bearer(accessToken),
                    data: { transcriptArtifactId: transcriptId },
                });
            expect(blocked.status()).toBe(409);

            const deepResponse = page.waitForResponse(response =>
                response.url().endsWith('/api/v1/seo/deep-analysis')
                && response.request().method() === 'POST', { timeout: 10 * 60 * 1000 });
            await page.getByRole('button', { name: 'Build the deep SEO/AEO report' }).click();
            const analysisResponse = await deepResponse;
            expect(analysisResponse.ok()).toBeTruthy();
            const report = await analysisResponse.json();

            expect(report.research.hasProviderMetrics).toBe(true);
            expect(report.research.keywords.length).toBeGreaterThan(5);
            expect(report.research.providerLookups).toEqual(expect.arrayContaining([
                'dataforseo_labs/google/keyword_suggestions/live',
                'dataforseo_labs/google/keyword_ideas/live',
                'dataforseo_labs/google/keyword_overview/live',
                'serp/google/organic/live/advanced',
            ]));
            expect(report.serp.organicResults.length).toBeGreaterThan(0);
            expect(report.insights.rankedKeywords.length).toBeGreaterThan(0);
            expect(report.insights.siteAuthority.referringDomains).not.toBeNull();
            expect(report.insights.competitors.length).toBeGreaterThan(1);
            expect(report.insights.competitors.some(row => row.topicVisibility != null)).toBe(true);
            expect(report.insights.competitors.some(row => row.authority?.referringDomains != null)).toBe(true);
            expect(report.insights.competitors.some(row => row.footprint?.totalOrganic > 0)).toBe(true);
            expect(report.insights.aeo.engines.length).toBe(4);

            await expect(page.getByRole('heading', { name: 'AI answer visibility' })).toBeVisible();
            await expect(page.getByRole('heading', { name: 'Target keywords and opportunity' })).toBeVisible();
            await expect(page.getByRole('heading', { name: 'Who ranks around you' })).toBeVisible();
            await expect(page.locator('.apexcharts-svg').first()).toBeVisible();

            const approval = page.waitForResponse(response =>
                response.url().includes(`/api/v1/campaigns/${campaignId}/seo-targets`)
                && response.request().method() === 'PUT'
                && response.ok());
            await page.getByRole('button', { name: 'Approve report & build content brief' }).click();
            await approval;
            await expect(page.getByText('Built from the approved report.')).toBeVisible({ timeout: 180_000 });

            const generated = await request.post(
                `http://localhost:5005/api/v1/ai/campaigns/${campaignId}/generate/newsletter`, {
                    headers: bearer(accessToken),
                    data: { transcriptArtifactId: transcriptId },
                    timeout: 180_000,
                });
            expect(generated.ok()).toBeTruthy();

            await page.goto(`/campaigns/${campaignId}/seo`);
            await expect(page.locator('.apexcharts-svg').first()).toBeVisible();
            await expect(page.locator('svg[aria-label="Campaign content hierarchy"]')).toBeVisible();
        } finally {
            if (campaignId && accessToken) {
                await request.delete(`http://localhost:5005/api/v1/campaigns/${campaignId}`, {
                    headers: bearer(accessToken),
                });
            }
            if (brandId && accessToken) {
                await request.delete(`http://localhost:5005/api/v1/brands/${brandId}`, {
                    headers: bearer(accessToken),
                });
            }
        }
    });
});

async function resolveTranscriptId(request, accessToken, campaignId) {
    const response = await request.get(
        `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
            headers: bearer(accessToken),
        });
    expect(response.ok()).toBeTruthy();
    const artifacts = await response.json();
    return artifacts.find(artifact => artifact.kind === 'transcript').id;
}

function bearer(accessToken) {
    return { Authorization: `Bearer ${accessToken}` };
}

async function cleanupPriorE2eRows(request, accessToken) {
    const headers = bearer(accessToken);
    const campaigns = await request.get('http://localhost:5005/api/v1/campaigns', { headers });
    if (campaigns.ok()) {
        for (const campaign of await campaigns.json()) {
            if (campaign.name?.startsWith('SEO E2E ')) {
                await request.delete(`http://localhost:5005/api/v1/campaigns/${campaign.id}`, { headers });
            }
        }
    }

    const brands = await request.get('http://localhost:5005/api/v1/brands', { headers });
    if (brands.ok()) {
        for (const brand of await brands.json()) {
            if (brand.name?.startsWith('SEO E2E Brand ')) {
                await request.delete(`http://localhost:5005/api/v1/brands/${brand.id}`, { headers });
            }
        }
    }
}
