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
            await page.locator('select.cm-brand__kind').selectOption({ label: brandName });
            const voice = page.getByLabel('Brand voice — from selected Brand', { exact: true });
            await expect(voice).toHaveValue(brandVoice);
            await expect(voice).toHaveAttribute('readonly', '');
            await page.getByLabel('Content type').selectOption('Tutorial');
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
            expect(report.insights.aeo.engines.every(engine => engine.succeeded)).toBe(true);
            expect(report.insights.aeo.enginesSucceeded).toBe(4);

            let artifacts = await listArtifacts(request, accessToken, campaignId);
            const placeholder = artifacts.find(artifact => artifact.kind === 'blog' && artifact.isPlaceholder);
            expect(placeholder).toBeTruthy();
            expect(placeholder.title).toBe(report.insights.contentAngles[0].angle);

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
            await expect(page.getByText('SEO-informed source brief')).toBeVisible({ timeout: 180_000 });

            artifacts = await listArtifacts(request, accessToken, campaignId);
            expect(artifacts.some(artifact => artifact.kind === 'campaign-summary')).toBe(true);

            // Target edits stale only the derived angles; rebuilding them reuses the expensive
            // report snapshot instead of paying for another DataForSEO crawl.
            const targets = report.research.keywords.slice(0, 4);
            const changedTargets = await request.put(
                `http://localhost:5005/api/v1/campaigns/${campaignId}/seo-targets`, {
                    headers: bearer(accessToken),
                    data: { primaryKeyword: targets[1].term, keywords: targets, questions: report.research.questions },
                });
            expect(changedTargets.ok()).toBeTruthy();
            let storedReport = await readArtifact(request, accessToken, campaignId, report.reportArtifactId);
            expect(JSON.parse(storedReport.contentJson).anglesStale).toBe(true);
            const rebuiltAngles = await request.post(
                `http://localhost:5005/api/v1/seo/reports/${report.reportArtifactId}/angles/regenerate`, {
                    headers: bearer(accessToken), data: {}, timeout: 180_000,
                });
            expect(rebuiltAngles.ok()).toBeTruthy();

            const blogDraft = await request.post(
                `http://localhost:5005/api/v1/ai/campaigns/${campaignId}/generate/blog`, {
                    headers: bearer(accessToken), timeout: 180_000,
                    data: { transcriptArtifactId: transcriptId,
                        brief: 'Use the strongest approved angle.', replaceArtifactId: placeholder.id },
                });
            expect(blogDraft.ok()).toBeTruthy();
            const blogId = (await blogDraft.json()).artifactId;
            expect(blogId).toBe(placeholder.id);

            const ownedSocial = await request.post(
                `http://localhost:5005/api/v1/ai/campaigns/${campaignId}/generate/social-x`, {
                    headers: bearer(accessToken), timeout: 180_000,
                    data: { transcriptArtifactId: transcriptId, parentArtifactId: blogId },
                });
            expect(ownedSocial.ok()).toBeTruthy();

            const youtube = await request.post(
                `http://localhost:5005/api/v1/ai/campaigns/${campaignId}/generate/youtube`, {
                    headers: bearer(accessToken), timeout: 240_000,
                    data: { transcriptArtifactId: transcriptId },
                });
            expect(youtube.ok()).toBeTruthy();
            const youtubeResult = await youtube.json();
            expect(youtubeResult.success).toBe(true);
            const youtubeArtifact = await readArtifact(
                request, accessToken, campaignId, youtubeResult.artifactId);
            const youtubePackage = JSON.parse(youtubeArtifact.contentJson).content;
            expect(youtubePackage.titleOptions.map(option => option.slot)).toEqual(['A', 'B', 'C']);
            expect(youtubePackage.suggestedPinnedComment.endsWith('?')).toBe(true);
            expect(youtubePackage.audit.hookWithin125).toBe(true);

            const generated = await request.post(
                `http://localhost:5005/api/v1/ai/campaigns/${campaignId}/generate/newsletter`, {
                    headers: bearer(accessToken),
                    data: { transcriptArtifactId: transcriptId },
                    timeout: 180_000,
                });
            expect(generated.ok()).toBeTruthy();

            const renamed = `${runName} ready`;
            const lifecycle = await request.put(`http://localhost:5005/api/v1/campaigns/${campaignId}`, {
                headers: bearer(accessToken),
                data: { name: renamed, brief: 'Audience: engineering leaders', brandId,
                    status: 'Ready', contentType: 'Webinar' },
            });
            expect(lifecycle.ok()).toBeTruthy();
            expect((await lifecycle.json()).status).toBe('Ready');
            storedReport = await readArtifact(request, accessToken, campaignId, report.reportArtifactId);
            expect(JSON.parse(storedReport.contentJson).inputsStale).toBe(true);

            await page.goto(`/campaigns/${campaignId}/focus?artifact=${blogId}`);
            await expect(page.getByText(renamed, { exact: true }).first()).toBeVisible();
            await expect(page.getByRole('button', { name: 'Ready', exact: true })).toBeVisible();
            await expect(page.getByText('Real search data informing this content')).toBeVisible();
            const pillarGroup = page.locator('.cm-tree__group').first();
            await expect(pillarGroup.locator('.cm-focus__list-item').filter({ hasText: 'X post' }))
                .toHaveCount(1);

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
    const artifacts = await listArtifacts(request, accessToken, campaignId);
    return artifacts.find(artifact => artifact.kind === 'transcript').id;
}

async function listArtifacts(request, accessToken, campaignId) {
    const response = await request.get(
        `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
            headers: bearer(accessToken),
        });
    expect(response.ok()).toBeTruthy();
    return response.json();
}

async function readArtifact(request, accessToken, campaignId, artifactId) {
    const response = await request.get(
        `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts/${artifactId}`, {
            headers: bearer(accessToken),
        });
    expect(response.ok()).toBeTruthy();
    return response.json();
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
