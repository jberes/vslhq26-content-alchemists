import { expect, signInFreshUser, test } from './fixtures.js';

// The studio layout (ADR-F77), both engines, every run, with a real render: every content item
// stays in the navigator while one slot is worked on, the take is the hero on the stage with its
// filmstrip, the inspector's tabs keep one generate dock, compare is an A/B split, the board shows
// the whole campaign, and Extract from video opens as a side sheet.

const API = 'http://localhost:5015';

test('The studio keeps the campaign in view, makes the take the hero, and keeps one generate dock', async ({ page, request }) => {
    test.setTimeout(8 * 60 * 1000);
    const headers = await signInFreshUser(page, request, 'studio-layout');
    const campaign = await request.post(`${API}/api/v1/campaigns`, { headers, data: { name: `Studio layout ${Date.now()}`, brief: 'Layout.' } });
    expect(campaign.status()).toBe(201);
    const campaignId = (await campaign.json()).id;
    try {
        const post = (kind, title, contentJson) => request.post(`${API}/api/v1/campaigns/${campaignId}/artifacts`, { headers, data: { kind, title, contentJson } });
        const blog = await post('blog', 'Governed conversational analytics', JSON.stringify({ content: { markdown: '# Governed analytics\n\nAsk the data a question.' } }));
        const social = await post('social-x', 'Ask your dashboards', JSON.stringify({ text: 'Post.' }));
        const blogSlot = await request.post(`${API}/api/v1/campaigns/${campaignId}/image-slots`, {
            headers, data: { artifactId: (await blog.json()).id, prompt: 'a lighthouse at dusk, centred, cinematic', targetWidth: 1600, targetHeight: 840 },
        });
        const blogSlotId = (await blogSlot.json()).id;
        await request.patch(`${API}/api/v1/campaigns/${campaignId}/image-slots/${blogSlotId}`, {
            headers, data: { promptMode: 'Manual', prompt: 'a lighthouse at dusk, centred, cinematic' },
        });
        await request.post(`${API}/api/v1/campaigns/${campaignId}/image-slots`, { headers, data: { artifactId: (await social.json()).id } });

        // Nothing selected: the board shows every item; Stage waits for a choice.
        await page.goto(`/campaigns/${campaignId}/images`);
        const board = page.locator('.cm-studio__board');
        await expect(board).toBeVisible();
        await expect(board.locator('.cm-studio__board-group')).toHaveCount(2);
        await expect(page.getByRole('button', { name: 'Stage', exact: true })).toBeDisabled();

        // Choose the blog image from the board: the stage opens, and BOTH items stay in the navigator.
        await board.locator('.cm-studio__card').first().click();
        const navigator = page.locator('.cm-studio__sheet');
        await expect(page.locator('.cm-studio__drawer')).toBeVisible();
        await expect(navigator.locator('.cm-studio__group')).toHaveCount(2);
        await expect(navigator).toContainText('Governed conversational analytics');
        await expect(navigator).toContainText('Ask your dashboards');

        // An empty slot offers generate and build-by-hand on the stage itself.
        await expect(page.locator('.cm-studio__stage-empty')).toContainText('No takes for');
        const dock = page.locator('.cm-studio__dock');
        const generated = page.waitForResponse(r => r.url().endsWith(`/image-slots/${blogSlotId}/generate`) && r.request().method() === 'POST', { timeout: 300_000 });
        await dock.getByRole('button', { name: /^Generate 1 variant/ }).click();
        expect((await generated).ok()).toBeTruthy();

        // The take is the hero: large on the stage, with its toolbar, and marked in the filmstrip.
        const hero = page.locator('.cm-lightbox--inline .cm-lightbox__image');
        await expect(hero).toBeVisible({ timeout: 60_000 });
        await expect.poll(async () => (await hero.boundingBox())?.width ?? 0).toBeGreaterThan(300);
        await expect(page.locator('.cm-lightbox--inline .cm-lightbox__toolbar')).toContainText('Use this image');
        await expect(page.locator('.cm-studio__film .cm-gallery__tile[aria-current="true"]')).toHaveCount(1);

        // Every inspector tab keeps the same generate dock.
        for (const [tab, marker] of [['Refine', 'Steer a new take'], ['Text', 'Text boxes'], ['Details', 'Belongs to'], ['Prompt', 'Prompt mode']]) {
            await page.getByRole('tab', { name: tab, exact: true }).click();
            await expect(page.locator('.cm-studio__panel')).toContainText(marker);
            await expect(dock).toContainText('Model');
            await expect(dock.getByRole('button', { name: /^Generate/ })).toBeVisible();
        }

        // The prompt editor always has real room: the dock never squeezes it to a sliver, at a
        // laptop size and a desktop size (reported from the desktop app).
        const expectEditorRoom = async () => {
            await page.getByRole('tab', { name: 'Prompt', exact: true }).click();
            const panel = page.locator('.cm-studio__panel');
            const editor = panel.locator('textarea.cm-studio__prompt').first();
            await editor.scrollIntoViewIfNeeded();
            const p = await panel.boundingBox();
            const e = await editor.boundingBox();
            const d = await dock.boundingBox();
            expect(p.height).toBeGreaterThanOrEqual(280);
            const visible = Math.min(e.y + e.height, p.y + p.height) - Math.max(e.y, p.y);
            expect(visible).toBeGreaterThanOrEqual(150);
            expect(Math.max(e.y, p.y)).toBeLessThan(d.y);
        };
        await expectEditorRoom();
        await page.setViewportSize({ width: 1600, height: 1000 });
        await expectEditorRoom();

        // A second take makes compare possible: an A/B split with a slider on the stage.
        const second = page.waitForResponse(r => r.url().endsWith(`/image-slots/${blogSlotId}/generate`) && r.request().method() === 'POST', { timeout: 300_000 });
        await dock.getByRole('button', { name: /^Generate 1 variant/ }).click();
        expect((await second).ok()).toBeTruthy();
        await expect(page.locator('.cm-studio__film .cm-gallery__tile')).toHaveCount(2, { timeout: 60_000 });
        await page.getByRole('button', { name: 'Compare', exact: true }).click();
        const slider = page.locator('.cm-lightbox--inline input.cm-compare-slider');
        await expect(slider).toBeVisible();
        await slider.fill('30');
        await expect(page.locator('.cm-lightbox__canvas--compare')).toHaveAttribute('style', /--cm-split: 30%/);
        await page.keyboard.press('Escape');
        await expect(slider).toHaveCount(0);

        // Board from the stage and back: the selection survives.
        await page.getByRole('button', { name: 'Board', exact: true }).click();
        await expect(board).toBeVisible();
        await page.getByRole('button', { name: 'Stage', exact: true }).click();
        await expect(page.locator('.cm-lightbox--inline')).toBeVisible();

        // Extract from video is a side sheet over the studio; Escape closes it.
        await page.getByRole('button', { name: 'Extract from video', exact: true }).click();
        const sheet = page.getByRole('dialog', { name: 'Extract from video' });
        await expect(sheet).toBeVisible();
        await expect(sheet).toContainText('Extract exact frames');
        await expect(sheet.getByRole('link', { name: 'Back to Image Studio' })).toHaveCount(0);
        await sheet.press('Escape');
        await expect(sheet).toHaveCount(0);
        await expect(page.locator('#blazor-error-ui')).toBeHidden();
    } finally {
        await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
    }
});
