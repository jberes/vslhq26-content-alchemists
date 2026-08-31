import { expect, test } from '@playwright/test';

test('The Wire schedules by keyboard and drag across three projections', async ({ page, request }) => {
    let accessToken;
    let campaignId;
    let scheduleGetCount = 0;
    page.on('request', current => {
        if (current.method() === 'GET' && new URL(current.url()).pathname === '/api/v1/schedule') {
            scheduleGetCount++;
        }
    });

    try {
        const credentials = await request.get('http://localhost:5005/api/v1/dev/demo-credentials');
        expect(credentials.ok()).toBeTruthy();
        const demo = await credentials.json();

        const login = await request.post('http://localhost:5005/api/v1/auth/login', {
            data: { email: demo.email, password: demo.password },
        });
        expect(login.ok()).toBeTruthy();
        accessToken = (await login.json()).accessToken;

        const campaign = await request.post('http://localhost:5005/api/v1/campaigns', {
            headers: bearer(accessToken),
            data: { name: `Wire E2E ${Date.now()}`, brief: 'Run of Show interaction fixture.' },
        });
        expect(campaign.status()).toBe(201);
        campaignId = (await campaign.json()).id;

        await createReviewedArtifact(request, accessToken, campaignId,
            'Keyboard scheduled story with a deliberately long title that must clamp cleanly');
        await createReviewedArtifact(request, accessToken, campaignId,
            'Dragged story lands on the spatial time ruler');

        await page.setViewportSize({ width: 1440, height: 900 });
        await page.goto('/sign-in');
        await page.getByLabel('Email').fill(demo.email);
        await page.getByLabel('Password').fill(demo.password);
        await page.getByRole('button', { name: 'Sign in' }).click();
        await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();

        await page.goto('/wire');
        await expect(page.locator('.cm-run-show__timeline')).toBeVisible();
        await expect(page.locator('.cm-run-show__queue-card')).toHaveCount(2);
        await expect(page.locator('.cm-run-show__day--empty').first()).toHaveCSS('height', '30px');
        await expect(page.locator('.cm-run-show__day--weekend')).toHaveCSS('height', '30px');

        const geometry = await page.evaluate(() => {
            const timeline = document.querySelector('.cm-run-show__timeline');
            const queue = document.querySelector('.cm-run-show__queue');
            const title = document.querySelector('.cm-run-show__queue-title');
            return {
                viewport: document.documentElement.clientWidth,
                body: document.body.scrollWidth,
                timelineMinWidth: getComputedStyle(timeline).minWidth,
                queueWidth: queue.getBoundingClientRect().width,
                clamp: getComputedStyle(title).webkitLineClamp,
            };
        });
        expect(geometry.body).toBe(geometry.viewport);
        expect(geometry.timelineMinWidth).toBe('0px');
        expect(geometry.queueWidth).toBe(288);
        expect(geometry.clamp).toBe('2');

        await page.getByText('Next →', { exact: true }).click();
        await page.locator('.cm-run-show__queue-card', { hasText: 'Keyboard scheduled story' })
            .getByText('Slot', { exact: true }).click();
        await expect(page.locator('igc-dialog[open]')).toBeVisible();
        await page.locator('igc-dialog[open]').getByText('Schedule', { exact: true }).click();
        await expect(page.getByText(/Keyboard scheduled story.*staged locally/)).toBeVisible();
        await expect(page.locator('.cm-run-show__queue-card')).toHaveCount(1);

        const dragCard = page.locator('.cm-run-show__queue-card', { hasText: 'Dragged story' });
        const targetLane = page.locator('.cm-run-show__lane').filter({ hasNotText: 'collapsed' }).nth(2);
        const laneBox = await targetLane.boundingBox();
        expect(laneBox).not.toBeNull();
        await dragCard.dragTo(targetLane, {
            targetPosition: { x: Math.round(laneBox.width * 0.5), y: 12 },
        });
        const dragDialog = page.locator('igc-dialog[open]');
        if (await dragDialog.isVisible()) {
            const timeInput = dragDialog.getByRole('textbox', { name: 'HH:mm' });
            await timeInput.fill('14:00');
            await timeInput.press('Tab');
            await expect(timeInput).toHaveValue('14:00');
            await dragDialog.getByText('Schedule', { exact: true }).click();
        }
        await expect(page.getByText(/Dragged story.*staged locally/)).toBeVisible();
        await expect(page.locator('.cm-run-show__queue-card')).toHaveCount(0);
        await expect(page.locator('.cm-run-show__item', { hasText: 'Dragged story' })).toContainText('14:00');

        const scheduleRequestsBeforeSwitch = scheduleGetCount;
        await page.getByText('Agenda', { exact: true }).click();
        await expect(page.locator('.cm-agenda')).toBeVisible();
        await expect(page.locator('.cm-agenda__title', { hasText: 'Dragged story' })).toBeVisible();
        await page.getByText('Pipeline', { exact: true }).click();
        await expect(page.locator('.cm-pipeline')).toBeVisible();
        await expect(page.locator('.cm-pipeline__card', { hasText: 'Dragged story' })).toBeVisible();
        expect(scheduleGetCount).toBe(scheduleRequestsBeforeSwitch);

        await page.getByText('Agenda', { exact: true }).click();
        await page.setViewportSize({ width: 1000, height: 900 });
        await expect(page.locator('.cm-agenda--narrow')).toBeVisible();
        const runToggle = page.locator('igc-toggle-button[value="run"]');
        await expect.poll(() => runToggle.evaluate(element =>
            element.hasAttribute('disabled') || element.disabled === true)).toBeTruthy();
        await runToggle.click({ force: true });
        await expect(page.locator('.cm-agenda--narrow')).toBeVisible();

        await page.setViewportSize({ width: 1440, height: 900 });
        await page.getByText('Run of show', { exact: true }).click();
        await expect(page.locator('.cm-run-show__timeline')).toBeVisible();
        await expect(page).toHaveScreenshot('wire-run-of-show.png', {
            animations: 'disabled',
            maxDiffPixelRatio: 0.01,
        });

        const entries = await request.get('http://localhost:5005/api/v1/schedule', {
            headers: bearer(accessToken),
        });
        expect(entries.ok()).toBeTruthy();
        const scheduled = await entries.json();
        expect(scheduled.filter(entry => entry.campaignId === campaignId)).toHaveLength(2);
        expect(scheduled.every(entry => 'metrics' in entry && entry.metrics === null)).toBeTruthy();
    } finally {
        if (campaignId && accessToken) {
            await request.delete(`http://localhost:5005/api/v1/campaigns/${campaignId}`, {
                headers: bearer(accessToken),
            });
        }
    }
});

async function createReviewedArtifact(request, accessToken, campaignId, title) {
    const created = await request.post(
        `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts`, {
            headers: bearer(accessToken),
            data: {
                kind: 'social-x',
                title,
                contentJson: JSON.stringify({
                    content: { text: title, hashtags: ['Castmill'] },
                    validation: {},
                }),
            },
        });
    expect(created.status()).toBe(201);
    let artifact = await created.json();

    for (const status of ['InReview', 'Queued']) {
        const changed = await request.patch(
            `http://localhost:5005/api/v1/campaigns/${campaignId}/artifacts/${artifact.id}/status`, {
                headers: { ...bearer(accessToken), 'If-Match': `"${artifact.version}"` },
                data: { status },
            });
        expect(changed.ok()).toBeTruthy();
        artifact = await changed.json();
    }

    return artifact;
}

function bearer(token) {
    return { Authorization: `Bearer ${token}` };
}