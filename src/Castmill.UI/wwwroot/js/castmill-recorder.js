let discarding = false;
let stream;
let recorder;
let chunks = [];
let recordingBlob;
let playbackUrl;
let dotnet;
let audioContext;
let analyser;
let levelFrame;
let elapsedTimer;
let startedAt = 0;
let pausedAt = 0;
let pausedDuration = 0;
let elapsedSeconds = 0;
let maxTimer;
let actualMimeType;
let inputInfo = {};
let inputWatcher;

// Browser aliases for "whatever the system uses" — the picker offers its own System default.
const ALIAS_INPUTS = new Set(['default', 'communications']);

export function chooseMimeType(MediaRecorderType = globalThis.MediaRecorder) {
    if (!MediaRecorderType) return null;
    const candidates = [
        'audio/webm;codecs=opus',
        'audio/mp4;codecs=mp4a.40.2',
        'audio/mp4',
        'audio/webm',
    ];
    return candidates.find(type => MediaRecorderType.isTypeSupported?.(type)) ?? '';
}

export function capability() {
    if (!globalThis.isSecureContext) {
        return {
            state: 'Unsupported',
            message: 'Voice recording requires HTTPS or localhost.',
        };
    }
    if (!navigator.mediaDevices?.getUserMedia || !globalThis.MediaRecorder) {
        return {
            state: 'Unsupported',
            message: 'This browser or desktop WebView does not support microphone recording.',
        };
    }
    return { state: 'Idle' };
}

/**
 * The named microphones. Browsers name inputs (and in Safari even list them) only after the
 * microphone has been allowed once, so an empty list means "not yet allowed", not "none".
 */
export async function listInputs() {
    if (!navigator.mediaDevices?.enumerateDevices) return [];
    const devices = await navigator.mediaDevices.enumerateDevices();
    return devices
        .filter(device => device.kind === 'audioinput' && device.deviceId && device.label
            && !ALIAS_INPUTS.has(device.deviceId))
        .map(device => ({ deviceId: device.deviceId, label: device.label }));
}

/** Reports the list again whenever a microphone is plugged in or removed. */
export function watchInputs(callback) {
    unwatchInputs();
    if (!navigator.mediaDevices?.addEventListener) return;
    inputWatcher = async () => {
        try {
            await callback.invokeMethodAsync('OnInputsChanged', await listInputs());
        } catch {
            // The page is tearing down; the next page load lists the inputs afresh.
        }
    };
    navigator.mediaDevices.addEventListener('devicechange', inputWatcher);
}

export function unwatchInputs() {
    if (inputWatcher) navigator.mediaDevices?.removeEventListener?.('devicechange', inputWatcher);
    inputWatcher = undefined;
}

function audioConstraints(deviceId) {
    return {
        audio: {
            echoCancellation: true,
            noiseSuppression: true,
            autoGainControl: true,
            ...(deviceId ? { deviceId: { exact: deviceId } } : {}),
        },
        video: false,
    };
}

const isMissingInput = error => error?.name === 'OverconstrainedError' || error?.name === 'NotFoundError';

/**
 * Opens the chosen microphone, exactly — never a silent substitute. A saved id can go stale
 * (unplugged, or Safari handing out a new id), so it is looked up again by name; only when no
 * input by that name exists does recording fall back to the system default, and it says so.
 */
async function openInput(preferred) {
    if (!preferred?.deviceId && !preferred?.label) {
        return { stream: await navigator.mediaDevices.getUserMedia(audioConstraints()) };
    }
    if (preferred.deviceId) {
        try {
            return { stream: await navigator.mediaDevices.getUserMedia(audioConstraints(preferred.deviceId)) };
        } catch (error) {
            if (!isMissingInput(error)) throw error;
        }
    }
    const fallback = await navigator.mediaDevices.getUserMedia(audioConstraints());
    const match = (await listInputs()).find(input => input.label === preferred.label);
    if (match) {
        if (fallback.getAudioTracks()[0]?.getSettings?.().deviceId === match.deviceId) return { stream: fallback };
        fallback.getTracks().forEach(track => track.stop());
        return { stream: await navigator.mediaDevices.getUserMedia(audioConstraints(match.deviceId)) };
    }
    return {
        stream: fallback,
        notice: `“${preferred.label}” isn't connected, so this is recording from the system default instead.`,
    };
}

export async function start(callback, maxSeconds = 600, preferredInput = null) {
    const supported = capability();
    if (supported.state !== 'Idle') {
        await callback.invokeMethodAsync('OnVoiceCaptureChanged', supported);
        return;
    }
    if (globalThis.navigator.userActivation
        && !globalThis.navigator.userActivation.isActive) {
        await callback.invokeMethodAsync('OnVoiceCaptureChanged', {
            state: 'Error',
            message: 'Press Record to grant microphone access.',
        });
        return;
    }

    await dispose();
    inputInfo = {};
    dotnet = callback;
    await emit({ state: 'RequestingPermission' });
    try {
        const opened = await openInput(preferredInput);
        stream = opened.stream;
        const track = stream.getAudioTracks?.()[0];
        inputInfo = {
            inputLabel: track?.label || null,
            inputDeviceId: track?.getSettings?.().deviceId || null,
            notice: opened.notice ?? null,
        };
    } catch (error) {
        await emit({
            state: error?.name === 'NotAllowedError' ? 'PermissionDenied' : 'Error',
            message: error?.name === 'NotAllowedError'
                ? 'Microphone access was denied. Check browser or system settings.'
                : 'The microphone could not be opened.',
        });
        stopTracks();
        return;
    }

    // Allowing the microphone is what makes the browser name its inputs.
    try {
        await callback.invokeMethodAsync('OnInputsChanged', await listInputs());
    } catch {
        // Listing is a convenience; the recording itself goes ahead.
    }

    const mimeType = chooseMimeType();
    recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined);
    actualMimeType = recorder.mimeType || mimeType || 'audio/webm';
    chunks = [];
    recordingBlob = undefined;
        discarding = false;
    revokePlayback();
    elapsedSeconds = 0;
    pausedDuration = 0;
    pausedAt = 0;
    startedAt = performance.now();
    recorder.ondataavailable = event => {
        if (event.data?.size > 0) chunks.push(event.data);
    };
    recorder.onstop = async () => {
        if (discarding) {
            chunks = [];
            recordingBlob = undefined;
            stopMeters();
            stopTracks();
            return;
        }
        recordingBlob = new Blob(chunks, { type: actualMimeType });
        playbackUrl = URL.createObjectURL(recordingBlob);
        stopMeters();
        stopTracks();
        await emit({
            state: 'Stopped',
            elapsedSeconds,
            playbackUrl,
            contentType: actualMimeType,
            sizeBytes: recordingBlob.size,
        });
    };
    recorder.start(1000);
    startMeters();
    maxTimer = setTimeout(() => stop(), Math.max(1, maxSeconds) * 1000);
    await emit({ state: 'Recording', contentType: actualMimeType });
}

export async function pause() {
    if (recorder?.state !== 'recording') return;
    recorder.pause();
    pausedAt = performance.now();
    await emit({ state: 'Paused', elapsedSeconds, contentType: actualMimeType });
}

export async function resume() {
    if (recorder?.state !== 'paused') return;
    pausedDuration += performance.now() - pausedAt;
    pausedAt = 0;
    recorder.resume();
    await emit({ state: 'Recording', elapsedSeconds, contentType: actualMimeType });
}

export async function stop() {
    if (!recorder || recorder.state === 'inactive') return;
    elapsedSeconds = elapsed();
    recorder.stop();
}

export async function discard() {
    discarding = true;
    if (recorder && recorder.state !== 'inactive') recorder.stop();
    stopMeters();
    stopTracks();
    chunks = [];
    recordingBlob = undefined;
    revokePlayback();
    elapsedSeconds = 0;
}

export async function getRecording() {
    if (!recordingBlob || !playbackUrl) throw new Error('No stopped recording is available.');
    const bytes = new Uint8Array(await recordingBlob.arrayBuffer());
    const extension = actualMimeType?.includes('mp4') ? 'm4a' : 'webm';
    return {
        bytes,
        fileName: `voice-note-${new Date().toISOString().replaceAll(':', '-')}.${extension}`,
        contentType: actualMimeType,
        durationSeconds: elapsedSeconds,
        playbackUrl,
    };
}

export async function dispose() {
    discarding = true;
    if (recorder && recorder.state !== 'inactive') recorder.stop();
    stopMeters();
    stopTracks();
    chunks = [];
    recordingBlob = undefined;
    revokePlayback();
    recorder = undefined;
    dotnet = undefined;
}

function startMeters() {
    const AudioContextType = globalThis.AudioContext || globalThis.webkitAudioContext;
    if (AudioContextType) {
        audioContext = new AudioContextType();
        const source = audioContext.createMediaStreamSource(stream);
        analyser = audioContext.createAnalyser();
        analyser.fftSize = 256;
        source.connect(analyser);
        const samples = new Uint8Array(analyser.fftSize);
        let lastLevelEmit = 0;
        const tick = async () => {
            if (!analyser || !dotnet) return;
            analyser.getByteTimeDomainData(samples);
            const sum = samples.reduce((total, sample) => {
                const centered = (sample - 128) / 128;
                return total + centered * centered;
            }, 0);
            const inputLevel = Math.min(1, Math.sqrt(sum / samples.length) * 3);
            if (performance.now() - lastLevelEmit >= 100) {
                lastLevelEmit = performance.now();
                await emit({
                    state: recorder?.state === 'paused' ? 'Paused' : 'Recording',
                    elapsedSeconds: elapsed(),
                    inputLevel,
                    contentType: actualMimeType,
                });
            }
            levelFrame = requestAnimationFrame(tick);
        };
        levelFrame = requestAnimationFrame(tick);
    }
    elapsedTimer = setInterval(() => {
        elapsedSeconds = elapsed();
    }, 250);
}

function elapsed() {
    const end = pausedAt || performance.now();
    return Math.max(0, (end - startedAt - pausedDuration) / 1000);
}

async function emit(snapshot) {
    if (dotnet) await dotnet.invokeMethodAsync('OnVoiceCaptureChanged', { ...inputInfo, ...snapshot });
}

function stopMeters() {
    if (levelFrame) cancelAnimationFrame(levelFrame);
    if (elapsedTimer) clearInterval(elapsedTimer);
    if (maxTimer) clearTimeout(maxTimer);
    levelFrame = undefined;
    elapsedTimer = undefined;
    maxTimer = undefined;
    analyser = undefined;
    if (audioContext) audioContext.close().catch(() => {});
    audioContext = undefined;
}

function stopTracks() {
    stream?.getTracks().forEach(track => track.stop());
    stream = undefined;
}

function revokePlayback() {
    if (playbackUrl) URL.revokeObjectURL(playbackUrl);
    playbackUrl = undefined;
}