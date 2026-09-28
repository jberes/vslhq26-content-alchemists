import { expect, signInFreshUser, test } from './fixtures.js';

// Nano Banana, end to end through Google (both engines, every run). The fresh user's key is a
// deliberately invalid one, so Google refuses in milliseconds at no cost — which still proves
// the whole path: the picker offers gemini-3-pro-image, the render goes to Google's
// gemini-3-pro-image model, and the provider's real reason is the message the producer sees
// (it used to be "No takes came back. Try again, or a simpler prompt.").

const API = 'http://localhost:5015';

test('Nano Banana renders on Gemini 3 Pro Image and a refusal says why', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'nano-banana');
    const stored = await request.put(`${API}/api/v1/settings/secrets/NanoBananaKey`, {
        headers, data: { value: 'AIzaSy-castmill-e2e-deliberately-invalid-key' },
    });
    expect(stored.ok()).toBeTruthy();
    const campaign = await request.post(`${API}/api/v1/campaigns`, {
        headers, data: { name: `Nano Banana ${Date.now()}`, brief: 'Nano Banana path.' },
    });
    expect(campaign.status()).toBe(201);
    const campaignId = (await campaign.json()).id;
    try {
        const blog = await request.post(`${API}/api/v1/campaigns/${campaignId}/artifacts`, {
            headers,
            data: { kind: 'blog', title: 'Governed conversational analytics', contentJson: JSON.stringify({ content: { markdown: '# Governed analytics\n\nBody.' } }) },
        });
        const slot = await request.post(`${API}/api/v1/campaigns/${campaignId}/image-slots`, {
            headers, data: { artifactId: (await blog.json()).id, prompt: 'a lighthouse at dusk, centred' },
        });
        const slotId = (await slot.json()).id;
        const manual = await request.patch(`${API}/api/v1/campaigns/${campaignId}/image-slots/${slotId}`, {
            headers, data: { promptMode: 'Manual', prompt: 'a lighthouse at dusk, centred' },
        });
        expect(manual.ok()).toBeTruthy();

        // The model list says which Google model Nano Banana is.
        const status = await request.get(`${API}/api/v1/ai/status`, { headers });
        const nano = (await status.json()).imageProviders.find(p => p.name === 'nano-banana');
        expect(nano.model).toBe('gemini-3-pro-image');
        expect(nano.ready).toBe(true);

        await page.goto(`/campaigns/${campaignId}/images?slot=${slotId}`);
        const drawer = page.locator('.cm-studio__drawer');
        await expect(drawer).toBeVisible();
        await drawer.getByRole('button', { name: /^Change/ }).click();
        const picker = page.getByRole('dialog', { name: 'Choose a model' });
        const choice = picker.locator('label.cm-modelpicker__choice', { hasText: 'Google Gemini' });
        await expect(choice).toContainText('gemini-3-pro-image');
        await choice.locator('input[type=radio]').check();
        await picker.getByRole('button', { name: 'Use model' }).click();
        await expect(picker).toHaveCount(0);
        await expect(drawer).toContainText('gemini-3-pro-image');

        const generated = page.waitForResponse(r => r.url().endsWith(`/image-slots/${slotId}/generate`) && r.request().method() === 'POST', { timeout: 120_000 });
        await drawer.getByRole('button', { name: /^Generate 1 variant/ }).click();
        const body = await (await generated).json();
        expect(body.variants).toHaveLength(0);
        expect(body.failures).toHaveLength(1);
        // Google's own answer, from the gemini-3-pro-image model.
        expect(body.failures[0]).toContain("'nano-banana' (gemini-3-pro-image)");
        expect(body.failures[0]).toMatch(/API key not valid|API_KEY_INVALID/i);

        const alert = drawer.locator('p.cm-form__error[role="alert"]');
        await expect(alert).toContainText('No takes came back.');
        await expect(alert).toContainText('gemini-3-pro-image');
        await expect(alert).toContainText(/API key not valid/i);
        await expect(drawer).not.toContainText('simpler prompt');
    } finally {
        await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers, timeout: 120_000 });
        await request.delete(`${API}/api/v1/settings/secrets/NanoBananaKey`, { headers });
    }
});
