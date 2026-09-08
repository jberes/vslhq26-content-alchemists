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
        baseURL: 'http://localhost:5094',
        browserName: 'chromium',
        actionTimeout: 30_000,
        // A hung page load must fail in a minute, not sit until the 12-minute test timeout.
        navigationTimeout: 60_000,
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
            command: 'dotnet run --project src/Castmill.Api --no-build --no-launch-profile -- --urls http://localhost:5015',
            cwd: '../..',
            env: {
                ...process.env,
                ASPNETCORE_ENVIRONMENT: 'Development',
                AZURE_TOKEN_CREDENTIALS: 'AzureCliCredential',
                RateLimits__AuthPerMinute: '1000',
                Cors__AllowedOrigins__0: 'http://localhost:5094',
            },
            url: 'http://localhost:5015/health/db',
            reuseExistingServer: false,
            timeout: 120_000,
        },
        {
            command: 'dotnet run --project src/Castmill.Web --no-build --no-launch-profile -- --urls http://localhost:5094 --ApiBaseAddress=http://localhost:5015',
            cwd: '../..',
            env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' },
            url: 'http://localhost:5094',
            reuseExistingServer: false,
            timeout: 120_000,
        },
    ],
});
