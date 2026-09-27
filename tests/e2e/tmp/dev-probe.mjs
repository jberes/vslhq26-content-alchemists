import { chromium, webkit } from '@playwright/test';
for (const [name, type, opts, ctx] of [
  ['chromium', chromium, { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] }, {}],
  ['webkit', webkit, {}, { permissions: ['microphone'] }],
]) {
  const browser = await type.launch(opts);
  const page = await (await browser.newContext(ctx)).newPage();
  await page.goto('http://localhost:5297/');
  const r = await page.evaluate(async () => {
    const list = async () => (await navigator.mediaDevices.enumerateDevices()).filter(d => d.kind === 'audioinput').map(d => `${d.deviceId.slice(0, 12)}|${d.label}`);
    const before = await list();
    const s = await navigator.mediaDevices.getUserMedia({ audio: true });
    const def = s.getAudioTracks()[0]; const defInfo = `${def.label} id=${def.getSettings().deviceId?.slice(0, 12)}`;
    s.getTracks().forEach(t => t.stop());
    const after = (await navigator.mediaDevices.enumerateDevices()).filter(d => d.kind === 'audioinput');
    const pick = after[after.length - 1];
    const s2 = await navigator.mediaDevices.getUserMedia({ audio: { deviceId: { exact: pick.deviceId } } });
    const t2 = s2.getAudioTracks()[0];
    let bad;
    try { await navigator.mediaDevices.getUserMedia({ audio: { deviceId: { exact: 'nope-missing' } } }); bad = 'no error'; } catch (e) { bad = `${e.name}: ${e.constraint ?? ''}`; }
    return { before, defInfo, after: after.map(d => `${d.deviceId.slice(0, 12)}|${d.label}`), picked: `${pick.label} -> track ${t2.label} id=${t2.getSettings().deviceId?.slice(0, 12)}`, bad, hasDevicechange: 'ondevicechange' in navigator.mediaDevices };
  });
  console.log(name, JSON.stringify(r, null, 1));
  await browser.close();
}
