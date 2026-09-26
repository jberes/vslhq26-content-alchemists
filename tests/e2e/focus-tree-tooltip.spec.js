import { expect, signInFreshUser, test } from './fixtures.js';

// Focus mode's outline: a long document title is clamped to two lines with an ellipsis, and a
// styled tooltip (IgbTooltip, top layer — never clipped by the scrolling outline) shows the
// whole title. A supporting row (X, LinkedIn) shows its parent document's title instead.

const API = 'http://localhost:5015';
const LONG_TITLE = 'React Data Grid Accessibility: Inspecting ARIA Labels and Keyboard Navigation in Ignite UI for React';

test('Focus outline clamps titles to two lines and shows the full title in a tooltip', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'focus-tip');
    let campaignId = null;
    try {
        const campaign = await request.post(`${API}/api/v1/campaigns`, {
            headers, data: { name: `Focus tooltip ${Date.now()}`, brief: 'Tree titles.' },
        });
        expect(campaign.status()).toBe(201);
        campaignId = (await campaign.json()).id;
        const blog = await request.post(`${API}/api/v1/campaigns/${campaignId}/artifacts`, {
            headers,
            data: { kind: 'blog', title: LONG_TITLE, contentJson: JSON.stringify({ content: { markdown: '# Post\n\nBody.' } }) },
        });
        expect(blog.status()).toBe(201);
        const blogId = (await blog.json()).id;
        const social = await request.post(`${API}/api/v1/campaigns/${campaignId}/artifacts`, {
            headers,
            data: { kind: 'social-x', title: LONG_TITLE, contentJson: JSON.stringify({ text: 'Post.' }), parentArtifactId: blogId },
        });
        expect(social.status()).toBe(201);
        const socialId = (await social.json()).id;

        await page.goto(`/campaigns/${campaignId}/focus`);
        const title = page.locator('.cm-focus__list-title', { hasText: 'React Data Grid' }).first();
        await expect(title).toBeVisible();

        // Two lines, then an ellipsis: the box is two line-heights tall and its text overflows it.
        const clamp = await title.evaluate(el => {
            const lineHeight = parseFloat(getComputedStyle(el).lineHeight) || parseFloat(getComputedStyle(el).fontSize) * 1.2;
            return { lines: Math.round(el.getBoundingClientRect().height / lineHeight), truncated: el.scrollHeight > el.clientHeight + 1 };
        });
        expect(clamp.lines).toBe(2);
        expect(clamp.truncated).toBeTruthy();

        // ONE tooltip element exists for the whole tree, and nothing opens it by itself.
        const tip = page.locator('.cm-focus__tip');
        await expect(tip).toHaveCount(1);
        await page.mouse.move(5, 5);
        await page.waitForTimeout(800);
        await expect(tip).toBeHidden();

        const blogRow = page.locator(`#cm-focus-item-${blogId.replaceAll('-', '')}`);
        const socialRow = page.locator(`#cm-focus-item-${socialId.replaceAll('-', '')}`);
        const glide = async (to, steps = 8) => {
            const b = await to.boundingBox();
            await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps });
        };

        // Hover a document row: the tooltip shows its whole title, to the row's right.
        await glide(blogRow);
        await expect(tip).toBeVisible();
        await expect(tip.locator('.cm-focus__tip-title')).toHaveText(LONG_TITLE);
        const tipBox = await tip.boundingBox();
        const rowBox = await blogRow.boundingBox();
        expect(tipBox.x).toBeGreaterThanOrEqual(rowBox.x + rowBox.width);
        expect(tipBox.width).toBeGreaterThan(200);

        // Move straight to the supporting row: the SAME tooltip now names its parent document.
        await glide(socialRow);
        await expect(tip.locator('.cm-focus__tip-kicker')).toContainText('from');
        await expect(tip.locator('.cm-focus__tip-title')).toHaveText(LONG_TITLE);
        await expect(tip).toHaveCount(1);

        // Leaving the tree closes it; clicking a row closes it too.
        await page.mouse.move(700, 300, { steps: 6 });
        await expect(tip).toBeHidden();
        await glide(blogRow);
        await expect(tip).toBeVisible();
        await blogRow.click();
        await expect(tip).toBeHidden();
        // Sweeping quickly over every row never leaves more than the one tooltip, and none after.
        for (const row of await page.locator('.cm-focus__list-item').all()) {
            await glide(row, 2);
        }
        await page.mouse.move(700, 300, { steps: 2 });
        await expect(tip).toBeHidden();
        await expect(tip).toHaveCount(1);
        await page.screenshot({ path: test.info().outputPath('focus-tooltip.png') });
    } finally {
        if (campaignId) {
            await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers });
        }
    }
});
