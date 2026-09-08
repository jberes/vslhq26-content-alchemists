import { randomUUID } from 'node:crypto';
import { expect, test } from './fixtures.js';

// Runs as a freshly registered user, not the demo account: the queue counts and the visual
// snapshot below assume an otherwise empty workspace, and the demo account carries real work.
test('The Wire schedules by keyboard and drag across three projections', async ({ page, request }) => {
    const email = `wire-${randomUUID()}@castmill.local`;
    const password = 'wire-workflow-password-2026';
    let accessToken;
    let campaignId;
    let scheduleGetCount = 0;
    page.on('request', current => {
        if (current.method() === 'GET' && new URL(current.url()).pathname === '/api/v1/schedule') {
            scheduleGetCount++;
        }
    });

    try {
        const credentials = await request.get('http://localhost:5015/api/v1/dev/demo-credentials');
        expect(credentials.ok()).toBeTruthy();
        const demo = await credentials.json();

        const registration = await request.post('http://localhost:5015/api/v1/auth/register', {
            data: { email, password, displayName: 'Wire E2E' },
        });
        expect(registration.status()).toBe(200);
        accessToken = (await registration.json()).accessToken;

        const campaign = await request.post('http://localhost:5015/api/v1/campaigns', {
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
        // Wait for the dev form's demo pre-fill to land before typing, or it overwrites the input.
        await expect(page.getByLabel('Email')).toHaveValue(demo.email);
        await page.getByLabel('Email').fill(email);
        await page.getByLabel('Password').fill(password);
        await page.getByRole('button', { name: 'Sign in' }).click();
        await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();

        await page.goto('/wire');
        await expect(page.locator('.cm-run-show__timeline')).toBeVisible();
        await expect(page.locator('.cm-run-show__queue-card')).toHaveCount(2);
        // Every day is a full lane (--cm-wire-day-min), empty or weekend included (backlog 2026-09-05).
        await expect(page.locator('.cm-run-show__day--empty').first()).toHaveCSS('height', '72px');
        await expect(page.locator('.cm-run-show__day--weekend').first()).toHaveCSS('height', '72px');

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

        const previousWeek = await page.getByRole('heading', { name: /^Week of / }).textContent();
        await page.getByText('Next →', { exact: true }).click();
        await expect(page.getByRole('heading', { name: /^Week of / })).not.toHaveText(previousWeek);
        // Card actions are icon buttons revealed on hover (no worded buttons inside cards).
        const keyboardCard = page.locator('.cm-run-show__queue-card', { hasText: 'Keyboard scheduled story' });
        await keyboardCard.hover();
        await keyboardCard.getByRole('button', { name: 'Slot' }).click();
        await expect(page.locator('igc-dialog[open]')).toBeVisible();
        await page.locator('igc-dialog[open]').getByText('Schedule', { exact: true }).click();
        // Assert the durable outcome, not the transient toast: the card leaves the queue and the
        // item appears on the timeline (the toast auto-dismisses and races a slow first paint).
        await expect(page.locator('igc-dialog[open]')).toHaveCount(0);
        // The schedule POST round-trips Azure SQL, which can take 10s+ when it is warming up.
        await expect(page.locator('.cm-run-show__queue-card')).toHaveCount(1, { timeout: 90_000 });
        await expect(page.locator('.cm-run-show__item', { hasText: 'Keyboard scheduled story' })).toBeVisible();

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
        await expect(page.locator('.cm-run-show__queue-card')).toHaveCount(0, { timeout: 90_000 });
        await expect(page.locator('.cm-run-show__item', { hasText: 'Dragged story' })).toBeVisible();
        await expect(page.locator('.cm-run-show__item', { hasText: 'Dragged story' })).toContainText('14:00');

        // Two projections (Run of show, Pipeline) over ONE data set: switching never refetches.
        const scheduleRequestsBeforeSwitch = scheduleGetCount;
        await page.getByText('Pipeline', { exact: true }).click();
        await expect(page.locator('.cm-pipeline')).toBeVisible();
        await expect(page.locator('.cm-pipeline__card', { hasText: 'Dragged story' })).toBeVisible();
        await expect(page.locator('.cm-pipeline__card', { hasText: 'Keyboard scheduled story' })).toBeVisible();
        expect(scheduleGetCount).toBe(scheduleRequestsBeforeSwitch);

        await page.getByText('Run of show', { exact: true }).click();
        await expect(page.locator('.cm-run-show__timeline')).toBeVisible();
        await expect(page).toHaveScreenshot('wire-run-of-show.png', {
            animations: 'disabled',
            maxDiffPixelRatio: 0.01,
        });

        const entries = await request.get('http://localhost:5015/api/v1/schedule', {
            headers: bearer(accessToken),
        });
        expect(entries.ok()).toBeTruthy();
        const scheduled = await entries.json();
        expect(scheduled.filter(entry => entry.campaignId === campaignId)).toHaveLength(2);
        expect(scheduled.every(entry => 'metrics' in entry && entry.metrics === null)).toBeTruthy();
    } finally {
        if (campaignId && accessToken) {
            await request.delete(`http://localhost:5015/api/v1/campaigns/${campaignId}`, {
                headers: bearer(accessToken),
            });
        }
    }
});

async function createReviewedArtifact(request, accessToken, campaignId, title) {
    const created = await request.post(
        `http://localhost:5015/api/v1/campaigns/${campaignId}/artifacts`, {
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
            `http://localhost:5015/api/v1/campaigns/${campaignId}/artifacts/${artifact.id}/status`, {
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