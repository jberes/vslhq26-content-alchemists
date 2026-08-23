import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
    capability,
    chooseMimeType,
    discard,
    dispose,
    getRecording,
    start,
} from '../../src/Castmill.UI/wwwroot/js/castmill-recorder.js';

class FakeMediaRecorder {
    static supported = new Set(['audio/webm;codecs=opus', 'audio/mp4']);
    static isTypeSupported(type) {
        return this.supported.has(type);
    }

    constructor(stream, options) {
        this.stream = stream;
        this.mimeType = options?.mimeType ?? 'audio/webm';
        this.state = 'inactive';
    }

    start() {
        this.state = 'recording';
    }

    pause() {
        this.state = 'paused';
    }

    resume() {
        this.state = 'recording';
    }

    stop() {
        this.state = 'inactive';
        this.ondataavailable?.({ data: new Blob(['voice'], { type: this.mimeType }) });
        this.onstop?.();
    }
}

describe('voice recorder island', () => {
    let getUserMedia;
    let stopped;

    beforeEach(() => {
        vi.useFakeTimers();
        stopped = vi.fn();
        getUserMedia = vi.fn(async () => ({
            getTracks: () => [{ stop: stopped }],
        }));
        Object.defineProperty(globalThis, 'isSecureContext', {
            value: true,
            configurable: true,
        });
        Object.defineProperty(navigator, 'mediaDevices', {
            value: { getUserMedia },
            configurable: true,
        });
        Object.defineProperty(navigator, 'userActivation', {
            value: { isActive: true },
            configurable: true,
        });
        Object.defineProperty(globalThis, 'MediaRecorder', {
            value: FakeMediaRecorder,
            configurable: true,
        });
        Object.defineProperty(globalThis.URL, 'createObjectURL', {
            value: vi.fn(() => 'blob:voice'),
            configurable: true,
        });
        Object.defineProperty(globalThis.URL, 'revokeObjectURL', {
            value: vi.fn(),
            configurable: true,
        });
    });

    afterEach(async () => {
        await dispose();
        vi.useRealTimers();
    });

    it('reports insecure and unsupported environments without opening the microphone', () => {
        Object.defineProperty(globalThis, 'isSecureContext', { value: false, configurable: true });
        expect(capability()).toEqual(expect.objectContaining({ state: 'Unsupported' }));
        expect(getUserMedia).not.toHaveBeenCalled();

        Object.defineProperty(globalThis, 'isSecureContext', { value: true, configurable: true });
        Object.defineProperty(navigator, 'mediaDevices', { value: undefined, configurable: true });
        expect(capability()).toEqual(expect.objectContaining({ state: 'Unsupported' }));
        expect(getUserMedia).not.toHaveBeenCalled();
    });

    it('chooses the first supported recording format', () => {
        expect(chooseMimeType(FakeMediaRecorder)).toBe('audio/webm;codecs=opus');
        FakeMediaRecorder.supported = new Set(['audio/mp4']);
        expect(chooseMimeType(FakeMediaRecorder)).toBe('audio/mp4');
        FakeMediaRecorder.supported = new Set(['audio/webm;codecs=opus', 'audio/mp4']);
    });

    it('does not open the microphone without an active user gesture', async () => {
        Object.defineProperty(navigator, 'userActivation', {
            value: { isActive: false },
            configurable: true,
        });
        const events = [];
        await start({ invokeMethodAsync: async (_, event) => events.push(event) }, 60);

        expect(getUserMedia).not.toHaveBeenCalled();
        expect(events.at(-1)).toEqual(expect.objectContaining({ state: 'Error' }));
    });

    it('reports microphone permission denial without retaining a stream', async () => {
        const denied = new Error('Denied');
        denied.name = 'NotAllowedError';
        getUserMedia.mockRejectedValueOnce(denied);
        const events = [];

        await start({ invokeMethodAsync: async (_, event) => events.push(event) }, 60);

        expect(events.at(-1)).toEqual(expect.objectContaining({
            state: 'PermissionDenied',
            message: expect.stringContaining('denied'),
        }));
        expect(stopped).not.toHaveBeenCalled();
    });

    it('auto-stops at the maximum duration and returns playable bytes', async () => {
        const events = [];
        await start({ invokeMethodAsync: async (_, event) => events.push(event) }, 1);
        expect(getUserMedia).toHaveBeenCalledOnce();
        expect(events).toContainEqual(expect.objectContaining({ state: 'Recording' }));

        await vi.advanceTimersByTimeAsync(1000);
        await vi.runAllTicks();

        expect(events).toContainEqual(expect.objectContaining({ state: 'Stopped' }));
        expect(stopped).toHaveBeenCalledOnce();
        const recording = await getRecording();
        expect(recording.contentType).toBe('audio/webm;codecs=opus');
        expect(recording.playbackUrl).toBe('blob:voice');
        expect(recording.bytes).toBeInstanceOf(Uint8Array);
        expect(recording.bytes.byteLength).toBeGreaterThan(0);
        await discard();
        expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:voice');
    });
});