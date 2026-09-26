import { expect, test } from './fixtures.js';
import { deflateSync } from 'node:zlib';

// The manual image editor (ADR-F72) in a real browser, driven the way a producer drives it:
// every layer arrives by DRAGGING a tile, resizing uses the handles, cropping happens on the
// canvas, the keyboard deletes and reorders. The saved spec and the server's composited
// pixels are then checked, so the preview and the published image are proven to agree.
// No model calls; every row it creates is removed in `finally`.

const API = 'http://localhost:5015';

test.use({ viewport: { width: 1600, height: 1000 } });

test('Image editor: drag to add, resize, crop, shape, pop, text, z-order, delete, save', async ({ page, request }) => {
    let token = null;
    let campaignId = null;
    let brandId = null;
    const assetIds = [];

    try {
        const email = `image-bench-e2e-${crypto.randomUUID()}@castmill.local`;
        const password = 'image-bench-e2e-password-2026';
        const registration = await request.post(`${API}/api/v1/auth/register`, {
            data: { email, password, displayName: 'Image Bench E2E' },
        });
        expect(registration.status(), await registration.text()).toBe(200);
        token = (await registration.json()).accessToken;

        const brand = await request.post(`${API}/api/v1/brands`, {
            headers: bearer(token),
            data: { name: `Image Bench E2E ${Date.now()}`, styleCard: { voice: 'Clear.' } },
        });
        expect(brand.status()).toBe(201);
        brandId = (await brand.json()).id;

        // A full-resolution two-colour "portrait" (so crop focus is visible in the pixels) and
        // a flat green backdrop (so anything drawn over it is detectable).
        const portrait = png(2400, 1600, x => x < 1200 ? [220, 40, 40] : [40, 60, 220]);
        const backdrop = png(1600, 900, () => [20, 110, 60]);
        await uploadKitImage(request, token, brandId, 'portrait.png', portrait, 'face', 'Speaker portrait', assetIds);
        await uploadKitImage(request, token, brandId, 'backdrop.png', backdrop, 'background', 'Backdrop', assetIds);

        const campaign = await request.post(`${API}/api/v1/campaigns`, {
            headers: bearer(token),
            data: { name: `Image Bench E2E ${Date.now()}`, brief: 'Manual thumbnail.', brandId, contentType: 'Webinar' },
        });
        expect(campaign.status()).toBe(201);
        campaignId = (await campaign.json()).id;
        const artifact = await request.post(`${API}/api/v1/campaigns/${campaignId}/artifacts`, {
            headers: bearer(token),
            data: { kind: 'social-x', title: 'Launch post', contentJson: JSON.stringify({ text: 'Launch.' }) },
        });
        expect(artifact.status()).toBe(201);
        const slotResponse = await request.post(`${API}/api/v1/campaigns/${campaignId}/image-slots`, {
            headers: bearer(token),
            data: { artifactId: (await artifact.json()).id, promptMode: 'Auto', prompt: 'Manual' },
        });
        expect(slotResponse.status()).toBe(201);
        const slot = await slotResponse.json();

        const errors = [];
        page.on('pageerror', error => errors.push(error.stack ?? error.message));
        page.on('console', message => {
            if (message.type() === 'error' && /Unhandled exception rendering component|System\.\w+Exception/.test(message.text())) {
                errors.push(message.text());
            }
        });
        const healthy = async () => {
            await expect(page.locator('#blazor-error-ui')).toBeHidden();
            expect(errors).toEqual([]);
        };

        // The sign-in page prefills the demo account after it loads; wait for that before
        // typing, or a slower engine overwrites the test's own credentials.
        const demo = await (await request.get(`${API}/api/v1/dev/demo-credentials`)).json();
        await page.goto('/sign-in');
        await expect(page.getByLabel('Email')).toHaveValue(demo.email);
        await page.getByLabel('Email').fill(email);
        await page.getByLabel('Password').fill(password);
        await page.getByRole('button', { name: 'Sign in' }).click();
        await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();

        await page.goto(`/campaigns/${campaignId}/images`);
        const group = page.locator('.cm-studio__group', { hasText: 'Launch post' });
        await group.getByRole('button', { name: 'Create from scratch' }).click();
        const editor = page.getByRole('dialog', { name: 'Build an image' });
        await expect(editor).toBeVisible();
        await expect(editor).toContainText('Start with a background.');
        await healthy();

        const stage = editor.locator('[data-bench-stage]');
        const tile = label => editor.locator(`[data-bench-tile][data-tile-label="${label}"]`);
        const layers = editor.locator('[data-layer-id]');
        // Dragging must never leave the browser's blue text selection across the dialog.
        const noTextSelection = async () =>
            expect(await page.evaluate(() => window.getSelection()?.toString() ?? '')).toBe('');
        const at = async (fx, fy) => {
            const b = await stage.boundingBox();
            return { x: b.x + b.width * fx, y: b.y + b.height * fy };
        };

        // 1 · Drag, not click. The first picture onto an empty canvas is the background.
        const baseSaved = page.waitForResponse(r => r.url().endsWith(`/image-slots/${slot.id}/base`) && r.request().method() === 'POST');
        await drag(page, await centre(tile('Backdrop')), await at(0.5, 0.5));
        expect((await baseSaved).ok()).toBeTruthy();
        await expect(editor.locator('img.cm-bench__bg')).toBeVisible();

        await tile('Speaker portrait').click();
        await expect(page.getByText('Drag it onto the canvas', { exact: false }).first()).toBeVisible();
        await expect(layers).toHaveCount(0);

        await drag(page, await centre(tile('Speaker portrait')), await at(0.7, 0.5));
        await expect(layers).toHaveCount(1);
        const photo = layers.first();
        const photoId = await photo.getAttribute('data-layer-id');
        await expect(photo).toHaveAttribute('data-kind', 'image');
        await noTextSelection();

        // 9 · Full resolution: the layer draws the ORIGINAL, never the 480 px thumbnail.
        const photoImg = photo.locator('img[data-layer-img]');
        await expect(photoImg).not.toHaveAttribute('src', /derived\/thumbs/);
        await expect.poll(() => photoImg.evaluate(img => img.naturalWidth)).toBe(2400);
        await expect(editor.getByTestId('resolution')).toContainText('Source 2400×1600');
        await expect(editor.getByTestId('resolution')).toContainText('Sharp');

        // 2 · Very visible handles: eight of them, at least 14 px, on a corner-aspect-locked resize.
        const handles = editor.locator('.cm-bench__handle');
        await expect(handles).toHaveCount(8);
        for (const h of await handles.all()) {
            const hb = await h.boundingBox();
            expect(Math.min(hb.width, hb.height)).toBeGreaterThanOrEqual(10);
            expect(Math.max(hb.width, hb.height)).toBeGreaterThanOrEqual(14);
        }
        const before = await photo.boundingBox();
        await drag(page, await centre(editor.locator('.cm-bench__handle--se')), {
            x: before.x + before.width + 80, y: before.y + before.height + 20,
        });
        const resized = await photo.boundingBox();
        expect(resized.width).toBeGreaterThan(before.width + 40);
        expect(Math.abs(resized.width / resized.height - before.width / before.height)).toBeLessThan(0.03);

        // 8 · Move: press-and-drag the body selects and moves in one gesture.
        await drag(page, await centre(photo), { x: (await centre(photo)).x - 120, y: (await centre(photo)).y });
        const moved = await photo.boundingBox();
        expect(moved.x).toBeLessThan(resized.x - 80);
        await noTextSelection();

        // 3 · Crop on the canvas: double-click, drag the photo, pull a bracket, Enter.
        await photo.dblclick();
        await expect(editor.locator('.cm-bench__mode')).toContainText('CROP');
        await expect(editor.locator('.cm-bench__ghost')).toBeVisible();
        const frameBefore = await photo.boundingBox();
        await drag(page, await centre(editor.locator('.cm-bench__crophandle--se')), {
            x: frameBefore.x + frameBefore.width - 90, y: frameBefore.y + frameBefore.height - 60,
        });
        const cropped = await photo.boundingBox();
        expect(cropped.width).toBeLessThan(frameBefore.width - 60);
        await drag(page, await centre(photo), { x: (await centre(photo)).x - 40, y: (await centre(photo)).y });
        await page.keyboard.press('Enter');
        await expect(editor.locator('.cm-bench__mode')).toHaveText('SELECT');
        await expect.poll(async () => Number(await photo.getAttribute('data-zoom'))).toBeGreaterThan(1.001);

        // 4 · Circle frame, physically round.
        await editor.getByRole('button', { name: 'Circle', exact: true }).click();
        await expect(photo).toHaveAttribute('data-shape', 'circle');
        const round = await photo.boundingBox();
        expect(Math.abs(round.width - round.height)).toBeLessThanOrEqual(2);
        await expect(editor.locator('.cm-bench__handle')).toHaveCount(4);

        // 5 · Pop: a sticker border and lift, visible in the preview style.
        await editor.getByRole('button', { name: 'Sticker', exact: true }).click();
        await expect(photo).toHaveAttribute('style', /drop-shadow/);

        // Crop instruments stay ON TOP: parked at the canvas edge and zoomed to 4×, the ghosted
        // picture reaches over the tray/inspector and must be what the pointer hits there, and
        // the crop toolbar must be the topmost thing under its own centre.
        await drag(page, await centre(photo), await at(0.14, 0.75));
        await photo.dblclick();
        await expect(editor.locator('.cm-bench__mode')).toContainText('CROP');
        await editor.getByLabel('Crop zoom').fill('4');
        await expect.poll(async () => Number(await photo.getAttribute('data-zoom'))).toBeGreaterThan(3.9);
        const onTop = await page.evaluate(() => {
            const ghost = document.querySelector('.cm-bench__ghost').getBoundingClientRect();
            const hits = [];
            for (const selector of ['.cm-bench__tray', '.cm-bench__side']) {
                const panel = document.querySelector(selector).getBoundingClientRect();
                const left = Math.max(ghost.left, panel.left), right = Math.min(ghost.right, panel.right);
                const top = Math.max(ghost.top, panel.top), bottom = Math.min(ghost.bottom, panel.bottom);
                if (right - left > 4 && bottom - top > 4) {
                    const hit = document.elementFromPoint((left + right) / 2, (top + bottom) / 2);
                    hits.push({ selector, onGhost: !!hit?.closest('.cm-bench__ghost') });
                }
            }
            const bar = document.querySelector('[role=toolbar][aria-label=Crop]').getBoundingClientRect();
            const barHit = document.elementFromPoint(bar.left + bar.width / 2, bar.top + bar.height / 2);
            return { hits, toolbarOnTop: !!barHit?.closest('[role=toolbar]') };
        });
        expect(onTop.hits.length).toBeGreaterThan(0);
        expect(onTop.hits.every(h => h.onGhost)).toBeTruthy();
        expect(onTop.toolbarOnTop).toBeTruthy();
        await page.keyboard.press('Enter');
        await expect(editor.locator('.cm-bench__mode')).toHaveText('SELECT');

        // 7 · Text layer with font, size, colour, opacity and a background.
        await drag(page, await centre(tile('Headline')), await at(0.28, 0.3));
        await expect(layers).toHaveCount(2);
        const headlineId = await layers.nth(1).getAttribute('data-layer-id');
        const headline = editor.locator(`[data-layer-id="${headlineId}"]`);
        await headline.dblclick();
        const typing = editor.locator('textarea[data-bench-textedit]');
        await expect(typing).toBeFocused();
        await page.keyboard.press('ControlOrMeta+A');
        await page.keyboard.type('SHIP IT FAST');
        await page.keyboard.press('Escape');
        await expect(headline.locator('[data-words]')).toHaveText('SHIP IT FAST');
        await editor.getByLabel('Font', { exact: true }).selectOption('Anton');
        await editor.getByLabel('Font size').fill('110');
        await editor.getByLabel('Font size').press('Tab');
        await editor.getByLabel('Font colour').fill('#ffdd00');
        await editor.getByLabel('Text opacity').fill('0.8');
        await editor.getByLabel('Background colour').fill('#112233');
        await editor.getByLabel('Background opacity').fill('0.6');
        await expect(headline.locator('.cm-bench__text')).toHaveAttribute('style', /font-family:"Anton"/);
        await expect(headline.locator('.cm-bench__frame')).toHaveAttribute('style', /background:rgba\(17,34,51,0\.6\)/);

        // 10 · Z-order from the keyboard and by dragging a Layers row.
        await editor.locator(`[data-bench-row="${photoId}"]`).click();
        await page.keyboard.press('ControlOrMeta+BracketRight');
        await expect.poll(() => paintOrder(layers)).toEqual([headlineId, photoId]);
        await page.keyboard.press('ControlOrMeta+Shift+BracketLeft');
        await expect.poll(() => paintOrder(layers)).toEqual([photoId, headlineId]);
        const rows = editor.locator('[data-bench-row]');
        await drag(page, await centre(rows.nth(1).locator('[data-bench-grip]')), await topOf(rows.nth(0)));
        await expect.poll(() => paintOrder(layers)).toEqual([headlineId, photoId]);
        await noTextSelection();

        // 6 · Delete with the Delete key — even straight after typing in an inspector field —
        // undo, then the row bin.
        await drag(page, await centre(tile('Label chip')), await at(0.15, 0.15));
        await expect(layers).toHaveCount(3);
        await editor.getByLabel('Layer name').fill('Chip');
        await page.mouse.click((await centre(layers.nth(2))).x, (await centre(layers.nth(2))).y);
        await page.keyboard.press('Delete');
        await expect(layers).toHaveCount(2);
        await page.keyboard.press('ControlOrMeta+Z');
        await expect(layers).toHaveCount(3);
        await editor.getByRole('button', { name: 'Delete Chip' }).click();
        await expect(layers).toHaveCount(2);

        // 11 · Selection: hover outlines, empty canvas clears, Tab picks the front layer.
        await page.mouse.move((await at(0.05, 0.9)).x, (await at(0.05, 0.9)).y);
        await page.mouse.click((await at(0.05, 0.9)).x, (await at(0.05, 0.9)).y);
        await expect(editor.locator('.cm-bench__selection')).toHaveCount(0);
        await page.mouse.move((await centre(headline)).x, (await centre(headline)).y);
        await expect(editor.locator('.cm-bench__hover')).toHaveCount(1);
        await page.mouse.move((await at(0.05, 0.9)).x, (await at(0.05, 0.9)).y);
        await page.keyboard.press('Tab');
        await expect(editor.locator('.cm-bench__selection')).toHaveCount(1);
        await healthy();

        // Save: the spec carries every control as numbers, and the server composites it.
        const put = page.waitForResponse(r => r.url().endsWith(`/image-slots/${slot.id}/overlay`) && r.request().method() === 'PUT');
        await editor.getByRole('button', { name: 'Save image' }).click();
        const saved = await put;
        expect(saved.status(), await saved.text()).toBe(200);
        const spec = saved.request().postDataJSON();
        expect(spec.boxes).toHaveLength(2);
        const imageLayer = spec.boxes.find(b => b.kind === 'image');
        const textLayer = spec.boxes.find(b => b.kind === 'text');
        expect(imageLayer).toMatchObject({ shape: 'circle', effect: { preset: 'sticker' } });
        expect(imageLayer.crop.zoom).toBeGreaterThan(1);
        expect(textLayer).toMatchObject({ text: 'SHIP IT FAST', fontFamily: 'Anton', color: '#FFDD00', textOpacity: 0.8, band: { color: '#112233', opacity: 0.6 } });
        expect(spec.boxes.indexOf(textLayer)).toBeLessThan(spec.boxes.indexOf(imageLayer));
        await expect(editor.getByRole('button', { name: 'Saved' })).toBeDisabled();
        await healthy();

        const slots = await (await request.get(`${API}/api/v1/campaigns/${campaignId}/image-slots`, { headers: bearer(token) })).json();
        const stored = slots.find(s => s.id === slot.id);
        expect(stored.publishedUrl).toContain('/composited/');
        expect(stored.overlay.boxes.map(b => b.kind)).toEqual(['text', 'image']);

        // The composite really draws the round, cropped portrait over the green backdrop.
        const composite = await request.get(stored.publishedUrl);
        expect(composite.ok()).toBeTruthy();
        const pixels = await readPixels(page, await composite.body(), [
            [imageLayer.x + imageLayer.w / 2, imageLayer.y + imageLayer.h / 2],
            [imageLayer.x + imageLayer.w * 0.03, imageLayer.y + imageLayer.h * 0.03],
            [0.02, 0.98],
        ]);
        expect(pixels.size).toEqual([slot.targetWidth, slot.targetHeight]);
        const [inside, corner, backdropPixel] = pixels.values;
        expect(inside[1]).toBeLessThan(100);            // portrait red or blue, not green
        expect(corner[1]).toBeGreaterThan(inside[1]);   // outside the circle: backdrop/shadow, not portrait
        expect(backdropPixel[1]).toBeGreaterThan(80);   // untouched backdrop is green

        // Escape steps out: first the selection, then the dialog. Reopening restores the layers.
        await page.keyboard.press('Escape');
        await page.keyboard.press('Escape');
        await expect(editor).toHaveCount(0);
        await group.getByRole('button', { name: 'Create from scratch' }).click();
        await expect(editor.locator('[data-layer-id]')).toHaveCount(2);
        await expect(editor.locator(`[data-layer-id="${photoId}"]`)).toHaveAttribute('data-shape', 'circle');
        await healthy();
    } finally {
        if (token && campaignId) {
            await request.delete(`${API}/api/v1/campaigns/${campaignId}`, { headers: bearer(token) });
        }
        if (token && brandId) {
            await request.delete(`${API}/api/v1/brands/${brandId}`, { headers: bearer(token) });
        }
        for (const id of assetIds) {
            await request.delete(`${API}/api/v1/assets/${id}`, { headers: bearer(token) });
        }
    }
});

async function uploadKitImage(request, token, brandId, fileName, bytes, kind, label, assetIds) {
    const asset = await request.post(`${API}/api/v1/assets`, {
        headers: bearer(token),
        data: { fileName, contentType: 'image/png', sizeBytes: bytes.length },
    });
    expect(asset.status()).toBe(201);
    const assetId = (await asset.json()).id;
    assetIds.push(assetId);
    const content = await request.post(`${API}/api/v1/blob/assets/${assetId}/content`, {
        headers: { ...bearer(token), 'Content-Type': 'image/png' },
        data: bytes,
    });
    expect(content.status(), await content.text()).toBe(204);
    const link = await request.post(`${API}/api/v1/brands/${brandId}/assets`, {
        headers: bearer(token),
        data: { assetId, kind, label },
    });
    expect(link.status()).toBe(201);
}

/** A slow, real pointer drag: press, a small wiggle past the drag threshold, glide, release. */
async function drag(page, from, to) {
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await page.mouse.move(from.x + 6, from.y + 6, { steps: 2 });
    await page.mouse.move(to.x, to.y, { steps: 14 });
    await page.mouse.up();
}

async function centre(locator) {
    const b = await locator.boundingBox();
    return { x: b.x + b.width / 2, y: b.y + b.height / 2 };
}

async function topOf(locator) {
    const b = await locator.boundingBox();
    return { x: b.x + b.width / 2, y: b.y + 2 };
}

async function paintOrder(layers) {
    return layers.evaluateAll(nodes => nodes.map(n => n.getAttribute('data-layer-id')));
}

/** Decodes the composite in the page (same-origin blob, so the canvas is readable) and samples ratio points. */
async function readPixels(page, bytes, points) {
    return page.evaluate(async ({ base64, points }) => {
        const blob = await (await fetch(`data:image/webp;base64,${base64}`)).blob();
        const bitmap = await createImageBitmap(blob);
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const g = canvas.getContext('2d');
        g.drawImage(bitmap, 0, 0);
        const values = points.map(([x, y]) => {
            const px = Math.min(bitmap.width - 1, Math.max(0, Math.round(x * bitmap.width)));
            const py = Math.min(bitmap.height - 1, Math.max(0, Math.round(y * bitmap.height)));
            return Array.from(g.getImageData(px, py, 1, 1).data.slice(0, 3));
        });
        return { size: [bitmap.width, bitmap.height], values };
    }, { base64: bytes.toString('base64'), points });
}

function bearer(token) {
    return { Authorization: `Bearer ${token}` };
}

/** Minimal RGB PNG encoder, so the test owns exact source pixels and resolution. */
function png(width, height, pixel) {
    const stride = width * 3 + 1;
    const raw = Buffer.alloc(stride * height);
    for (let y = 0; y < height; y++) {
        raw[y * stride] = 0;
        for (let x = 0; x < width; x++) {
            const [r, g, b] = pixel(x, y);
            const o = y * stride + 1 + x * 3;
            raw[o] = r; raw[o + 1] = g; raw[o + 2] = b;
        }
    }
    const header = Buffer.alloc(13);
    header.writeUInt32BE(width, 0);
    header.writeUInt32BE(height, 4);
    header[8] = 8; header[9] = 2; header[10] = 0; header[11] = 0; header[12] = 0;
    return Buffer.concat([
        Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        chunk('IHDR', header),
        chunk('IDAT', deflateSync(raw)),
        chunk('IEND', Buffer.alloc(0)),
    ]);
}

function chunk(type, data) {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length, 0);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body), 0);
    return Buffer.concat([length, body, crc]);
}

function crc32(buffer) {
    let c = ~0;
    for (const byte of buffer) {
        c ^= byte;
        for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1));
    }
    return ~c >>> 0;
}
