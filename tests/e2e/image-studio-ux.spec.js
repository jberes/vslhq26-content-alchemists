import { expect, test } from '@playwright/test';

test('Brand asset types and Image Studio controls update in place', async ({ page, request }) => {
    let accessToken = null;
    let campaignId = null;
    let campaignName = null;
    let brandId = null;
    let assetId = null;

    try {
        await page.goto('/sign-in');
        const email = 'image-ux-e2e@castmill.local';
        const password = 'image-ux-e2e-password-2026';
        let login = await request.post('http://localhost:5005/api/v1/auth/login', {
            data: { email, password },
        });
        if (login.status() === 401) {
            login = await request.post('http://localhost:5005/api/v1/auth/register', {
                data: { email, password, displayName: 'Image UX E2E' },
            });
        }
        const loginBody = await login.text();
        expect(login.status(), loginBody).toBe(200);
        accessToken = JSON.parse(loginBody).accessToken;

        const brand = await request.post('http://localhost:5005/api/v1/brands', {
            headers: bearer(accessToken),
            data: { name: `Image UX E2E ${Date.now()}`, styleCard: { voice: 'Clear and direct.' } },
        });
        expect(brand.status()).toBe(201);
        brandId = (await brand.json()).id;

        const asset = await request.post('http://localhost:5005/api/v1/assets', {
            headers: bearer(accessToken),
            data: { fileName: 'studio-wall.png', contentType: 'image/png', sizeBytes: 128 },
        });
        expect(asset.status()).toBe(201);
        assetId = (await asset.json()).id;

        const link = await request.post(`http://localhost:5005/api/v1/brands/${brandId}/assets`, {
            headers: bearer(accessToken),
            data: { assetId, kind: 'background', label: 'Studio wall' },
        });
        expect(link.status()).toBe(201);

        campaignName = `Image UX E2E ${Date.now()}`;
        const campaign = await request.post('http://localhost:5005/api/v1/campaigns', {
            headers: bearer(accessToken),
            data: {
                name: campaignName,
                brief: 'A focused visual story.',
                brandId,
                contentType: 'Webinar',
            },
        });
        expect(campaign.status()).toBe(201);
        campaignId = (await campaign.json()).id;

        const blog = await request.post(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
                headers: bearer(accessToken),
                data: {
                    kind: 'blog',
                    title: 'Launch article',
                    contentJson: JSON.stringify({ content: { markdown: '# Launch article\n\nBody.' } }),
                },
            });
        expect(blog.status()).toBe(201);

        const youtube = await request.post(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
                headers: bearer(accessToken),
                data: {
                    kind: 'youtube',
                    title: 'Launch video package',
                    contentJson: JSON.stringify({
                        title: 'Launch video package',
                        titleOptions: [],
                        description: 'The complete launch walkthrough.',
                        chapters: [],
                        tags: [],
                    }),
                },
            });
        expect(youtube.status()).toBe(201);

        const artifact = await request.post(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
                headers: bearer(accessToken),
                data: {
                    kind: 'social-x',
                    title: 'Launch post',
                    contentJson: JSON.stringify({ text: 'A concise product launch post.' }),
                },
            });
        expect(artifact.status()).toBe(201);
        const artifactId = (await artifact.json()).id;

        const transcript = await request.post(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
                headers: bearer(accessToken),
                data: {
                    kind: 'transcript',
                    title: 'Source transcript',
                    contentJson: JSON.stringify({
                        source: 'e2e',
                        segments: [{
                            id: 's01', startSeconds: 0, endSeconds: 6, speaker: null,
                            text: 'A focused visual story for every content channel.',
                        }],
                    }),
                },
            });
        expect(transcript.status()).toBe(201);

        const summary = await request.post(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
                headers: bearer(accessToken),
                data: {
                    kind: 'campaign-summary',
                    title: 'Internal campaign summary',
                    contentJson: JSON.stringify({ summary: 'Internal strategy only.' }),
                },
            });
        expect(summary.status()).toBe(201);

        const slot = await request.post(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/image-slots`, {
                headers: bearer(accessToken),
                data: { artifactId, promptMode: 'Auto', prompt: 'Clean editorial composition' },
            });
        expect(slot.status()).toBe(201);
        const slotId = (await slot.json()).id;

        const seoReport = await request.post(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
                headers: bearer(accessToken),
                data: {
                    kind: 'seo-report',
                    title: 'SEO/AEO analysis',
                    contentJson: JSON.stringify(reportFixture()),
                },
            });
        expect(seoReport.status()).toBe(201);
        const reportArtifact = await seoReport.json();
        const submitReport = await request.patch(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts/${reportArtifact.id}/status`, {
                headers: { ...bearer(accessToken), 'If-Match': `"${reportArtifact.version}"` },
                data: { status: 'InReview' },
            });
        expect(submitReport.ok()).toBeTruthy();

        await page.getByLabel('Email').fill(email);
        await page.getByLabel('Password').fill(password);
        await page.getByRole('button', { name: 'Sign in' }).click();
        await expect(page).toHaveURL(/\/$/);

        const workspaceRail = page.locator('nav.cm-rail');
        await expect(workspaceRail.getByText('In this campaign', { exact: true })).toHaveCount(0);
        await expect(workspaceRail.locator('#cm-rail-campaigns')).toHaveText('Campaigns');
        await expect(workspaceRail.locator('.cm-rail__card')).toHaveCount(0);
        const campaignRow = workspaceRail.locator('.cm-rail__row', { hasText: campaignName });
        await expect(campaignRow).toContainText('Webinar');
        await expect(campaignRow).toContainText('Added');
        await expect(campaignRow).toContainText('Updated');
        await campaignRow.hover();
        await expect(campaignRow.locator('.cm-rail__delete')).toBeVisible();
        await expect(campaignRow.locator('.cm-rail__delete')).toContainText('🗑');

        await page.goto(`/campaigns/${campaignId}/floor`);
        await expect(page.locator('.cm-campaign-header__meta')).toContainText('Webinar');

        const renamedCampaignName = `${campaignName} renamed`;
        const renameResponse = page.waitForResponse(response =>
            response.url().endsWith(`/api/v1/campaigns/${campaignId}`)
            && response.request().method() === 'PUT');
        await page.getByRole('button', { name: 'Rename', exact: true }).click();
        await page.getByLabel('Campaign name').fill(renamedCampaignName);
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        expect((await renameResponse).ok()).toBeTruthy();
        await expect(page.locator('.cm-campaign-header__name')).toHaveText(renamedCampaignName);
        await expect(workspaceRail.locator('.cm-rail__campaign-name'))
            .toHaveText(renamedCampaignName);
        campaignName = renamedCampaignName;

        const printKinds = page.locator('.cm-print-chip');
        await expect(printKinds).toHaveCount(8);
        await expect(printKinds).toContainText([
            'Blog post', 'Social set (6)', 'YouTube package', 'Show notes',
            'Email sequence', 'Newsletter', 'Clip suggestions', 'Landing page',
        ]);
        await expect(printKinds.filter({ hasText: 'Campaign summary' })).toHaveCount(0);
        await expect(printKinds.filter({ hasText: 'SEO brief' })).toHaveCount(0);

        await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
        await page.getByRole('button', { name: 'Copy', exact: true }).click();
        await expect.poll(() => page.evaluate(() => navigator.clipboard.readText()))
            .toBe('A focused visual story for every content channel.');
        await page.getByRole('button', { name: 'View', exact: true }).click();
        await page.getByRole('button', { name: 'Copy all', exact: true }).click();
        await expect.poll(() => page.evaluate(() => navigator.clipboard.readText()))
            .toBe('A focused visual story for every content channel.');
        await page.getByRole('button', { name: 'Close', exact: true }).click();

        const fallback = await page.evaluate(async () => {
            let selectedText = '';
            const originalExecCommand = document.execCommand;
            Object.defineProperty(navigator, 'clipboard', {
                value: undefined, configurable: true,
            });
            document.execCommand = command => {
                selectedText = document.activeElement?.value ?? '';
                return command === 'copy';
            };
            try {
                const clipboard = await import('/_content/Castmill.UI/js/castmill-clipboard.js');
                return { copied: await clipboard.copyText('WebView fallback'), selectedText };
            } finally {
                document.execCommand = originalExecCommand;
                delete navigator.clipboard;
            }
        });
        expect(fallback).toEqual({ copied: true, selectedText: 'WebView fallback' });

        await page.goto(`/brands/${brandId}`);
        await page.getByRole('tab', { name: 'Asset kit' }).click();
        const typeSwitcher = page.getByLabel('Type for Studio wall');
        await page.locator('.cm-asset-card').hover();
        await typeSwitcher.selectOption('face');
        await expect(page.getByText('Face · 1')).toBeVisible();
        await expect(typeSwitcher).toHaveValue('face');

        const createTemplate = page.waitForResponse(response =>
            response.url().endsWith(`/api/v1/brands/${brandId}/templates`)
            && response.request().method() === 'POST');
        await page.getByRole('tab', { name: 'Templates' }).click();
        await createTemplate;
        const templateKind = page.locator('.cm-brand__kind');
        await expect(templateKind).toHaveValue('youtube');
        await expect(templateKind.locator('option[value="youtube"]')).toHaveText('YouTube package');
        await expect(templateKind.locator('option')).toHaveCount(13);
        await expect(templateKind.locator('option')).toHaveText([
            'YouTube package', 'Blog post', 'Show notes', 'X post', 'LinkedIn post',
            'Facebook post', 'Instagram post', 'Threads post', 'Bluesky post',
            'Email sequence', 'Newsletter', 'Clip suggestions', 'Landing page',
        ]);
        await expect(templateKind.locator('option[value="clip-suggestions"]'))
            .toHaveText('Clip suggestions');
        await expect(templateKind.locator('option[value="campaign-summary"]')).toHaveCount(0);
        await expect(templateKind.locator('option[value="seo-brief"]')).toHaveCount(0);
        const templateEditor = page.getByLabel('Template for YouTube package');
        await expect(templateEditor).toHaveAttribute('maxlength', '20000');
        const templateBox = await templateEditor.boundingBox();
        expect(templateBox.height).toBeGreaterThan(page.viewportSize().height * 0.35);

        const longYoutubeTemplate = `YOUTUBE-E2E-BEGIN\n${'semantic strategy '.repeat(460)}\nYOUTUBE-E2E-END`;
        const saveTemplate = page.waitForResponse(response =>
            response.url().includes(`/api/v1/brands/${brandId}/templates/`)
            && response.request().method() === 'PUT');
        await templateEditor.fill(longYoutubeTemplate);
        await templateEditor.blur();
        const saveTemplateResponse = await saveTemplate;
        expect(saveTemplateResponse.request().postData()).toContain('YOUTUBE-E2E-END');
        const saveTemplateBody = await saveTemplateResponse.text();
        expect(saveTemplateResponse.status(), saveTemplateBody).toBe(200);
        expect(JSON.parse(saveTemplateBody).steeringPrompt).toBe(longYoutubeTemplate);
        await page.getByRole('tab', { name: 'Asset kit' }).click();
        const storedTemplates = await request.get(
            `http://localhost:5005/api/v1/brands/${brandId}/templates?verify=${Date.now()}`, {
                headers: { ...bearer(accessToken), 'Cache-Control': 'no-cache' },
            });
        expect(storedTemplates.ok()).toBeTruthy();
        const storedYoutube = (await storedTemplates.json())
            .find(item => item.kind === 'youtube' && item.isDefault);
        expect(storedYoutube.steeringPrompt).toBe(longYoutubeTemplate);

        await page.goto('/settings');
        await page.getByRole('tab', { name: 'Models', exact: true }).click();
        const defaultModel = page.getByLabel('Default image generator');
        const firstReadyModel = defaultModel.locator('option:not([disabled])').first();
        const defaultAlias = await firstReadyModel.getAttribute('value');
        expect(defaultAlias).toBeTruthy();
        await defaultModel.selectOption(defaultAlias);
        const saveDefault = page.waitForResponse(response =>
            response.url().endsWith('/api/v1/settings/images.default-model')
            && response.request().method() === 'PUT');
        await page.getByRole('button', { name: 'Save default' }).click();
        const savedDefault = await saveDefault;
        expect(savedDefault.ok()).toBeTruthy();
        expect(savedDefault.request().postDataJSON().value).toBe(defaultAlias);
        await expect(page.locator('.cm-settings__default-model')).toContainText('SAVED');

        let previewRequests = 0;
        page.on('request', current => {
            if (current.method() === 'GET'
                && current.url().endsWith(`/api/v1/campaigns/${campaignId}/preview`)) {
                previewRequests += 1;
            }
        });

        // Exercise take-state UX without spending against an image model. The browser still
        // drives the real authenticated client, drawer, dialog and PATCH reconciliation; only
        // the metered image pixels are represented by a deterministic fixture.
        const takeId = crypto.randomUUID();
        let takeState = 'Candidate';
        const takeFixture = () => ({
            id: takeId,
            slotId,
            url: 'http://localhost:5084/favicon.png',
            thumbUrl: 'http://localhost:5084/favicon.png',
            model: 'gpt-image-2',
            state: takeState,
            steeringNote: null,
            sourceVariantId: null,
            width: 1280,
            height: 720,
            createdAt: new Date().toISOString(),
        });
        await page.route(url =>
            url.pathname.endsWith(`/api/v1/campaigns/${campaignId}/image-slots/${slotId}/variants`)
            && url.searchParams.get('includeDiscarded')?.toLowerCase() === 'true',
        async route => route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify([takeFixture()]),
        }));
        await page.route(`**/api/v1/campaigns/${campaignId}/image-slots/${slotId}/variants/${takeId}`,
            async route => {
                takeState = route.request().postDataJSON().state;
                await route.fulfill({
                    status: 200,
                    contentType: 'application/json',
                    body: JSON.stringify(takeFixture()),
                });
            });
        await page.goto(`/campaigns/${campaignId}/images`);
        await expect(page.getByRole('tab', { name: 'Image studio' }))
            .toHaveAttribute('aria-selected', 'true');
        await expect(page.locator('.cm-studio__content-title', { hasText: 'Launch post' }))
            .toBeVisible();
        await expect(page.getByText('Internal campaign summary', { exact: true })).toHaveCount(0);

        // ADR-F43: the sheet opens with the drawer closed — coverage first, editor on demand.
        await expect(page.locator('.cm-studio__drawer')).toHaveCount(0);
        await page.locator('.cm-studio__card:not(.cm-studio__card--add)').first().click();
        await expect(page.locator('.cm-studio__drawer')).toBeVisible();
        await expect(page).toHaveURL(new RegExp(`slot=${slotId}`));
        await expect(page.locator('.cm-studio__context'))
            .toContainText('A concise product launch post.');
        const loadedPreviewRequests = previewRequests;

        const compactModel = page.locator('.cm-studio__models');
        await expect(compactModel).toContainText('DEFAULT');
        await expect(compactModel.locator('input[type="radio"]')).toHaveCount(0);
        await compactModel.getByRole('button', { name: 'Change…' }).click();
        await expect(page.locator('.cm-modelpicker')).toBeVisible();
        await expect(page.locator('.cm-modelpicker__choice').first())
            .toContainText('Workspace default');
        await page.locator('.cm-modelpicker').getByRole('button', { name: 'Cancel' }).click();

        await expect(page.locator('.cm-gallery__tile')).toHaveCount(1);
        await expect(page.getByRole('button', { name: 'Show discarded takes' })).toHaveCount(0);
        await page.locator('.cm-gallery__tile').click();
        await page.getByRole('button', { name: 'Mark as keeper' }).click();
        await page.getByRole('button', { name: 'Close', exact: true }).click();
        await expect(page.locator('.cm-gallery__tile')).toHaveClass(/cm-gallery__tile--keeper/);
        await expect(page.locator('.cm-gallery__keeper')).toHaveText('✓ Keeper');

        const manual = page.getByRole('button', { name: /Manual Use this prompt verbatim/ });
        const patch = page.waitForResponse(response =>
            response.url().endsWith(`/api/v1/campaigns/${campaignId}/image-slots/${slotId}`)
            && response.request().method() === 'PATCH');
        await manual.click();
        await patch;
        await expect(manual).toHaveAttribute('aria-pressed', 'true');
        await expect(page.locator('.cm-studio__prompt')).toBeEnabled();
        await expect.poll(() => previewRequests).toBe(loadedPreviewRequests);

        const constraint = page.locator('.cm-studio__chips .cm-studio__chip').first();
        await constraint.click();
        await expect(constraint).toHaveAttribute('aria-pressed', 'true');
        await expect(constraint).toHaveClass(/cm-studio__chip--on/);
        await expect(constraint).toContainText('Applied');

        await page.goto(`/campaigns/${campaignId}/seo`);
        await expect(page.locator('.cm-aeo-tab')).toHaveCount(2);
        await expect(page.locator('.cm-aeo-markdown h2')).toHaveText('Recommended answer');
        await page.locator('.cm-aeo-tab').nth(1).click();
        await expect(page.locator('.cm-aeo-markdown strong')).toContainText('Gemini answer');

        // Operational reports never appear as home-page edit work and a stale direct link
        // falls back to real content instead of rendering report JSON as a manuscript.
        await page.goto('/');
        await expect(page.getByText('SEO/AEO analysis', { exact: true })).toHaveCount(0);

        // Entering Focus without an artifact deep link always opens the first visible row,
        // using the same lane order as the rail rather than API insertion order.
        await page.goto(`/campaigns/${campaignId}/focus`);
        await expect(page.locator('.cm-focus__head h1')).toHaveText('Launch video package');
        await expect(page.locator('.cm-focus__list-item').first())
            .toContainText('Launch video package');
        await expect(page.locator('.cm-focus__list-item').first())
            .toHaveAttribute('aria-current', 'true');

        const staleReportUrl = `/campaigns/${campaignId}/focus?artifact=${reportArtifact.id}`;
        await page.evaluate(url => {
            history.pushState(null, '', url);
            window.dispatchEvent(new PopStateEvent('popstate'));
        }, staleReportUrl);
        await expect(page).toHaveURL(new RegExp(`${campaignId}/focus\\?artifact=${reportArtifact.id}`));
        await expect(page.locator('.cm-focus__head h1')).toHaveText('Launch video package');
        await expect(page.locator('.cm-focus__category')).toHaveCount(3);
        await expect(page.locator('.cm-focus__category').nth(0)).toContainText('YouTube');
        await expect(page.locator('.cm-focus__category').nth(1)).toContainText('Blog');
        await expect(page.locator('.cm-focus__category').nth(2)).toContainText('Social');
        await expect(page.getByText('Campaign-wide', { exact: true })).toHaveCount(0);
        await expect(page.locator('.cm-focus__category button')).toHaveCount(0);
        await expect(page.locator('.cm-tree__delete').first()).toContainText('🗑');

        await page.locator('.cm-focus__list-item', { hasText: 'Launch post' }).click();
        await expect(page.locator('.cm-focus__head h1')).toHaveText('Launch post');
    } finally {
        if (accessToken && campaignId) {
            await request.delete(`http://localhost:5005/api/v1/campaigns/${campaignId}`, {
                headers: bearer(accessToken),
            });
        }
        if (accessToken && brandId) {
            await request.delete(`http://localhost:5005/api/v1/brands/${brandId}`, {
                headers: bearer(accessToken),
            });
        }
        if (accessToken && assetId) {
            await request.delete(`http://localhost:5005/api/v1/assets/${assetId}`, {
                headers: bearer(accessToken),
            });
        }
    }
});

function bearer(accessToken) {
    return { Authorization: `Bearer ${accessToken}` };
}

function reportFixture() {
    const generatedAt = new Date().toISOString();
    return {
        reportArtifactId: crypto.randomUUID(),
        generatedAt,
        research: {
            keywords: [{
                term: 'product launch visuals', volume: 1400, difficulty: 28,
                opportunity: 36.8, source: 'provider', competition: 0.31,
                cpc: 2.4, intent: 'commercial',
            }],
            questions: [{ question: 'How should launch visuals be composed?', source: 'paa' }],
            hasProviderMetrics: true,
            notes: [],
            providerLookups: ['fixture/non-metered'],
        },
        serp: {
            keyword: 'product launch visuals',
            aiOverview: 'Clear, well-fitted visual hierarchy improves comprehension.',
            featuredSnippet: 'Keep the complete message inside a generous safe area.',
            organicResults: [{
                rank: 1, title: 'Launch visual guide', url: 'https://example.com/guide',
                domain: 'example.com', description: 'A practical composition guide.',
            }],
        },
        recommendations: ['Keep essential copy inside the visual safe area.'],
        status: 'Draft',
        siteUrl: 'https://example.com',
        campaignBrief: 'A focused visual story.',
        insights: {
            aeo: {
                visibilityPercent: 50,
                enginesSucceeded: 2,
                enginesCitingDomain: 1,
                engines: [{
                    provider: 'chat_gpt', label: 'ChatGPT', succeeded: true,
                    domainCited: true,
                    answer: '## Recommended answer\n\n- Keep the hierarchy concise.\n- Fit every element inside the frame.',
                    citations: [{
                        title: 'Example guide', url: 'https://example.com/guide',
                        domain: 'example.com', isOwnDomain: true,
                    }],
                }, {
                    provider: 'gemini', label: 'Gemini', succeeded: true,
                    domainCited: false,
                    answer: '**Gemini answer** with a second evidence-led framing.',
                    citations: [],
                }],
            },
            keywordGaps: [], rankedKeywords: [], siteAuthority: null, competitors: [],
            contentAngles: [], sections: [], anglesGeneratedAt: generatedAt,
        },
        inputsStale: false, anglesStale: false, shareStale: false, sharedAt: null,
    };
}
