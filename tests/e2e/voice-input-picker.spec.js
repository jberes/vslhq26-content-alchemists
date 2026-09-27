import { expect, signInFreshUser, test } from './fixtures.js';

// Choosing the microphone (both engines, every run). Chromium's launch flags supply "Fake Audio
// Input 1/2"; WebKit supplies "Mock audio device 1…4" once the permission is granted. "Recording
// from …" is read from the live audio track, so it proves which input really recorded — not
// which one the page asked for.

test('The recorder records from the chosen microphone, remembers it, follows plug and unplug, and says when it falls back', async ({ page, request }) => {
    await signInFreshUser(page, request, 'mic-picker');
    const recorder = page.getByRole('region', { name: 'Voice note recorder' });
    const picker = recorder.getByLabel('Microphone');
    const source = recorder.locator('.cm-voice__source');

    const openRecorder = async () => {
        await page.goto('/campaigns/new');
        await page.getByRole('listitem').filter({ hasText: 'Record an idea' }).click();
        await expect(recorder).toBeVisible();
    };

    // Records a short note, checks which input the track says it came from, then discards it:
    // nothing is uploaded and no campaign is created.
    const recordFrom = async expected => {
        await recorder.getByRole('button', { name: 'Record', exact: true }).click();
        await expect(recorder.getByText('Recording', { exact: true })).toBeVisible();
        await expect(source).toHaveText(expected ? `Recording from ${expected}` : /^Recording from \S/);
        const used = (await source.textContent()).replace('Recording from ', '').trim();
        await page.waitForTimeout(600);
        await recorder.getByRole('button', { name: 'Stop', exact: true }).click();
        await expect(recorder.getByText('Voice note ready', { exact: true })).toBeVisible();
        await expect(source).toHaveText(`Recorded from ${used}`);
        await recorder.getByRole('button', { name: 'Discard', exact: true }).click();
        await expect(recorder.getByRole('button', { name: 'Record', exact: true })).toBeVisible();
        return used;
    };

    await openRecorder();
    // The first recording allows the microphone; from then on the browser names its inputs.
    const systemDefault = await recordFrom(null);
    await expect(picker).toBeVisible();
    const labels = (await picker.locator('option').allTextContents()).map(label => label.trim());
    expect(labels[0]).toBe('System default');
    const inputs = labels.slice(1);
    expect(inputs.length).toBeGreaterThanOrEqual(2);
    expect(inputs).not.toContain('');

    // 1 · Pick the last input: the recording really comes from it.
    const second = inputs.at(-1);
    await picker.selectOption({ label: second });
    await recordFrom(second);

    // 2 · Remembered: a fresh page records from it straight away (WebKit names nothing before
    // the first recording, so this also proves the saved id is used, not the picker's state).
    await openRecorder();
    await recordFrom(second);
    await expect(picker.locator('option:checked')).toHaveText(second);

    // 3 · Switch to another input.
    const first = inputs[0];
    await picker.selectOption({ label: first });
    await recordFrom(first);

    // 4 · Live: the chosen microphone is unplugged, then plugged back in.
    await page.evaluate(label => {
        const devices = navigator.mediaDevices;
        window.__cmRealEnumerate ??= devices.enumerateDevices.bind(devices);
        devices.enumerateDevices = async () => (await window.__cmRealEnumerate()).filter(d => d.label !== label);
        devices.dispatchEvent(new Event('devicechange'));
    }, first);
    await expect(picker.locator('option:checked')).toHaveText(`${first} (not connected)`);
    await expect(recorder.locator('.cm-voice__notice')).toHaveText(`“${first}” isn't connected. Record will use the system default.`);
    await page.evaluate(() => {
        navigator.mediaDevices.enumerateDevices = window.__cmRealEnumerate;
        navigator.mediaDevices.dispatchEvent(new Event('devicechange'));
    });
    await expect(picker.locator('option:checked')).toHaveText(first);
    await expect(recorder.locator('.cm-voice__notice')).toHaveCount(0);

    // 5 · A remembered microphone that is gone: Record falls back to the system default and says
    // so, and the remembered choice is kept for when it comes back.
    await page.evaluate(() => localStorage.setItem('castmill:cm.voice.input',
        JSON.stringify({ deviceId: 'gone-usb-mic', label: 'USB Podcast Mic' })));
    await openRecorder();
    await recorder.getByRole('button', { name: 'Record', exact: true }).click();
    await expect(recorder.getByRole('alert')).toHaveText('“USB Podcast Mic” isn\'t connected, so this is recording from the system default instead.');
    await expect(source).toHaveText(`Recording from ${systemDefault}`);
    await recorder.getByRole('button', { name: 'Stop', exact: true }).click();
    await expect(recorder.getByText('Voice note ready', { exact: true })).toBeVisible();
    await recorder.getByRole('button', { name: 'Discard', exact: true }).click();
    await expect(recorder.locator('.cm-voice__notice')).toHaveText('“USB Podcast Mic” isn\'t connected. Record will use the system default.');
    await expect(picker.locator('option:checked')).toHaveText('USB Podcast Mic (not connected)');
    expect(await page.evaluate(() => localStorage.getItem('castmill:cm.voice.input'))).toContain('gone-usb-mic');

    // Back to the system default clears the choice.
    await picker.selectOption({ label: 'System default' });
    await expect(recorder.locator('.cm-voice__notice')).toHaveCount(0);
    expect(await page.evaluate(() => localStorage.getItem('castmill:cm.voice.input'))).toBe('');
});
