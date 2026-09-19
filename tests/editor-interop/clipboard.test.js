// @vitest-environment jsdom

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { copyText } from '../../src/Castmill.UI/wwwroot/js/castmill-clipboard.js';

describe('clipboard copy', () => {
    beforeEach(() => {
        document.body.replaceChildren();
    });

    it('copies synchronously before consulting the async API', async () => {
        const asyncWrite = vi.fn().mockRejectedValue(new DOMException('Denied', 'NotAllowedError'));
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText: asyncWrite },
            configurable: true,
        });

        let selectedText = '';
        document.execCommand = vi.fn(command => {
            selectedText = document.activeElement?.value ?? '';
            return command === 'copy';
        });

        await expect(copyText('Complete transcript')).resolves.toBe(true);
        expect(selectedText).toBe('Complete transcript');
        expect(document.execCommand).toHaveBeenCalledWith('copy');
        expect(asyncWrite).toHaveBeenCalledWith('Complete transcript');
    });

    it('uses the async API immediately when selection copy is unavailable', async () => {
        document.execCommand = vi.fn(() => false);
        const asyncWrite = vi.fn().mockResolvedValue(undefined);
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText: asyncWrite },
            configurable: true,
        });

        await expect(copyText('Complete transcript')).resolves.toBe(true);
        expect(asyncWrite).toHaveBeenCalledWith('Complete transcript');
    });
});
