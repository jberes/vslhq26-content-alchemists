// @vitest-environment jsdom

import { beforeEach, describe, expect, it } from 'vitest';
import {
    clearCallback,
    consumeCallback,
    hasCallback,
    readPending,
    writePending,
} from '../../src/Castmill.UI/wwwroot/js/castmill-external-auth.js';

describe('external auth callback proof', () => {
    const attemptId = '48fd987e-dfc0-447a-a166-74364194f5f7';
    const code = 'e'.repeat(43);

    beforeEach(() => {
        clearCallback();
        window.sessionStorage.clear();
        window.history.replaceState({}, '', '/sign-in');
    });

    it('consumes fragment proof, scrubs history, and stores it with the pending verifier', () => {
        const pending = JSON.stringify({
            AttemptId: attemptId,
            PollSecret: 'p'.repeat(43),
            CodeVerifier: 'v'.repeat(43),
            ExpiresAt: '2026-08-29T22:00:00Z',
            ReturnUrl: '/campaigns/123',
            FlowKind: 'sign-in',
        });
        expect(writePending(pending)).toBe(true);
        window.history.replaceState(
            {},
            '',
            `/sign-in#external=complete&attemptId=${attemptId}&code=${code}`);

        const callback = JSON.parse(consumeCallback(attemptId));

        expect(callback).toEqual({ AttemptId: attemptId, Code: code, ErrorCode: null });
        expect(window.location.hash).toBe('');
        expect(window.location.pathname).toBe('/sign-in');
        expect(JSON.parse(readPending())).toEqual({
            ...JSON.parse(pending),
            CallbackCode: code,
            CallbackErrorCode: null,
        });
        expect(hasCallback()).toBe(true);
        expect(JSON.parse(consumeCallback(attemptId))).toEqual(callback);
        clearCallback();
        expect(hasCallback()).toBe(true);
        expect(JSON.parse(consumeCallback(attemptId))).toEqual(callback);
    });

    it('scrubs and rejects malformed or wrong-attempt fragments', () => {
        window.history.replaceState(
            {},
            '',
            `/settings/security#external=complete&attemptId=${attemptId}&code=short`);

        expect(JSON.parse(consumeCallback(attemptId))).toEqual({
            AttemptId: attemptId,
            Code: null,
            ErrorCode: 'external_auth_invalid_exchange_code',
        });
        expect(window.location.hash).toBe('');

        clearCallback();
        window.history.replaceState(
            {},
            '',
            `/settings/security#external=complete&attemptId=${attemptId}&code=${code}`);
        expect(consumeCallback('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')).toBeNull();
        expect(window.location.hash).toBe('');
    });

    it('prefers a newer fragment over stale cached proof', () => {
        const newerCode = 'n'.repeat(43);
        expect(writePending(JSON.stringify({
            AttemptId: attemptId,
            PollSecret: 'p'.repeat(43),
            CodeVerifier: 'v'.repeat(43),
            ExpiresAt: '2026-08-29T22:00:00Z',
            ReturnUrl: '',
            FlowKind: 'sign-in',
        }))).toBe(true);
        window.history.replaceState(
            {},
            '',
            `/sign-in#external=complete&attemptId=${attemptId}&code=${code}`);
        expect(JSON.parse(consumeCallback(attemptId)).Code).toBe(code);

        window.history.replaceState(
            {},
            '',
            `/sign-in#external=complete&attemptId=${attemptId}&code=${newerCode}`);

        expect(JSON.parse(consumeCallback(attemptId)).Code).toBe(newerCode);
        expect(JSON.parse(readPending()).CallbackCode).toBe(newerCode);
    });

    it('writing a new pending attempt resets stale in-memory proof', () => {
        expect(writePending(JSON.stringify({ AttemptId: attemptId, FlowKind: 'sign-in' }))).toBe(true);
        window.history.replaceState(
            {},
            '',
            `/sign-in#external=complete&attemptId=${attemptId}&code=${code}`);
        expect(consumeCallback(attemptId)).not.toBeNull();

        const newAttemptId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
        expect(writePending(JSON.stringify({ AttemptId: newAttemptId, FlowKind: 'sign-in' }))).toBe(true);

        expect(consumeCallback(newAttemptId)).toBeNull();
    });

    it('scrubs unsolicited and wrong-flow callback fragments without storing their proof', () => {
        window.history.replaceState(
            {},
            '',
            `/sign-in#external=complete&attemptId=${attemptId}&code=${code}`);
        expect(hasCallback()).toBe(true);
        expect(window.location.hash).toBe('');
        expect(readPending()).toBeNull();

        clearCallback();
        expect(writePending(JSON.stringify({ AttemptId: attemptId, FlowKind: 'link' }))).toBe(true);
        window.history.replaceState(
            {},
            '',
            `/sign-in#external=complete&attemptId=${attemptId}&code=${code}`);
        expect(hasCallback()).toBe(true);
        expect(window.location.hash).toBe('');
        expect(JSON.parse(readPending())).not.toHaveProperty('CallbackCode');
    });

    it('scrubs a wrong-attempt fragment and preserves the pending attempt for retry', () => {
        const otherAttemptId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
        expect(writePending(JSON.stringify({ AttemptId: attemptId, FlowKind: 'sign-in' }))).toBe(true);
        window.history.replaceState(
            {},
            '',
            `/sign-in#external=complete&attemptId=${otherAttemptId}&code=${code}`);

        expect(hasCallback()).toBe(true);
        expect(window.location.hash).toBe('');
        expect(consumeCallback(attemptId)).toBeNull();
        expect(JSON.parse(readPending())).toEqual({ AttemptId: attemptId, FlowKind: 'sign-in' });
    });

    it('restores persisted proof after module memory is cleared', () => {
        expect(writePending(JSON.stringify({ AttemptId: attemptId, FlowKind: 'sign-in' }))).toBe(true);
        window.history.replaceState(
            {},
            '',
            `/sign-in#external=complete&attemptId=${attemptId}&code=${code}`);
        expect(hasCallback()).toBe(true);
        clearCallback();

        expect(JSON.parse(consumeCallback(attemptId))).toEqual({
            AttemptId: attemptId,
            Code: code,
            ErrorCode: null,
        });
    });
});