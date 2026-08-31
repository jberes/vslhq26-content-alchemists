import { expect, test } from '@playwright/test';

const sourceId = '92900000-0000-0000-0000-000000000001';
const revisionOneId = '92900000-0000-0000-0000-000000000002';
const revisionTwoId = '92900000-0000-0000-0000-000000000003';

test.describe('webpage Start a Run', () => {
    test('reviews metadata and requires approval before a page intent', async ({ page, request }) => {
        let campaignId = null;
        let campaignName = null;
        let accessToken = null;
        const now = new Date().toISOString();

        await page.route('**/api/v1/campaigns/*/sources/import/webpage', async route => {
            campaignId = campaignIdFrom(route.request().url());
            await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(
                revision(campaignId, now, 1, revisionOneId, true, false)) });
        });
        await page.route('**/api/v1/campaigns/*/sources/*/evidence/web-0001', async route => {
            await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(
                revision(campaignId, now, 2, revisionTwoId, false, true)) });
        });
        await page.route('**/api/v1/campaigns/*/sources/*/evidence/2/approve', async route => {
            await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(
                revision(campaignId, now, 2, revisionTwoId, true, true)) });
        });

        try {
            accessToken = await signIn(page, request);
            await page.goto('/campaigns/new');
            await page.getByRole('listitem').filter({ hasText: 'Import webpage' }).click();
            campaignName = `Web evidence E2E ${Date.now()}`;
            await page.getByLabel('Campaign name').fill(campaignName);
            await page.getByLabel('Public webpage URL').fill('https://example.com/article');
            await page.getByRole('button', { name: 'Import webpage' }).click();

            await expect(page.getByRole('heading', { name: 'Choose the campaign intent.' })).toBeVisible();
            await expect(page.getByRole('complementary', { name: 'Imported source summary' })).toBeVisible();
            await expect(page.getByRole('region', { name: 'Source evidence' })).toHaveCount(0);
            await page.getByRole('button', { name: 'Review source' }).click();
            await expect(page.getByRole('dialog', { name: 'Review source evidence' })).toBeVisible();
            await expect(page.getByRole('region', { name: 'Source evidence' })).toBeVisible();
            await expect(page.getByText('Canonical URL', { exact: true })).toBeVisible();
            await expect(page.getByRole('link', { name: 'Open eligible image' }))
                .toHaveAttribute('href', 'https://example.com/hero.webp');
            await expect(page.locator('.cm-evidence-review img')).toHaveCount(0);
            await expect(page.getByRole('radio')).toHaveCount(2);
            await expect(page.getByRole('radio', { name: /^Repurpose this page/ })).toBeVisible();
            await expect(page.getByRole('radio', { name: /^Promote or expand this page/ })).toBeVisible();

            const bodyBlock = page.locator('.cm-evidence-block').filter({ hasText: 'Measured result' });
            await bodyBlock.getByRole('button', { name: 'Exclude' }).click();
            const review = page.getByRole('dialog', { name: 'Review source evidence' });
            await expect(review.getByText('Draft r2')).toBeVisible();
            await expect(page.getByRole('radio').first()).toBeDisabled();
            await page.getByRole('button', { name: 'Approve revision 2' }).click();
            await expect(review.getByText('Approved', { exact: true })).toBeVisible();
            await expect(page.getByRole('radio').first()).toBeEnabled();
            await page.getByRole('button', { name: 'Close source review' }).click();
            await expect(page.getByRole('dialog', { name: 'Review source evidence' })).toHaveCount(0);

            const layout = await page.evaluate(() => ({
                viewport: document.documentElement.clientWidth,
                scrollWidth: document.documentElement.scrollWidth,
            }));
            expect(layout.scrollWidth).toBe(layout.viewport);

            await deleteCampaign(request, accessToken, campaignId);
            campaignId = null;
            campaignName = null;
            await page.unroute('**/api/v1/campaigns/*/sources/import/webpage');
            await page.route('**/api/v1/campaigns/*/sources/import/webpage', async route => {
                campaignId = campaignIdFrom(route.request().url());
                await route.fulfill({
                    status: 400,
                    contentType: 'application/problem+json',
                    body: JSON.stringify({
                        title: 'Bad Request',
                        status: 400,
                        detail: 'This page renders its content with JavaScript. Castmill captured the server HTML but found no readable text; import a server-rendered page or paste its content instead.',
                    }),
                });
            });

            await page.goto('/campaigns/new');
            await page.getByRole('listitem').filter({ hasText: 'Import webpage' }).click();
            campaignName = `JS shell E2E ${Date.now()}`;
            await page.getByLabel('Campaign name').fill(campaignName);
            await page.getByLabel('Public webpage URL').fill('https://example.com/application');
            await page.getByRole('button', { name: 'Import webpage' }).click();

            await expect(page.getByRole('alert')).toContainText('renders its content with JavaScript');
            await expect(page.getByRole('alert')).toContainText('paste its content instead');
            await expect(page.getByRole('button', { name: 'Back' })).toBeVisible();
            await deleteCampaign(request, accessToken, campaignId);
            campaignId = null;
            campaignName = null;
        } finally {
            if (campaignId && accessToken) {
                await deleteCampaign(request, accessToken, campaignId).catch(() => {});
            }
        }
    });
});

async function signIn(page, request) {
    const credentials = await request.get('http://localhost:5005/api/v1/dev/demo-credentials');
    expect(credentials.ok()).toBeTruthy();
    const { email, password } = await credentials.json();

    await page.goto('/sign-in');
    await expect(page.getByLabel('Email')).toHaveValue(email);
    await expect(page.getByLabel('Password')).toHaveValue(password);
    const loginResponse = page.waitForResponse(response =>
        response.url().endsWith('/api/v1/auth/login')
        && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Sign in' }).click();
    const login = await loginResponse;
    expect(login.status()).toBe(200);
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();

    const apiLogin = await request.post('http://localhost:5005/api/v1/auth/login', {
        data: { email, password },
    });
    expect(apiLogin.ok()).toBeTruthy();
    return (await apiLogin.json()).accessToken;
}

async function deleteCampaign(request, accessToken, campaignId) {
    const response = await request.delete(`http://localhost:5005/api/v1/campaigns/${campaignId}`, {
        headers: { Authorization: `Bearer ${accessToken}` },
    });
    expect(response.ok()).toBeTruthy();
}

function campaignIdFrom(url) {
    return new URL(url).pathname.split('/')[4];
}

function revision(campaignId, now, revisionNumber, revisionId, approved, excluded) {
    const approvedEvidence = approved
        ? { sourceAssetId: sourceId, revision: revisionNumber, revisionId, hash: 'approved', approvedAt: now }
        : { sourceAssetId: sourceId, revision: 1, revisionId: revisionOneId, hash: 'approved', approvedAt: now };
    return {
        source: {
            id: sourceId,
            campaignId,
            legacyArtifactId: null,
            kind: 'webpage',
            modality: 'web',
            label: 'Measured launch guide',
            originalUri: 'https://example.com/article',
            contentType: 'text/html',
            sizeBytes: 2048,
            snapshotIdentity: 'sha256:e2e',
            currentEvidenceRevision: revisionNumber,
            currentEvidenceRevisionId: revisionId,
            approvedEvidence,
            createdAt: now,
            updatedAt: now,
        },
        revision: revisionNumber,
        revisionId,
        isApproved: approved,
        blocks: [
            block('metadata-canonical', 0, 'Canonical URL: https://example.com/article',
                'webpage-metadata', { url: 'https://example.com/article', field: 'canonical', label: 'Canonical URL' },
                revisionNumber, revisionId, approved, false),
            block('image-0001', 1, 'Eligible image: Launch dashboard',
                'webpage-image', { url: 'https://example.com/hero.webp', alt: 'Launch dashboard', width: 1200, height: 630 },
                revisionNumber, revisionId, approved, false),
            block('web-0001', 2, 'Measured result: launch review time fell by forty percent.',
                'webpage-section', { url: 'https://example.com/article', heading: 'Measured result', element: 'p', ordinal: 1 },
                revisionNumber, revisionId, approved, excluded),
        ],
    };
}

function block(stableId, ordinal, content, locatorKind, locator, revisionNumber, revisionId, approved, excluded) {
    return {
        sourceAssetId: sourceId,
        stableId,
        ordinal,
        content,
        locatorKind,
        locator,
        revision: revisionNumber,
        revisionId,
        approvalState: approved ? 'Approved' : 'Draft',
        isExcluded: excluded,
    };
}