import { test as base, expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';

export { expect };

export async function signInFreshUser(page, request, prefix) {
    const email = `${prefix}-${randomUUID()}@example.com`;
    const password = `Browser-test-${randomUUID()}`;
    const registration = await request.post('http://localhost:5015/api/v1/auth/register', {
        data: { email, password, displayName: 'Browser Test' },
    });
    expect(registration.status()).toBe(200);
    const auth = await registration.json();
    const credentials = await request.get('http://localhost:5015/api/v1/dev/demo-credentials');
    expect(credentials.ok()).toBeTruthy();
    const demo = await credentials.json();
    await page.goto('/sign-in');
    await expect(page.getByLabel('Email')).toHaveValue(demo.email);
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill(password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
    return { Authorization: `Bearer ${auth.accessToken}` };
}

export const test = base.extend({
    page: async ({ page }, use, testInfo) => {
        await page.route('**/appsettings.Development.json', route => route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({ ApiBaseAddress: 'http://localhost:5015/' }),
        }));
        const requests = [];
        page.on('response', response => {
            const url = new URL(response.url());
            if (url.pathname.startsWith('/api/v1/') && !url.pathname.includes('/dev/')) {
                requests.push({
                    method: response.request().method(),
                    path: url.pathname,
                    status: response.status(),
                });
            }
        });
        await use(page);
        await testInfo.attach('client-api-interactions', {
            body: JSON.stringify(requests, null, 2),
            contentType: 'application/json',
        });
    },
});