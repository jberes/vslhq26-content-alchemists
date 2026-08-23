import { createHash, randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';

test('uploaded media and a recorded voice note reach analysis and Press Run', async ({ page, request }) => {
    const uploads = new Map();
    const campaignNames = [];
    await installMediaRoutes(page, uploads);
    await installAnalysisRoutes(page);
    await signIn(page, request);

    try {
        const uploadName = `Media upload E2E ${Date.now()}`;
        campaignNames.push(uploadName);
        await page.goto('/campaigns/new');
        await page.getByRole('listitem').filter({ hasText: 'Upload media' }).click();
        await page.getByLabel('Campaign name').fill(uploadName);
        await page.getByLabel('Upload audio or video').setInputFiles({
            name: 'launch.wav',
            mimeType: 'audio/wav',
            buffer: Buffer.from('RIFF deterministic test audio bytes'),
        });
        await page.getByRole('button', { name: 'Upload and transcribe' }).click();
        await expect(page.getByRole('heading', { name: 'Choose the campaign intent.' })).toBeVisible();
        await expect(page.locator('.cm-run__panel[hidden]')).toBeHidden();
        const pageOverflow = await page.locator('.cm-page').evaluate(element =>
            element.scrollHeight - element.clientHeight);
        const activeStepHeight = await page.locator('.cm-run__panel:not([hidden])').evaluate(element =>
            element.scrollHeight);
        expect(pageOverflow).toBeLessThan(activeStepHeight);
        await expect(page.getByRole('region', { name: 'Source evidence' })).toHaveCount(0);
        await page.getByRole('button', { name: 'Review transcript' }).click();
        await expect(page.getByRole('region', { name: 'Source evidence' }))
            .toContainText('Timed media evidence');
        expect(await page.locator('.cm-evidence-review__blocks').evaluate(element =>
            getComputedStyle(element).overflowY === 'auto')).toBeTruthy();
        expect(await page.locator('.cm-evidence-review__blocks').evaluate(element =>
            element.scrollHeight > element.clientHeight)).toBeTruthy();
        await page.getByRole('button', { name: 'Close source review' }).click();
        const uploadedCampaign = campaignIdFrom(page.url());
        expect(uploads.get(uploadedCampaign).checksumsValid).toBeTruthy();
        await completeAnalysisAndPressRun(page, uploadedCampaign, /^Launch/);
        await deleteCampaign(page, uploadName);
        campaignNames.shift();

        const voiceName = `Voice note E2E ${Date.now()}`;
        campaignNames.push(voiceName);
        await page.context().grantPermissions(['microphone'], { origin: 'http://localhost:5084' });
        await page.goto('/campaigns/new');
        await page.getByRole('listitem').filter({ hasText: 'Record an idea' }).click();
        await page.getByLabel('Campaign name').fill(voiceName);
        await expect(page.getByRole('region', { name: 'Voice note recorder' })).toBeVisible();
        await page.getByRole('button', { name: 'Record', exact: true }).click();
        await expect(page.getByText('Recording', { exact: true })).toBeVisible();
        await expect(page.getByRole('meter', { name: 'Microphone input level' })).toBeVisible();
        await page.waitForTimeout(1200);
        await page.getByRole('button', { name: 'Pause', exact: true }).click();
        await expect(page.getByText('Recording paused', { exact: true })).toBeVisible();
        await page.getByRole('button', { name: 'Resume', exact: true }).click();
        await page.waitForTimeout(400);
        await page.getByRole('button', { name: 'Stop', exact: true }).click();
        await expect(page.getByText('Voice note ready', { exact: true })).toBeVisible();
        await expect(page.locator('audio.cm-voice__playback')).toHaveAttribute('src', /^blob:/);
        await page.getByRole('button', { name: 'Use recording', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Choose the campaign intent.' })).toBeVisible();
        await expect(page.getByRole('region', { name: 'Source evidence' })).toHaveCount(0);
        await page.getByRole('button', { name: 'Review transcript' }).click();
        await expect(page.getByRole('region', { name: 'Source evidence' }))
            .toContainText('Timed media evidence');
        await page.getByRole('button', { name: 'Close source review' }).click();
        const voiceCampaign = campaignIdFrom(page.url());
        expect(uploads.get(voiceCampaign).contentType).toMatch(/^audio\/(webm|mp4)/);
        expect(uploads.get(voiceCampaign).totalBytes).toBeGreaterThan(0);
        await completeAnalysisAndPressRun(page, voiceCampaign, /^Capture an idea/);
        await deleteCampaign(page, voiceName);
        campaignNames.shift();
    } finally {
        for (const name of campaignNames) {
            await deleteCampaign(page, name).catch(() => {});
        }
    }
});

async function installMediaRoutes(page, uploads) {
    await page.route('**/api/v1/campaigns/*/media-uploads**', async route => {
        const request = route.request();
        const url = new URL(request.url());
        const parts = url.pathname.split('/');
        const campaignId = parts[4];
        const uploadId = parts[6];
        if (request.method() === 'POST' && parts.length === 6) {
            const body = request.postDataJSON();
            const state = {
                id: randomUUID(),
                campaignId,
                assetId: randomUUID(),
                fileName: body.fileName,
                contentType: body.contentType,
                totalBytes: body.sizeBytes,
                uploadedBytes: 0,
                nextBlockIndex: 0,
                blockSize: 4 * 1024 * 1024,
                status: 'Uploading',
                error: null,
                transcriptArtifactId: null,
                updatedAt: new Date().toISOString(),
                expiresAt: new Date(Date.now() + 86400000).toISOString(),
                checksumsValid: true,
            };
            uploads.set(campaignId, state);
            return fulfill(route, state, 201);
        }
        const state = uploads.get(campaignId);
        if (!state) return route.fulfill({ status: 404 });
        if (request.method() === 'PUT' && parts[7] === 'blocks') {
            const bytes = request.postDataBuffer();
            const expected = createHash('sha256').update(bytes).digest('hex');
            state.checksumsValid &&= request.headers()['x-content-sha256'] === expected;
            state.uploadedBytes += bytes.length;
            state.nextBlockIndex += 1;
            return fulfill(route, state);
        }
        if (request.method() === 'POST' && parts[7] === 'commit') {
            state.status = 'Committed';
            return fulfill(route, state);
        }
        if (request.method() === 'POST' && parts[7] === 'transcribe') {
            state.status = 'Completed';
            state.transcriptArtifactId = randomUUID();
            state.sourceId = randomUUID();
            state.revisionId = randomUUID();
            return fulfill(route, state);
        }
        if (request.method() === 'GET') return fulfill(route, state);
        if (request.method() === 'DELETE') {
            state.status = 'Cancelled';
            return route.fulfill({ status: 204 });
        }
        return route.fallback();
    });

    await page.route('**/api/v1/campaigns/*/sources', async route => {
        const campaignId = new URL(route.request().url()).pathname.split('/')[4];
        const state = uploads.get(campaignId);
        if (!state?.sourceId) return fulfill(route, []);
        return fulfill(route, [source(state)]);
    });
    await page.route('**/api/v1/campaigns/*/sources/*/evidence?approved=false', async route => {
        const campaignId = new URL(route.request().url()).pathname.split('/')[4];
        const state = uploads.get(campaignId);
        return state ? fulfill(route, evidence(state)) : route.fulfill({ status: 404 });
    });
}

async function installAnalysisRoutes(page) {
    await page.route('**/api/v1/ai/campaigns/*/research-context*', route =>
        fulfill(route, { audience: 'Product leaders turning recorded evidence into governed campaigns' }));
    await page.route('**/api/v1/seo/deep-analysis', route => fulfill(route, report()));
    await page.route('**/api/v1/campaigns/*/seo-targets', async route => {
        if (route.request().method() !== 'PUT') return route.fallback();
        const body = route.request().postDataJSON();
        return fulfill(route, body);
    });
    await page.route('**/api/v1/ai/campaigns/*/brief*', route => fulfill(route, {
        title: 'Recorded campaign',
        audience: 'Product leaders',
        brandVoice: null,
        angle: 'Turn one recorded source into governed channel-ready content.',
        summary: 'The recorded source provides a concise evidence-backed campaign foundation.',
        keyPoints: ['The source is timed.', 'The evidence is approved.'],
    }));
    await page.route('**/api/v1/ai/campaigns/*/generate', route => fulfill(route, {
        runId: randomUUID(), succeeded: 3, failed: 0, results: [],
    }));
}

async function completeAnalysisAndPressRun(page, campaignId, intentName) {
    await page.getByRole('radio', { name: intentName }).click();
    await expect(page.getByRole('heading', { name: 'Set the research context.' })).toBeVisible();
    await page.getByLabel(/Site URL/).fill('https://example.com');
    await page.getByRole('button', { name: 'Build the deep SEO/AEO report' }).click();
    await expect(page.getByRole('heading', { name: 'Review the deep SEO/AEO report.' })).toBeVisible();
    await page.getByRole('button', { name: 'Approve report & build content brief' }).click();
    await expect(page.getByRole('heading', { name: 'Choose the output recipe.' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Press Run' })).toBeEnabled();
    const generate = page.waitForRequest(request =>
        request.url().endsWith(`/api/v1/ai/campaigns/${campaignId}/generate`)
        && request.method() === 'POST');
    await page.getByRole('button', { name: 'Press Run' }).click();
    const request = await generate;
    expect(request.postDataJSON().transcriptArtifactId).toBeTruthy();
    await expect(page).toHaveURL(new RegExp(`/campaigns/${campaignId}/floor`));
}

async function signIn(page, request) {
    const credentials = await request.get('http://localhost:5005/api/v1/dev/demo-credentials');
    const { email, password } = await credentials.json();
    await page.goto('/sign-in');
    await expect(page.getByLabel('Email')).toHaveValue(email);
    await expect(page.getByLabel('Password')).toHaveValue(password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
}

async function deleteCampaign(page, campaignName) {
    await page.getByRole('button', { name: `Delete ${campaignName}` }).click();
    await page.getByRole('button', { name: 'Delete campaign', exact: true }).click();
    await expect(page.getByRole('button', { name: `Delete ${campaignName}` })).toHaveCount(0);
}

function campaignIdFrom(url) {
    return new URL(url).searchParams.get('campaign');
}

function fulfill(route, body, status = 200) {
    return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function source(state) {
    return {
        id: state.sourceId,
        campaignId: state.campaignId,
        legacyArtifactId: state.transcriptArtifactId,
        kind: 'transcript',
        modality: 'media',
        label: state.fileName,
        originalUri: null,
        contentType: state.contentType,
        sizeBytes: state.totalBytes,
        snapshotIdentity: 'sha256:media-e2e',
        currentEvidenceRevision: 1,
        currentEvidenceRevisionId: state.revisionId,
        approvedEvidence: {
            sourceAssetId: state.sourceId,
            revision: 1,
            revisionId: state.revisionId,
            hash: 'approved',
            approvedAt: new Date().toISOString(),
        },
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
    };
}

function evidence(state) {
    return {
        source: source(state),
        revision: 1,
        revisionId: state.revisionId,
        isApproved: true,
        blocks: Array.from({ length: 80 }, (_, ordinal) => ({
            sourceAssetId: state.sourceId,
            stableId: `s${String(ordinal + 1).padStart(2, '0')}`,
            ordinal,
            content: `Timed media evidence segment ${ordinal + 1} from the selected recording.`,
            locatorKind: 'media-time-range',
            locator: {
                startSeconds: ordinal * 2.5,
                endSeconds: (ordinal + 1) * 2.5,
                speaker: 'Host',
                sourceLabel: state.fileName,
            },
            revision: 1,
            revisionId: state.revisionId,
            approvalState: 'Approved',
            isExcluded: false,
        })),
    };
}

function report() {
    return {
        reportArtifactId: randomUUID(),
        generatedAt: new Date().toISOString(),
        research: {
            keywords: [{ term: 'recorded campaign', volume: 100, difficulty: 10,
                opportunity: 5, source: 'provider', competition: 0.2, cpc: 1, intent: 'informational' }],
            questions: [{ question: 'How do recorded sources become campaigns?', source: 'paa' }],
            hasProviderMetrics: true,
            notes: [],
            providerLookups: ['fixture/non-metered'],
        },
        serp: { keyword: 'recorded campaign', aiOverview: null, featuredSnippet: null, organicResults: [] },
        recommendations: ['Lead with governed evidence.'],
        status: 'Draft',
        siteUrl: 'https://example.com',
        insights: null,
    };
}