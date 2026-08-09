import { expect, test } from '@playwright/test';

test('the signed-out default entry point is the login form', async ({ page }) => {
    await page.goto('/');

    await expect(page).toHaveURL(/\/sign-in(?:\?|$)/);
    await expect(page.getByRole('heading', { name: 'Sign in.' })).toBeVisible();
    await expect(page.getByLabel('Email')).toBeVisible();
    await expect(page.getByLabel('Password')).toBeVisible();
});
