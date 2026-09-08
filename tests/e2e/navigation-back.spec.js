import { randomUUID } from 'node:crypto';
import { expect, test } from './fixtures.js';

// ADR-F69: from a content item, an image opens in the studio; the header trail names the item
// and the drawer's Back link returns to it. Real API + real client, no metered model calls:
// the slot is switched to Manual before the studio asks for a prompt preview.
test('Image studio names the owning item and Back returns to it in Focus mode', async ({ page, request }) => {
    const email = `back-navigation-${randomUUID()}@castmill.local`;
    const password = 'back-navigation-password-2026';
    let campaignId;
    let accessToken;

    try {
        const registration = await request.post('http://localhost:5015/api/v1/auth/register', {
            data: { email, password, displayName: 'Back Navigation E2E' },
        });
        expect(registration.status()).toBe(200);
        accessToken = (await registration.json()).accessToken;

        const campaign = await request.post('http://localhost:5015/api/v1/campaigns', {
            headers: bearer(accessToken),
            data: { name: `Back Navigation ${Date.now()}`, brief: null },
        });
        expect(campaign.status()).toBe(201);
        campaignId = (await campaign.json()).id;

        const artifact = await request.post(`http://localhost:5015/api/v1/campaigns/${campaignId}/artifacts`, {
            headers: bearer(accessToken),
            data: {
                kind: 'blog',
                title: 'Owning article for the image',
                contentJson: JSON.stringify({ content: { markdown: '# Owning article\n\nBody.' } }),
            },
        });
        expect(artifact.status()).toBe(201);
        const artifactId = (await artifact.json()).id;

        const slot = await request.post(`http://localhost:5015/api/v1/campaigns/${campaignId}/image-slots`, {
            headers: bearer(accessToken),
            data: { artifactId, prompt: 'a supporting figure, exactly this' },
        });
        expect(slot.status()).toBe(201);
        const slotId = (await slot.json()).id;
        const manual = await request.patch(`http://localhost:5015/api/v1/campaigns/${campaignId}/image-slots/${slotId}`, {
            headers: bearer(accessToken),
            data: { promptMode: 'Manual', prompt: 'a supporting figure, exactly this' },
        });
        expect(manual.ok()).toBeTruthy();

        await page.goto('/sign-in');
        // The dev sign-in form pre-fills the demo account asynchronously; wait for that to land
        // or it overwrites the credentials typed below and the test runs as the wrong user.
        const demoCredentials = await request.get('http://localhost:5015/api/v1/dev/demo-credentials');
        expect(demoCredentials.ok()).toBeTruthy();
        const demo = await demoCredentials.json();
        await expect(page.getByLabel('Email')).toHaveValue(demo.email);
        await page.getByLabel('Email').fill(email);
        await page.getByLabel('Password').fill(password);
        await page.getByRole('button', { name: 'Sign in' }).click();
        await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();

        await page.goto(`/campaigns/${campaignId}/focus?artifact=${artifactId}`);
        await expect(page.locator('.cm-focus__head h1')).toHaveText('Owning article for the image');
        await expect(page.locator('.cm-campaign-header__trail')).toContainText('Focus mode');
        await expect(page.locator('.cm-campaign-header__trail')).toContainText('Owning article for the image');

        await page.goto(`/campaigns/${campaignId}/images?slot=${slotId}`);
        await expect(page.locator('.cm-studio__drawer')).toBeVisible();
        await expect(page.locator('.cm-campaign-header__trail')).toContainText('Image studio');
        await expect(page.locator('.cm-campaign-header__trail')).toContainText('Owning article for the image');
        await expect(page.locator('.cm-studio__drawer textarea.cm-studio__prompt')).toHaveValue('a supporting figure, exactly this');

        await page.locator('.cm-studio__drawer-head a.cm-studio__back').click();
        await expect(page).toHaveURL(new RegExp(`/campaigns/${campaignId}/focus\\?artifact=${artifactId}$`));
        await expect(page.locator('.cm-focus__head h1')).toHaveText('Owning article for the image');

        await page.getByRole('button', { name: 'Go back' }).click();
        await expect(page).toHaveURL(new RegExp(`/campaigns/${campaignId}/images\\?slot=${slotId}$`));
        await expect(page.locator('.cm-studio__drawer')).toBeVisible();
    } finally {
        if (campaignId && accessToken) {
            await request.delete(`http://localhost:5015/api/v1/campaigns/${campaignId}`, {
                headers: bearer(accessToken),
            });
        }
    }
});

function bearer(token) {
    return { Authorization: `Bearer ${token}` };
}
