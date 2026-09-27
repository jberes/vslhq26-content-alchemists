import { expect, signInFreshUser, test } from './fixtures.js';

// ADR-F74: every action that calls the API shows it is working — the control that started it
// pulses and ignores repeat clicks, a slim bar runs across the app, and a campaign delete covers
// the main pane. API calls are slowed here so the feedback is observable, then released.

const API = 'http://localhost:5015';

test('Buttons that start API work show it until the work finishes', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'activity');
    let campaignId = null;
    try {
        const campaign = await request.post(`${API}/api/v1/campaigns`, {
            headers, data: { name: `Activity ${Date.now()}`, brief: 'Feedback check.' },
        });
        expect(campaign.status()).toBe(201);
        campaignId = (await campaign.json()).id;
        const blog = await request.post(`${API}/api/v1/campaigns/${campaignId}/artifacts`, {
            headers,
            data: { kind: 'blog', title: 'Feedback article', contentJson: JSON.stringify({ content: { markdown: '# Feedback article\n\nBody.' } }) },
        });
        expect(blog.status()).toBe(201);

        const slow = (pattern, method) => page.route(pattern, async route => {
            if (route.request().method() !== method) return route.fallback();
            await new Promise(resolve => setTimeout(resolve, 1500));
            await route.continue();
        });
        const activityOn = () => page.evaluate(() => document.documentElement.hasAttribute('data-cm-activity'));

        // Header rename: "Saving…", disabled, and the app bar while the PUT is in flight.
        await page.goto(`/campaigns/${campaignId}/floor`);
        // Rename waits for the campaign itself (the preview), not just the rail's name.
        await expect(page.getByRole('button', { name: 'Rename campaign', exact: true })).toBeEnabled({ timeout: 30_000 });
        await slow(`**/api/v1/campaigns/${campaignId}`, 'PUT');
        await page.getByRole('button', { name: 'Rename campaign', exact: true }).click();
        await page.getByLabel('Campaign name').fill('Activity renamed');
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await expect(page.getByRole('button', { name: 'Saving…' })).toBeDisabled();
        await expect.poll(activityOn).toBe(true);
        await expect(page.locator('.cm-campaign-header__name')).toHaveText('Activity renamed');
        await expect.poll(activityOn).toBe(false);

        // Any API-backed button: Focus "Send to review" is marked busy while its call runs.
        await page.goto(`/campaigns/${campaignId}/focus`);
        const send = page.getByRole('button', { name: 'Send to review' });
        await expect(send).toBeVisible();
        await page.waitForTimeout(1500);
        await slow('**/api/v1/campaigns/*/artifacts/*/status', 'PATCH');
        await send.click();
        await expect(send).toHaveAttribute('data-cm-busy', 'true');
        await expect(send).toHaveAttribute('aria-busy', 'true');
        await expect.poll(activityOn).toBe(true);
        await expect.poll(activityOn, { timeout: 15_000 }).toBe(false);

        // Idle: nothing lights up on its own (background polling is never tied to a click).
        await page.waitForTimeout(3000);
        expect(await activityOn()).toBe(false);
        expect(await page.locator('[data-cm-busy]').count()).toBe(0);

        // Rail delete: the main pane is covered and says what is happening until it returns.
        await slow(`**/api/v1/campaigns/${campaignId}`, 'DELETE');
        const row = page.locator('.cm-rail__row', { hasText: 'Activity renamed' });
        await row.hover();
        await row.getByRole('button', { name: 'Delete Activity renamed' }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Delete campaign' }).click();
        const cover = page.locator('.cm-app__busy');
        await expect(cover).toBeVisible();
        await expect(cover).toContainText('Deleting “Activity renamed”');
        await expect(row).toHaveClass(/cm-rail__row--deleting/);
        await expect.poll(activityOn).toBe(true);
        await expect(cover).toHaveCount(0, { timeout: 30_000 });
        await expect(page.locator('.cm-rail__row', { hasText: 'Activity renamed' })).toHaveCount(0);
        campaignId = null;
    } finally {
        if (campaignId) {
            await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
        }
    }
});

async function createCampaign(request, headers, name, withBlog = false) {
    const campaign = await request.post(`${API}/api/v1/campaigns`, { headers, data: { name, brief: 'Feedback check.' } });
    expect(campaign.status()).toBe(201);
    const id = (await campaign.json()).id;
    if (withBlog) {
        const blog = await request.post(`${API}/api/v1/campaigns/${id}/artifacts`, {
            headers,
            data: { kind: 'blog', title: 'Feedback article', contentJson: JSON.stringify({ content: { markdown: '# Feedback article\n\nBody.' } }) },
        });
        expect(blog.status()).toBe(201);
    }
    return id;
}

const activityOn = page => page.evaluate(() => document.documentElement.hasAttribute('data-cm-activity'));

test('Rename waits for the campaign, and a refused save keeps the typed name', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'rename-gate');
    const name = `Rename gate ${Date.now()}`;
    let campaignId = await createCampaign(request, headers, name);
    try {
        // Hold the campaign itself: until it arrives, Rename is disabled and says why.
        let releasePreview;
        const previewHeld = new Promise(resolve => { releasePreview = resolve; });
        await page.route(`**/api/v1/campaigns/${campaignId}/preview*`, async route => {
            await previewHeld;
            await route.continue();
        });
        await page.goto(`/campaigns/${campaignId}/floor`);
        const rename = page.getByRole('button', { name: 'Rename campaign', exact: true });
        await expect(rename).toBeDisabled();
        await expect(rename).toHaveAttribute('title', 'Loading the campaign…');
        releasePreview();
        await expect(rename).toBeEnabled({ timeout: 30_000 });
        await expect(rename).toHaveAttribute('title', 'Rename campaign');

        // The server refuses the save: an error says so, the editor stays open with the typed
        // name, the controls come back, and nothing is left looking busy.
        await page.route(`**/api/v1/campaigns/${campaignId}`, route => route.request().method() === 'PUT'
            ? route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Rename failed on the server.', status: 500 }) })
            : route.fallback());
        await rename.click();
        await page.getByLabel('Campaign name').fill('Refused rename');
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await expect(page.locator('.cm-toast--error')).toBeVisible();
        await expect(page.getByLabel('Campaign name')).toHaveValue('Refused rename');
        await expect(page.getByLabel('Campaign name')).toBeEnabled();
        await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();
        await expect.poll(() => activityOn(page)).toBe(false);
        expect(await page.locator('[data-cm-busy]').count()).toBe(0);

        // Cancel leaves the real name untouched on the server.
        await page.getByRole('button', { name: 'Cancel', exact: true }).click();
        await expect(page.locator('.cm-campaign-header__name')).toHaveText(name);
        const stored = await request.get(`${API}/api/v1/campaigns/${campaignId}`, { headers });
        expect((await stored.json()).name).toBe(name);
    } finally {
        if (campaignId) await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
    }
});

test('Keyboard activation shows busy, a busy control ignores repeat clicks, and a failure clears it', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'activity-keys');
    let campaignId = await createCampaign(request, headers, `Activity keys ${Date.now()}`, true);
    try {
        const patches = [];
        let failNext = false;
        await page.route('**/api/v1/campaigns/*/artifacts/*/status', async route => {
            if (route.request().method() !== 'PATCH') return route.fallback();
            patches.push(route.request().url());
            await new Promise(resolve => setTimeout(resolve, 1500));
            if (failNext) {
                failNext = false;
                return route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Status change failed.', status: 500 }) });
            }
            await route.continue();
        });

        await page.goto(`/campaigns/${campaignId}/focus`);
        const send = page.getByRole('button', { name: 'Send to review' });
        await expect(send).toBeVisible();
        await page.waitForTimeout(1500);

        // A failure: the control is busy while the call runs, then released, with an error.
        failNext = true;
        await send.focus();
        await page.keyboard.press('Enter');
        await expect(send).toHaveAttribute('data-cm-busy', 'true');
        await expect.poll(() => activityOn(page)).toBe(true);
        // A second press on the busy control is swallowed: no second request.
        const box = await send.boundingBox();
        await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
        await expect.poll(() => activityOn(page), { timeout: 15_000 }).toBe(false);
        expect(patches).toHaveLength(1);
        await expect(send).not.toHaveAttribute('data-cm-busy', /.*/);
        await expect(send).not.toHaveAttribute('aria-busy', 'true');
        await expect(page.locator('.cm-toast--error')).toBeVisible();

        // Space activates too, and this time it succeeds.
        await send.focus();
        await page.keyboard.press('Space');
        await expect(send).toHaveAttribute('data-cm-busy', 'true');
        await expect.poll(() => activityOn(page), { timeout: 15_000 }).toBe(false);
        expect(patches).toHaveLength(2);
        expect(await page.locator('[data-cm-busy]').count()).toBe(0);
    } finally {
        if (campaignId) await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
    }
});

test('Deleting from the Campaigns page covers the main pane; a failed delete lifts it and keeps the campaign', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'index-delete');
    const kept = `Index keep ${Date.now()}`;
    const doomed = `Index doomed ${Date.now()}`;
    const keptId = await createCampaign(request, headers, kept);
    let doomedId = await createCampaign(request, headers, doomed);
    try {
        let fail = true;
        await page.route('**/api/v1/campaigns/*', async route => {
            if (route.request().method() !== 'DELETE') return route.fallback();
            await new Promise(resolve => setTimeout(resolve, 1500));
            if (fail && route.request().url().endsWith(keptId)) {
                return route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Delete failed on the server.', status: 500 }) });
            }
            await route.continue();
        });

        const deleteFromIndex = async name => {
            await page.getByRole('button', { name: `Delete campaign ${name}` }).click();
            const dialog = page.getByRole('alertdialog');
            await dialog.getByLabel(/to confirm/).fill(name);
            await dialog.getByRole('button', { name: 'Delete campaign' }).click();
        };

        await page.goto('/campaigns');
        await expect(page.getByRole('button', { name: `Delete campaign ${kept}` })).toBeAttached();
        const cover = page.locator('.cm-app__busy');

        // Failure: the cover says what is happening, then lifts; the campaign stays.
        await deleteFromIndex(kept);
        await expect(cover).toBeVisible();
        await expect(cover).toContainText(`Deleting “${kept}”`);
        await expect(cover).toHaveCount(0, { timeout: 30_000 });
        await expect(page.locator('.cm-toast--error', { hasText: `Couldn't delete ${kept}` })).toBeVisible();
        await expect(page.getByRole('button', { name: `Delete campaign ${kept}` })).toBeAttached();

        // Success: covered while the real delete runs, gone from the page and the rail after.
        fail = false;
        await deleteFromIndex(doomed);
        await expect(cover).toBeVisible();
        await expect(cover).toContainText(`Deleting “${doomed}”`);
        await expect(cover).toHaveCount(0, { timeout: 120_000 });
        await expect(page.getByRole('button', { name: `Delete campaign ${doomed}` })).toHaveCount(0);
        await expect(page.locator('.cm-rail__row', { hasText: doomed })).toHaveCount(0);
        doomedId = null;
    } finally {
        await request.delete(`${API}/api/v1/campaigns/${keptId}`, { headers, timeout: 120_000 });
        if (doomedId) await request.delete(`${API}/api/v1/campaigns/${doomedId}`, { headers, timeout: 120_000 });
    }
});
