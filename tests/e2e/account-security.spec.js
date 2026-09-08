import { randomUUID } from 'node:crypto';
import { expect, test } from './fixtures.js';

test('registration password change and sign out round trip through the real API', async ({ page, request }) => {
    const email = `account-e2e-${randomUUID()}@example.com`;
    const password = `Original-${randomUUID()}`;
    const replacement = `Replacement-${randomUUID()}`;
    await page.goto('/settings/password');
    await expect(page).toHaveURL(/sign-in/);
    await page.goto('/register');
    await page.getByLabel('Your name').fill('Account E2E');
    await page.getByLabel('Email', { exact: true }).fill(email);
    await page.getByLabel(/^Password/).fill(password);
    const registered = page.waitForResponse(response => response.url().endsWith('/api/v1/auth/register'));
    await page.getByRole('button', { name: 'Create account', exact: true }).click();
    expect((await registered).status()).toBe(200);
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();

    await page.goto('/settings/password');
    await page.getByLabel('Current password').fill('incorrect-current-password');
    await page.getByLabel('New password').fill(replacement);
    await page.getByRole('button', { name: 'Change password', exact: true }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await page.getByLabel('Current password').fill(password);
    await page.getByRole('button', { name: 'Change password', exact: true }).click();
    await expect(page.getByLabel('Current password')).toHaveValue('');
    await expect(page.getByLabel('New password')).toHaveValue('');
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/sign-in/);

    const credentials = await request.get('http://localhost:5015/api/v1/dev/demo-credentials');
    expect(credentials.ok()).toBeTruthy();
    const demo = await credentials.json();
    await expect(page.getByLabel('Email')).toHaveValue(demo.email);
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill(password);
    const rejected = page.waitForResponse(response => response.url().endsWith('/api/v1/auth/login'));
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    expect((await rejected).status()).toBe(401);
    await expect(page.getByRole('alert')).toBeVisible();
    await page.getByLabel('Password').fill(replacement);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/sign-in/);
    await page.goto('/settings/security');
    await expect(page).toHaveURL(/sign-in/);
});