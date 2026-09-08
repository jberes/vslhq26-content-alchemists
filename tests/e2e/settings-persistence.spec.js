import { expect, signInFreshUser, test } from './fixtures.js';

test('Settings links and write-only credentials persist and can be removed', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'settings');
    const api = 'http://localhost:5015/api/v1';
    try {
        await page.goto('/settings');
        await page.getByRole('tab', { name: /^Links/ }).click();
        await page.getByRole('button', { name: 'Add a link' }).click();
        await page.getByLabel('Link label').fill('Documentation');
        await page.getByLabel('URL', { exact: true }).fill('https://example.com/docs');
        const saved = page.waitForResponse(response => response.url().endsWith('/settings/workspace.links')
            && response.request().method() === 'PUT');
        await page.getByRole('button', { name: 'Save links' }).click();
        expect((await saved).ok()).toBeTruthy();
        await page.reload();
        await page.getByRole('tab', { name: /^Links/ }).click();
        await expect(page.getByLabel('URL', { exact: true })).toHaveValue('https://example.com/docs');
        const settings = await request.get(`${api}/settings`, { headers });
        expect(settings.ok()).toBeTruthy();
        const links = JSON.parse((await settings.json()).find(row => row.key === 'workspace.links').value);
        expect(links).toEqual([{ label: 'Documentation', url: 'https://example.com/docs' }]);

        await page.getByRole('tab', { name: /^Credentials/ }).click();
        const card = page.locator('.cm-settings__card').filter({ hasText: 'Nano Banana key (Google AI Studio)' });
        await card.getByLabel('Nano Banana key (Google AI Studio) value').fill('test-only-nonfunctional-key');
        await card.getByRole('button', { name: 'Save', exact: true }).click();
        await expect(card.getByText('STORED', { exact: true })).toBeVisible();
        await page.reload();
        await expect(card.getByLabel('Nano Banana key (Google AI Studio) value')).toHaveValue('');
        const stored = await request.get(`${api}/settings/secrets`, { headers });
        expect(stored.ok()).toBeTruthy();
        expect(await stored.text()).not.toContain('test-only-nonfunctional-key');
        await card.getByRole('button', { name: 'Remove', exact: true }).click();
        await expect(card.getByText('NOT SET', { exact: true })).toBeVisible();
        const removed = await request.get(`${api}/settings/secrets`, { headers });
        expect((await removed.json()).find(row => row.kind === 'NanoBananaKey').configured).toBe(false);

        await page.getByRole('tab', { name: /^Links/ }).click();
        await page.getByRole('button', { name: 'Remove Documentation' }).click();
        await page.getByRole('button', { name: 'Save links' }).click();
        await expect(page.getByLabel('Link label')).toHaveCount(0);
        await page.getByRole('tab', { name: /^Security/ }).click();
        await expect(page.getByRole('list', { name: 'Sign-in methods' })).toBeVisible();
        await page.getByRole('tab', { name: /^Readiness/ }).click();
        await expect(page.getByText('Text generation', { exact: true })).toBeVisible();
        await page.getByRole('tab', { name: /^Models/ }).click();
        await expect(page.getByLabel('Default image generator')).toBeVisible();
    } finally {
        await request.delete(`${api}/settings/workspace.links`, { headers });
        await request.delete(`${api}/settings/secrets/NanoBananaKey`, { headers });
    }
});