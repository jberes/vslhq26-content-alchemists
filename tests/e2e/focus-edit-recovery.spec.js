import { expect, signInFreshUser, test } from './fixtures.js';

test('Focus saves exports and preserves the draft after a failed Tech Edit', async ({ page, request }) => {
    const headers = await signInFreshUser(page, request, 'focus');
    const api = 'http://localhost:5015/api/v1';
    const created = await request.post(`${api}/campaigns`, {
        headers, data: { name: `Focus recovery ${Date.now()}`, brief: null },
    });
    expect(created.status()).toBe(201);
    const campaign = await created.json();
    const root = `${api}/campaigns/${campaign.id}`;
    try {
        const drafted = await request.post(`${root}/artifacts`, {
            headers,
            data: {
                kind: 'blog', title: 'Preserved draft',
                contentJson: JSON.stringify({ content: {
                    title: 'Preserved draft', markdown: '# Preserved draft\n\nOriginal saved body.',
                    metaDescription: 'Test draft', citations: [],
                } }),
            },
        });
        expect(drafted.status()).toBe(201);
        const artifact = await drafted.json();
        const url = `${root}/artifacts/${artifact.id}`;
        await page.goto(`/campaigns/${campaign.id}/focus?artifact=${artifact.id}`);
        const editor = page.locator('.tiptap[contenteditable="true"]');
        await expect(editor).toContainText('Original saved body.');
        await editor.fill('Edited body persisted from Playwright.');
        const saved = page.waitForResponse(response => response.url() === url && response.request().method() === 'PUT');
        await page.getByLabel('Steering', { exact: true }).click();
        expect((await saved).status()).toBe(200);
        await page.reload();
        await expect(editor).toContainText('Edited body persisted from Playwright.');
        const stored = await request.get(url, { headers });
        expect(stored.ok()).toBeTruthy();
        const before = await stored.json();
        expect(JSON.parse(before.contentJson).content.markdown).toContain('Edited body persisted from Playwright.');

        const download = page.waitForEvent('download');
        await page.getByRole('button', { name: 'Download', exact: true }).click();
        await page.getByRole('button', { name: 'Markdown (.md)', exact: true }).click();
        expect((await download).suggestedFilename()).toBe('preserved-draft.md');

        const error = 'The model returned invalid or incomplete JSON at line 1, byte 97. No changes were saved. Please retry the edit.';
        await page.route(`**/api/v1/ai/campaigns/${campaign.id}/artifacts/${artifact.id}/tech-edit`, route =>
            route.fulfill({
                status: 200, contentType: 'application/json',
                body: JSON.stringify({ success: false, error, artifactId: artifact.id,
                    version: before.version, provider: 'test', knowledgeBaseUsed: false,
                    changes: [], validationWarnings: [], durationMs: 10 }),
            }));
        const techEdit = page.getByRole('button', { name: /^Tech Edit/ });
        await expect(techEdit).toBeEnabled();
        await techEdit.click();
        await expect(page.getByText(error, { exact: true })).toBeVisible();
        await expect(techEdit).toBeEnabled();
        await expect(editor).toContainText('Edited body persisted from Playwright.');
        const after = await (await request.get(url, { headers })).json();
        expect(after.version).toBe(before.version);
        expect(after.contentJson).toBe(before.contentJson);

        const reviewed = page.waitForResponse(response => response.url() === `${url}/status`);
        await page.getByRole('button', { name: 'Send to review', exact: true }).click();
        expect((await reviewed).ok()).toBeTruthy();
        await expect(page.getByRole('button', { name: 'Mark reviewed', exact: true })).toBeVisible();
        await page.reload();
        await expect(page.getByRole('button', { name: 'Mark reviewed', exact: true })).toBeVisible();
    } finally {
        expect((await request.delete(root, { headers })).ok()).toBeTruthy();
    }
});