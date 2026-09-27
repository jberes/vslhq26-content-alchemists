import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
    capability,
    chooseMimeType,
    discard,
    dispose,
    getRecording,
    listInputs,
    start,
    unwatchInputs,
    watchInputs,
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
describe('microphone choice', () => {
    const inputs = [
        { kind: 'audioinput', deviceId: 'default', label: 'Default - MacBook Pro Microphone' },
        { kind: 'audioinput', deviceId: 'mbp', label: 'MacBook Pro Microphone' },
        { kind: 'audioinput', deviceId: 'usb-1', label: 'Shure MV7' },
        { kind: 'videoinput', deviceId: 'cam', label: 'FaceTime HD Camera' },
    ];
    let devices;
    let opened;
    let listeners;

    function track(deviceId, label) {
        return { label, stop: vi.fn(), getSettings: () => ({ deviceId }) };
    }

    beforeEach(() => {
        vi.useFakeTimers();
        devices = [...inputs];
        opened = [];
        listeners = {};
        const getUserMedia = vi.fn(async constraints => {
            const wanted = constraints.audio.deviceId?.exact;
            const device = wanted
                ? devices.find(d => d.deviceId === wanted)
                : devices.find(d => d.deviceId === 'mbp');
            if (!device) {
                const error = new Error('No such device');
                error.name = 'OverconstrainedError';
                throw error;
            }
            opened.push(constraints.audio.deviceId?.exact ?? 'system default');
            const t = track(device.deviceId, device.label);
            return { getTracks: () => [t], getAudioTracks: () => [t] };
        });
        Object.defineProperty(globalThis, 'isSecureContext', { value: true, configurable: true });
        Object.defineProperty(navigator, 'mediaDevices', {
            value: {
                getUserMedia,
                enumerateDevices: async () => devices,
                addEventListener: (type, fn) => { listeners[type] = fn; },
                removeEventListener: type => { delete listeners[type]; },
            },
            configurable: true,
        });
        Object.defineProperty(navigator, 'userActivation', { value: { isActive: true }, configurable: true });
        Object.defineProperty(globalThis, 'MediaRecorder', { value: FakeMediaRecorder, configurable: true });
        Object.defineProperty(globalThis.URL, 'createObjectURL', { value: vi.fn(() => 'blob:voice'), configurable: true });
        Object.defineProperty(globalThis.URL, 'revokeObjectURL', { value: vi.fn(), configurable: true });
    });

    afterEach(async () => {
        unwatchInputs();
        await dispose();
        vi.useRealTimers();
    });

    const recorderCallback = () => {
        const calls = [];
        return { calls, invokeMethodAsync: async (method, payload) => calls.push({ method, payload }) };
    };
    const snapshots = callback => callback.calls.filter(c => c.method === 'OnVoiceCaptureChanged').map(c => c.payload);

    it('lists named microphones only, without the browser aliases for the default', async () => {
        expect(await listInputs()).toEqual([
            { deviceId: 'mbp', label: 'MacBook Pro Microphone' },
            { deviceId: 'usb-1', label: 'Shure MV7' },
        ]);
        // Before the microphone is allowed, Safari lists one unnamed entry: nothing to offer yet.
        devices = [{ kind: 'audioinput', deviceId: '', label: '' }];
        expect(await listInputs()).toEqual([]);
    });

    it('reports the list again when a microphone is plugged in or removed', async () => {
        const callback = recorderCallback();
        watchInputs(callback);
        devices = devices.filter(d => d.deviceId !== 'usb-1');
        await listeners.devicechange();
        expect(callback.calls.at(-1)).toEqual({
            method: 'OnInputsChanged',
            payload: [{ deviceId: 'mbp', label: 'MacBook Pro Microphone' }],
        });
        unwatchInputs();
        expect(listeners.devicechange).toBeUndefined();
    });

    it('records from exactly the chosen microphone and says which one it is', async () => {
        const callback = recorderCallback();
        await start(callback, 60, { deviceId: 'usb-1', label: 'Shure MV7' });

        expect(opened).toEqual(['usb-1']);
        expect(snapshots(callback)).toContainEqual(expect.objectContaining({
            state: 'Recording', inputLabel: 'Shure MV7', inputDeviceId: 'usb-1', notice: null,
        }));
        // Allowing the microphone is what names the inputs, so the picker's list is refreshed.
        expect(callback.calls).toContainEqual(expect.objectContaining({ method: 'OnInputsChanged' }));
    });

    it('finds the chosen microphone again by name when its id has changed', async () => {
        const callback = recorderCallback();
        await start(callback, 60, { deviceId: 'stale-id', label: 'Shure MV7' });

        expect(opened).toEqual(['system default', 'usb-1']);
        expect(snapshots(callback)).toContainEqual(expect.objectContaining({
            state: 'Recording', inputLabel: 'Shure MV7', inputDeviceId: 'usb-1', notice: null,
        }));
    });

    it('falls back to the system default when the chosen microphone is unplugged, and says so', async () => {
        devices = devices.filter(d => d.deviceId !== 'usb-1');
        const callback = recorderCallback();
        await start(callback, 60, { deviceId: 'usb-1', label: 'Shure MV7' });

        expect(opened).toEqual(['system default']);
        expect(snapshots(callback)).toContainEqual(expect.objectContaining({
            state: 'Recording',
            inputLabel: 'MacBook Pro Microphone',
            notice: expect.stringContaining('“Shure MV7” isn\'t connected'),
        }));
    });

    it('records from the system default when nothing was chosen', async () => {
        const callback = recorderCallback();
        await start(callback, 60, null);
        expect(opened).toEqual(['system default']);
        expect(snapshots(callback)).toContainEqual(expect.objectContaining({ inputLabel: 'MacBook Pro Microphone', notice: null }));
    });

    it('does not hide a real failure behind the fallback', async () => {
        navigator.mediaDevices.getUserMedia = vi.fn(async () => {
            const error = new Error('Denied');
            error.name = 'NotAllowedError';
            throw error;
        });
        const callback = recorderCallback();
        await start(callback, 60, { deviceId: 'usb-1', label: 'Shure MV7' });
        expect(snapshots(callback).at(-1)).toEqual(expect.objectContaining({ state: 'PermissionDenied' }));
        expect(navigator.mediaDevices.getUserMedia).toHaveBeenCalledOnce();
    });
});
