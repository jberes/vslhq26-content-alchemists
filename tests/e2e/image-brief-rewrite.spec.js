import { expect, signInFreshUser, test } from './fixtures.js';

// Rewrite brief in Image Studio (real brief writer, real API). Rewriting opens the "prompt
// Castmill will send" disclosure; the browser answers with a toggle event, and a handler that
// flipped a flag on toggle closed it again — the panel flashed open/closed without end. Watched
// here in the real engine: the disclosure opens once and stays put, and a click still closes it.

const API = 'http://localhost:5015';

test('Rewrite brief opens the prompt preview once — no open/close flashing', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'brief-rewrite');
    const campaign = await request.post(`${API}/api/v1/campaigns`, {
        headers, data: { name: `Brief rewrite ${Date.now()}`, brief: 'Conversational analytics for governed dashboards.' },
    });
    expect(campaign.status()).toBe(201);
    const campaignId = (await campaign.json()).id;
    try {
        const blog = await request.post(`${API}/api/v1/campaigns/${campaignId}/artifacts`, {
            headers,
            data: {
                kind: 'blog', title: 'Conversational Analytics with Governed Queries',
                contentJson: JSON.stringify({ content: { markdown: '# Conversational Analytics\n\nAsk governed data a question in plain English and get a chart back.' } }),
            },
        });
        expect(blog.status()).toBe(201);
        const slot = await request.post(`${API}/api/v1/campaigns/${campaignId}/image-slots`, {
            headers, data: { artifactId: (await blog.json()).id },
        });
        expect(slot.status()).toBe(201);
        const slotId = (await slot.json()).id;

        await page.goto(`/campaigns/${campaignId}/images?slot=${slotId}`);
        const brief = page.getByLabel('Visual brief');
        await expect(brief).toBeVisible({ timeout: 180_000 });
        const preview = page.locator('details.cm-studio__preview');
        await expect(preview).not.toHaveAttribute('open', /.*/);

        // Count every open/close of the disclosure from here on.
        await page.evaluate(() => {
            const details = document.querySelector('details.cm-studio__preview');
            window.__cmToggles = 0;
            new MutationObserver(list => { window.__cmToggles += list.length; })
                .observe(details, { attributes: true, attributeFilter: ['open'] });
        });

        await page.getByRole('button', { name: 'Rewrite brief' }).click();
        await expect(page.getByRole('button', { name: 'Rewriting brief…' })).toBeVisible();
        await expect(page.getByRole('button', { name: 'Rewrite brief' })).toBeEnabled({ timeout: 180_000 });
        await expect(brief).not.toHaveValue('');
        await expect(preview).toHaveAttribute('open', '');

        // Settle, then prove it stays still: one open, nothing after.
        await page.waitForTimeout(3000);
        const afterRewrite = await page.evaluate(() => window.__cmToggles);
        expect(afterRewrite).toBe(1);
        await expect(preview).toHaveAttribute('open', '');

        // The producer can still close it, and it stays closed.
        await preview.locator('summary').click();
        await page.waitForTimeout(2000);
        await expect(preview).not.toHaveAttribute('open', /.*/);
        expect(await page.evaluate(() => window.__cmToggles)).toBe(2);
    } finally {
        await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
    }
});
