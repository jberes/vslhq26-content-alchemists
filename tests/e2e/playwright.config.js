import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: '.',
    testMatch: /.*\.spec\.js/,
    timeout: 12 * 60 * 1000,
    expect: { timeout: 30_000 },
    fullyParallel: false,
    workers: 1,
    reporter: [['list']],
    use: {
        baseURL: 'http://localhost:5084',
        browserName: 'chromium',
        actionTimeout: 30_000,
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure',
        launchOptions: {
            args: [
                '--use-fake-device-for-media-stream',
                '--use-fake-ui-for-media-stream',
            ],
        },
    },
    webServer: [
        {
            command: 'dotnet run --project src/Castmill.Api --no-build',
            cwd: '../..',
            env: { ...process.env, RateLimits__AuthPerMinute: '1000' },
            url: 'http://localhost:5005/health/db',
            reuseExistingServer: true,
            timeout: 120_000,
        },
        {
            command: 'dotnet run --project src/Castmill.Web --no-build -- --ApiBaseAddress=http://localhost:5005',
            cwd: '../..',
            url: 'http://localhost:5084',
            reuseExistingServer: true,
            timeout: 120_000,
        },
    ],
});
