const pendingKey = 'castmill:external-auth';
let callbackProof = null;

export function writePending(json) {
    try {
        callbackProof = null;
        window.sessionStorage.setItem(pendingKey, json);
        return true;
    } catch {
        return false;
    }
}

export function readPending() {
    try {
        return window.sessionStorage.getItem(pendingKey);
    } catch {
        return null;
    }
}

export function clearPending() {
    try {
        window.sessionStorage.removeItem(pendingKey);
    } catch {
        // Storage may be unavailable in hardened browser contexts.
    }
}

export function navigate(url) {
    window.location.assign(url);
}

export function hasCallback() {
    return parseCurrentFragment() || callbackProof !== null || readStoredProof() !== null;
}

export function consumeCallback(expectedAttemptId) {
    parseCurrentFragment();
    if (callbackProof !== null) {
        return callbackProof.AttemptId.toLowerCase() === expectedAttemptId.toLowerCase()
            ? JSON.stringify(callbackProof)
            : null;
    }

    const storedProof = readStoredProof();
    return storedProof !== null
        && storedProof.AttemptId.toLowerCase() === expectedAttemptId.toLowerCase()
        ? JSON.stringify(storedProof)
        : null;
}

export function clearCallback() {
    callbackProof = null;
}

function parseCurrentFragment() {
    const fragment = window.location.hash;
    if (fragment.length <= 1) {
        return false;
    }

    const parameters = new URLSearchParams(fragment.substring(1));
    if (parameters.get('external') !== 'complete') {
        return false;
    }

    window.history.replaceState(
        window.history.state,
        '',
        window.location.pathname + window.location.search);

    const pending = readPendingObject();
    const attemptId = parameters.get('attemptId');
    const pendingAttemptId = typeof pending?.AttemptId === 'string' ? pending.AttemptId : null;
    const proofAttemptId = attemptId ?? pendingAttemptId ?? '';
    const allowed = new Set(['external', 'attemptId', 'code', 'error']);
    for (const key of parameters.keys()) {
        if (!allowed.has(key) || parameters.getAll(key).length !== 1) {
            storeProof(invalidProof(proofAttemptId), pending, canBindToPending(pending, attemptId));
            return true;
        }
    }

    const code = parameters.get('code');
    const error = parameters.get('error');
    const codeIsValid = code !== null && /^[A-Za-z0-9_-]{43}$/.test(code);
    const errorIsValid = error !== null && /^[A-Za-z0-9_]{1,100}$/.test(error);
    const boundToPending = canBindToPending(pending, attemptId);
    if (attemptId === null || codeIsValid === errorIsValid) {
        storeProof(invalidProof(proofAttemptId), pending, boundToPending || attemptId === null);
        return true;
    }

    storeProof(
        { AttemptId: attemptId, Code: code, ErrorCode: error },
        pending,
        boundToPending);
    return true;
}

function canBindToPending(pending, attemptId) {
    if (pending === null
        || typeof pending.AttemptId !== 'string'
        || attemptId === null
        || pending.AttemptId.toLowerCase() !== attemptId.toLowerCase()) {
        return false;
    }

    return (pending.FlowKind === 'sign-in' && window.location.pathname === '/sign-in')
        || (pending.FlowKind === 'link' && window.location.pathname === '/settings/security');
}

function invalidProof(attemptId) {
    return {
        AttemptId: attemptId,
        Code: null,
        ErrorCode: 'external_auth_invalid_exchange_code',
    };
}

function storeProof(proof, pending, persist) {
    callbackProof = proof;
    if (!persist || pending === null) {
        return;
    }

    pending.CallbackCode = proof.Code;
    pending.CallbackErrorCode = proof.ErrorCode;
    try {
        window.sessionStorage.setItem(pendingKey, JSON.stringify(pending));
    } catch {
        // The in-memory proof still supports the current page lifetime.
    }
}

function readStoredProof() {
    const pending = readPendingObject();
    if (pending === null
        || typeof pending.AttemptId !== 'string'
        || (typeof pending.CallbackCode !== 'string'
            && typeof pending.CallbackErrorCode !== 'string')) {
        return null;
    }

    return {
        AttemptId: pending.AttemptId,
        Code: pending.CallbackCode ?? null,
        ErrorCode: pending.CallbackErrorCode ?? null,
    };
}

function readPendingObject() {
    const json = readPending();
    if (json === null) {
        return null;
    }

    try {
        return JSON.parse(json);
    } catch {
        return null;
    }
}