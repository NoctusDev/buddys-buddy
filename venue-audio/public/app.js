const $ = id => document.getElementById(id);
const status = text => { $('status').textContent = text; };
const peers = new Map();
let room, token, id, events, context, mixed, captured = [], generation = 0;
const guestRoom = new URLSearchParams(location.hash.slice(1)).get('room');
if (guestRoom) { $('broadcast').hidden = true; $('listen').hidden = false; }
async function json(url, body) {
  const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const value = await res.json(); if (!res.ok) throw new Error(value.error); return value;
}
function endpoint(action) { return `/rooms/${room}/${action}?${new URLSearchParams({ id, ...(token ? { token } : {}) })}`; }
function signal(to, type, data) { return json(endpoint('signal'), { to, type, data }); }
function count() { $('count').textContent = `${peers.size} listener connection(s)`; }
function closePeer(key) { peers.get(key)?.close(); peers.delete(key); count(); }
function peer(key) {
  if (peers.has(key)) return peers.get(key);
  // No third-party STUN server by default: same-PC/LAN proof first. Configure TURN for internet deployment.
  const pc = new RTCPeerConnection({ iceServers: [] });
  peers.set(key, pc); count();
  pc.onicecandidate = event => { if (event.candidate) signal(key, 'candidate', event.candidate).catch(e => status(e.message)); };
  pc.onconnectionstatechange = () => {
    if (pc.connectionState === 'failed') status('Audio connection failed. Internet connections may require a TURN relay. Leave and rejoin to retry.');
    if (pc.connectionState === 'connected') status(id === 'host' ? 'Broadcasting live.' : 'Connected. Listening live.');
  };
  pc.ontrack = event => { $('audio').srcObject = event.streams[0]; $('audio').play().catch(() => status('Press Join audio again to allow playback.')); };
  if (id === 'host') for (const track of mixed.getTracks()) pc.addTrack(track, mixed);
  pc.pendingCandidates = [];
  return pc;
}
let messages = Promise.resolve();
async function handle(message) {
  if (message.type === 'ready') return;
  if (message.type === 'offline') { closePeer('host'); $('audio').srcObject = null; return status('DJ is offline. Waiting for the broadcast.'); }
  if (message.type === 'leave') return closePeer(message.from);
  const pc = peer(message.from);
  if (message.type === 'join') {
    await pc.setLocalDescription(await pc.createOffer());
    await signal(message.from, 'offer', pc.localDescription);
  } else if (message.type === 'offer' || message.type === 'answer') {
    await pc.setRemoteDescription(message.data);
    for (const candidate of pc.pendingCandidates.splice(0)) await pc.addIceCandidate(candidate);
    if (message.type === 'offer') { await pc.setLocalDescription(await pc.createAnswer()); await signal(message.from, 'answer', pc.localDescription); }
  } else if (message.type === 'candidate') {
    if (pc.remoteDescription) await pc.addIceCandidate(message.data); else pc.pendingCandidates.push(message.data);
  }
}
function connect() {
  const current = generation;
  events = new EventSource(endpoint('events'));
  events.onmessage = event => { messages = messages.then(() => { if (current === generation) return handle(JSON.parse(event.data)); }).catch(e => status(e.message)); };
  events.onerror = () => { events?.close(); status('Signaling connection lost. Stop/leave and reconnect.'); };
}
function stop() {
  generation++; events?.close(); events = null;
  for (const key of peers.keys()) closePeer(key);
  for (const stream of captured) stream.getTracks().forEach(t => t.stop());
  captured = []; mixed = null; context?.close(); context = null;
  $('audio').srcObject = null; $('start').disabled = false; $('stop').disabled = true;
  $('join').disabled = false; $('leave').disabled = true; $('copy').disabled = true; $('invite').value = '';
  status('Disconnected. Audio capture stopped.');
}
$('devices').onclick = async () => {
  try {
    const permission = await navigator.mediaDevices.getUserMedia({ audio: true });
    permission.getTracks().forEach(t => t.stop());
    const devices = await navigator.mediaDevices.enumerateDevices();
    $('device').replaceChildren(new Option('Default input', ''), ...devices.filter(d => d.kind === 'audioinput').map(d => new Option(d.label || 'Audio input', d.deviceId)));
    status('Inputs refreshed. Temporary microphone access has stopped.');
  } catch (e) { status(e.message); }
};
$('start').onclick = async () => {
  $('start').disabled = true;
  try {
    let source;
    if ($('source').value === 'display') {
      source = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: true }); captured.push(source);
      source.getVideoTracks().forEach(t => t.stop());
      if (!source.getAudioTracks().length) throw new Error('No shared audio. Choose a tab with “Share audio” enabled, or select an audio input.');
    } else {
      source = await navigator.mediaDevices.getUserMedia({ audio: { deviceId: $('device').value ? { exact: $('device').value } : undefined, echoCancellation: false, noiseSuppression: false, autoGainControl: false } }); captured.push(source);
    }
    context = new AudioContext(); await context.resume();
    const destination = context.createMediaStreamDestination();
    context.createMediaStreamSource(source).connect(destination);
    if ($('mic').checked) {
      const microphone = await navigator.mediaDevices.getUserMedia({ audio: true }); captured.push(microphone);
      context.createMediaStreamSource(microphone).connect(destination);
    }
    mixed = destination.stream;
    ({ room, token } = await json('/rooms', {})); id = 'host'; connect();
    $('invite').value = `${location.origin}/#room=${room}`;
    $('stop').disabled = false; $('copy').disabled = false;
    source.getAudioTracks()[0].onended = stop;
    status('Broadcast ready. Share the guest link. Audio is not monitored locally to avoid feedback.');
  } catch (e) { stop(); status(e.message); }
};
$('join').onclick = async () => {
  if (events) { await $('audio').play().catch(e => status(e.message)); return; }
  room = guestRoom; token = null; id = crypto.randomUUID();
  $('audio').volume = Number($('volume').value);
  connect(); $('leave').disabled = false; status('Joining venue. Waiting for the DJ connection…');
};
$('stop').onclick = stop; $('leave').onclick = stop;
$('volume').oninput = () => { $('audio').volume = Number($('volume').value); };
$('copy').onclick = () => navigator.clipboard.writeText($('invite').value).then(() => status('Guest link copied.')).catch(e => status(e.message));
window.addEventListener('beforeunload', stop);
