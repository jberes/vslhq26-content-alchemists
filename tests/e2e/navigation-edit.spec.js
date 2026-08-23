import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';

test('Mill Floor Edit opens the clicked artifact and Back keeps completed progress closed', async ({ page, request }) => {
    const email = `edit-navigation-${randomUUID()}@castmill.local`;
    const password = 'edit-navigation-password-2026';
    let campaignId;
    let accessToken;

    try {
        const registration = await request.post('http://localhost:5005/api/v1/auth/register', {
            data: { email, password, displayName: 'Edit Navigation E2E' },
        });
        expect(registration.status()).toBe(200);
        accessToken = (await registration.json()).accessToken;

        const campaign = await request.post('http://localhost:5005/api/v1/campaigns', {
            headers: bearer(accessToken),
            data: { name: `Edit Navigation ${Date.now()}`, brief: null },
        });
        expect(campaign.status()).toBe(201);
        campaignId = (await campaign.json()).id;

        const transcript = await request.post(
            `http://localhost:5005/api/v1/ai/campaigns/${campaignId}/transcripts`, {
                headers: bearer(accessToken),
                data: {
                    source: 'navigation-e2e',
                    text: 'The exact clicked article should open in Focus mode with its persisted content.',
                },
            });
        expect(transcript.status()).toBe(201);

        const artifact = await request.post(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
                headers: bearer(accessToken),
                data: {
                    kind: 'blog',
                    title: 'Exact clicked article',
                    contentJson: JSON.stringify({
                        content: { markdown: '# Exact clicked article\n\nPersisted body.' },
                    }),
                },
            });
        expect(artifact.status()).toBe(201);
        const artifactId = (await artifact.json()).id;

        await page.route(`**/api/v1/ai/campaigns/${campaignId}/generate`, route =>
            route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    runId: randomUUID(),
                    succeeded: 1,
                    failed: 0,
                    results: [{
                        kind: 'newsletter',
                        success: true,
                        artifactId: null,
                        error: null,
                        validationWarnings: [],
                        durationMs: 10,
                    }],
                }),
            }));

        await page.goto('/sign-in');
        const demoCredentials = await request.get('http://localhost:5005/api/v1/dev/demo-credentials');
        expect(demoCredentials.ok()).toBeTruthy();
        const demo = await demoCredentials.json();
        await expect(page.getByLabel('Email')).toHaveValue(demo.email);
        await expect(page.getByLabel('Password')).toHaveValue(demo.password);
        await page.getByLabel('Email').fill(email);
        await page.getByLabel('Password').fill(password);
        await page.getByRole('button', { name: 'Sign in' }).click();
        await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();

        await page.goto(`/campaigns/${campaignId}/floor`);
        await expect(page.getByText('Exact clicked article', { exact: true })).toBeVisible();
        await page.locator('.cm-print-chip', { hasText: 'Newsletter' }).click();
        await page.getByRole('button', { name: 'Print 1 newsletter' }).click();
        await expect(page.getByRole('button', { name: 'Done — back to the board' })).toBeVisible();

        await page.locator('.cm-card--lane', { hasText: 'Exact clicked article' }).hover();
        await page.getByRole('button', { name: 'Edit Exact clicked article' }).click();
        await expect(page).toHaveURL(new RegExp(`/campaigns/${campaignId}/focus\\?artifact=${artifactId}$`));
        await expect(page.locator('.cm-focus__head h1')).toHaveText('Exact clicked article');

        await page.goBack();
        await expect(page).toHaveURL(new RegExp(`/campaigns/${campaignId}/floor$`));
        await expect(page.locator('.cm-press')).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'Done — back to the board' })).toHaveCount(0);
    } finally {
        if (campaignId && accessToken) {
            await request.delete(`http://localhost:5005/api/v1/campaigns/${campaignId}`, {
                headers: bearer(accessToken),
            });
        }
    }
});

function bearer(token) {
    return { Authorization: `Bearer ${token}` };
}