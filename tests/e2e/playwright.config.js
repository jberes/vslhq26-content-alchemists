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
        actionTimeout: 30_000,
        // A hung page load must fail in a minute, not sit until the 12-minute test timeout.
        navigationTimeout: 60_000,
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure',
    },
    // Every spec runs in both engines on every pass — including the metered deep SEO/AEO flow
    // and the live webpage import. Nothing is opt-in.
    projects: [
        {
            name: 'chromium',
            use: {
                browserName: 'chromium',
                launchOptions: {
                    args: [
                        '--use-fake-device-for-media-stream',
                        '--use-fake-ui-for-media-stream',
                    ],
                },
            },
        },
        {
            // WebKit (the Safari / Mac Catalyst engine) supplies a mock microphone once the
            // permission is granted, so the voice recorder runs here too.
            name: 'webkit',
            use: { browserName: 'webkit', permissions: ['microphone'] },
        },
    ],
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
