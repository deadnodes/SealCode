import * as Y from 'https://esm.sh/yjs@13.6.29';
import { MonacoBinding } from 'https://esm.sh/y-monaco@0.1.6';
import { Awareness } from 'https://esm.sh/y-protocols@1.0.7/awareness';

const connectionStatusEl = document.getElementById('connectionStatus');
const roomNameEl = document.getElementById('roomName');
const createdByEl = document.getElementById('createdBy');
const participantsList = document.getElementById('participantsList');
const languageSelect = document.getElementById('languageSelect');
const versionNumberEl = document.getElementById('versionNumber');
const editorHost = document.getElementById('monacoEditor');
const joinOverlay = document.getElementById('joinOverlay');
const joinBtn = document.getElementById('joinBtn');
const displayNameInput = document.getElementById('displayNameInput');
const joinError = document.getElementById('joinError');

const pathSegments = new URL(window.location.href).pathname.split('/').filter(Boolean);
const roomId = pathSegments[pathSegments.length - 1] ?? '';
const urlParams = new URL(window.location.href).searchParams;
const platformAccessToken = urlParams.get('access_token') || '';
let displayName = '';
let currentVersion = 0;
let isApplyingRemoteUpdate = false;
let cursorTimer = null;
let currentUsers = [];
const cursorPositions = {};
const caretColors = {};
const caretPalette = ['#fbbf24', '#22d3ee', '#34d399', '#f472b6', '#a78bfa', '#fb7185'];
let hideRemoteCarets = false;
let hideRemoteTimer = null;
const activeEditors = new Map();
const selectingUsers = new Map();
const copyingUsers = new Map();
const caretClassMap = new Map();
let caretClassCounter = 0;

let editor = null;
let model = null;
let editorReadyResolve;
const editorReady = new Promise((resolve) => { editorReadyResolve = resolve; });
let ydoc = null;
let ytext = null;
let awareness = null;
let monacoBinding = null;
let pendingYjsStateTimer = null;
let pendingYjsUpdates = [];
let isBootstrapping = false;
let roomClosed = false;
let hasJoinedRoom = false;
let startConnectionPromise = null;
let isRejoiningRoom = false;
let rejoinRetryTimer = null;
let rejoinRetryAttempt = 0;
const rejoinRetryDelaysMs = [500, 1000, 2000, 5000, 10000];

const connection = new signalR.HubConnectionBuilder()
  .withUrl(`/roomHub${platformAccessToken ? `?access_token=${encodeURIComponent(platformAccessToken)}` : ''}`)
  .withAutomaticReconnect({
    nextRetryDelayInMilliseconds: () => (navigator.onLine ? 3000 : 2000)
  })
  .build();

connection.serverTimeoutInMilliseconds = 15000;
connection.keepAliveIntervalInMilliseconds = 5000;

window.__debugYjsSend = () => {
  if (!ydoc) return false;
  scheduleYjsUpdateSend(Y.encodeStateAsUpdate(ydoc));
  return true;
};

function setStatus(status) {
  connectionStatusEl.textContent = status;
}

function readPlatformDisplayName() {
  if (!platformAccessToken) return '';
  const body = platformAccessToken.split('.')[0] || '';
  if (!body) return '';
  try {
    const padded = body.replace(/-/g, '+').replace(/_/g, '/').padEnd(body.length + ((4 - body.length % 4) % 4), '=');
    const payload = JSON.parse(atob(padded));
    return String(payload.name || '').trim();
  } catch {
    return '';
  }
}

function clearRejoinRetryTimer() {
  if (!rejoinRetryTimer) return;
  clearTimeout(rejoinRetryTimer);
  rejoinRetryTimer = null;
}

async function ensureConnectionStarted() {
  if (connection.state !== signalR.HubConnectionState.Disconnected) {
    return true;
  }
  if (startConnectionPromise) {
    return startConnectionPromise;
  }
  startConnectionPromise = connection.start()
    .then(() => true)
    .catch((err) => {
      console.warn('Failed to start connection.', err);
      return false;
    })
    .finally(() => {
      startConnectionPromise = null;
    });
  return startConnectionPromise;
}

function getRejoinDelayMs() {
  const index = Math.min(rejoinRetryAttempt, rejoinRetryDelaysMs.length - 1);
  return rejoinRetryDelaysMs[index];
}

async function rejoinRoomWithRetry() {
  if (!hasJoinedRoom || !displayName || roomClosed) return;
  if (connection.state !== signalR.HubConnectionState.Connected) return;
  if (isRejoiningRoom) return;

  isRejoiningRoom = true;
  clearRejoinRetryTimer();
  setStatus('reconnecting');
  setEditorReadOnly(true);
  setLanguageReadOnly(true);

  let joined = false;
  try {
    joined = await joinRoom();
  } finally {
    isRejoiningRoom = false;
  }

  if (joined) {
    rejoinRetryAttempt = 0;
    setStatus('connected');
    setEditorReadOnly(false);
    setLanguageReadOnly(false);
    if (ydoc) {
      scheduleYjsUpdateSend(Y.encodeStateAsUpdate(ydoc));
    }
    return;
  }

  if (roomClosed || connection.state !== signalR.HubConnectionState.Connected) {
    return;
  }

  const delayMs = getRejoinDelayMs();
  rejoinRetryAttempt += 1;
  rejoinRetryTimer = setTimeout(() => {
    rejoinRetryTimer = null;
    rejoinRoomWithRetry().catch((err) => {
      console.warn('Failed to rejoin room.', err);
    });
  }, delayMs);
}

function base64FromUint8(data) {
  if (!data || data.length === 0) return '';
  let binary = '';
  const chunkSize = 0x8000;
  for (let i = 0; i < data.length; i += chunkSize) {
    const chunk = data.subarray(i, i + chunkSize);
    binary += String.fromCharCode(...chunk);
  }
  return btoa(binary);
}

function uint8FromBase64(base64) {
  if (!base64) return new Uint8Array();
  const binary = atob(base64);
  const len = binary.length;
  const bytes = new Uint8Array(len);
  for (let i = 0; i < len; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

function truncateName(name) {
  if (!name) return '';
  if (name.length <= 10) return name;
  return `${name.slice(0, 9)}...`;
}

function setLanguageOptions(selectEl, languages, selected) {
  const cleaned = Array.isArray(languages)
    ? languages.map((language) => (typeof language === 'string' ? language.trim() : ''))
      .filter((language) => language)
    : [];
  const unique = [...new Set(cleaned)];

  selectEl.innerHTML = '';
  if (unique.length === 0) {
    const option = document.createElement('option');
    option.value = '';
    option.textContent = 'Languages unavailable';
    option.disabled = true;
    option.selected = true;
    selectEl.appendChild(option);
    selectEl.disabled = true;
    return;
  }

  for (const language of unique) {
    const option = document.createElement('option');
    option.value = language;
    option.textContent = language;
    selectEl.appendChild(option);
  }

  selectEl.disabled = false;
  if (selected && unique.includes(selected)) {
    selectEl.value = selected;
  } else {
    selectEl.selectedIndex = 0;
  }
}

function ensureLanguageOption(language) {
  if (!language) return;
  if ([...languageSelect.options].some((option) => option.value === language)) return;
  const option = document.createElement('option');
  option.value = language;
  option.textContent = language;
  languageSelect.appendChild(option);
  languageSelect.disabled = false;
}

async function loadLanguages(selected) {
  const preferred = selected ?? languageSelect?.value ?? '';
  try {
    const res = await fetch('/languages');
    if (!res.ok) {
      throw new Error('Failed to load languages');
    }
    const languages = await res.json();
    setLanguageOptions(languageSelect, languages, preferred);
  } catch {
    setLanguageOptions(languageSelect, [], preferred);
  }

  if (!languageSelect.disabled && languageSelect.value) {
    setLanguage(languageSelect.value);
  }
}

function renderUsers(users) {
  if (!Array.isArray(users)) {
    participantsList.innerHTML = '';
    return;
  }
  currentUsers = users;
  participantsList.innerHTML = '';
  for (const user of users) {
    const li = document.createElement('li');
    const pos = cursorPositions[user];
    const display = truncateName(user);
    li.title = user;
    if (activeEditors.get(user)) {
      li.classList.add('active');
    }
    if (selectingUsers.get(user)) {
      li.classList.add('copied');
    }
    if (copyingUsers.get(user)) {
      li.classList.add('copying');
    }
    if (typeof pos === 'number' && model) {
      const lc = getLineColFromOffset(pos);
      li.textContent = `${display} (${lc.line}:${lc.col})`;
    } else {
      li.textContent = display;
    }
    participantsList.appendChild(li);
  }
}

function getLineColFromOffset(offset) {
  if (!model) return { line: 1, col: 1 };
  const pos = model.getPositionAt(Math.max(0, offset));
  return { line: pos.lineNumber, col: pos.column };
}

function assignColor(name) {
  if (!caretColors[name]) {
    const idx = Object.keys(caretColors).length % caretPalette.length;
    caretColors[name] = caretPalette[idx];
  }
  return caretColors[name];
}

function ensureCaretStyles() {
  let styleEl = document.getElementById('remoteCaretStyles');
  if (!styleEl) {
    styleEl = document.createElement('style');
    styleEl.id = 'remoteCaretStyles';
    document.head.appendChild(styleEl);
  }
  return styleEl;
}

function renderRemoteCarets() {
  if (!editor || !model) return;
  if (hideRemoteCarets) {
    editor.remoteCaretDecorations = editor.deltaDecorations(editor.remoteCaretDecorations || [], []);
    return;
  }
  const decorations = [];
  const styleEl = ensureCaretStyles();
  let css = '';

  for (const [name, offset] of Object.entries(cursorPositions)) {
    if (name === displayName) continue;
    if (typeof offset !== 'number') continue;
    const pos = model.getPositionAt(Math.max(0, offset));
    const line = pos.lineNumber;
    const col = Math.min(pos.column, model.getLineMaxColumn(line));
    const color = assignColor(name);
    if (!caretClassMap.has(name)) {
      caretClassCounter += 1;
      caretClassMap.set(name, `remote-caret-${caretClassCounter}`);
    }
    const className = caretClassMap.get(name);
    css += `.${className} { border-left: 2px solid ${color}; margin-left: -1px; }\n`;
    decorations.push({
      range: new monaco.Range(line, col, line, col),
      options: {
        beforeContentClassName: className,
        stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges
      }
    });
  }

  styleEl.textContent = css;
  editor.remoteCaretDecorations = editor.deltaDecorations(editor.remoteCaretDecorations || [], decorations);
}

function hideRemoteCaretsTemporarily() {
  hideRemoteCarets = true;
  renderRemoteCarets();
  if (hideRemoteTimer) clearTimeout(hideRemoteTimer);
  hideRemoteTimer = setTimeout(() => {
    hideRemoteCarets = false;
    renderRemoteCarets();
  }, 800);
}

function markActiveEditor(name) {
  if (!name) return;
  activeEditors.set(name, Date.now());
  renderUsers(currentUsers);
  setTimeout(() => {
    const last = activeEditors.get(name);
    if (last && Date.now() - last > 1200) {
      activeEditors.delete(name);
      renderUsers(currentUsers);
    }
  }, 1300);
}

function markSelecting(name, isSelecting) {
  if (!name) return;
  if (isSelecting) {
    selectingUsers.set(name, Date.now());
  } else {
    selectingUsers.delete(name);
  }
  renderUsers(currentUsers);
}

function markCopying(name) {
  if (!name) return;
  copyingUsers.set(name, Date.now());
  renderUsers(currentUsers);
  setTimeout(() => {
    const last = copyingUsers.get(name);
    if (last && Date.now() - last > 1200) {
      copyingUsers.delete(name);
      renderUsers(currentUsers);
    }
  }, 1300);
}

function sendCursor() {
  if (!displayName) return;
  if (connection.state !== signalR.HubConnectionState.Connected) return;
  if (!editor || !model) return;
  const pos = editor.getPosition();
  if (!pos) return;
  const offset = model.getOffsetAt(pos);
  cursorPositions[displayName] = offset;
  renderUsers(currentUsers);
  renderRemoteCarets();
  connection.invoke('UpdateCursor', roomId, offset).catch(() => {});
}

function scheduleCursorSend() {
  if (cursorTimer) return;
  cursorTimer = setTimeout(() => {
    cursorTimer = null;
    sendCursor();
  }, 100);
}

function hasNonEmptyEditorSelection() {
  if (!editor) return false;
  const selections = editor.getSelections();
  if (!Array.isArray(selections) || selections.length === 0) {
    return false;
  }
  return selections.some((selection) => !selection.isEmpty());
}

function isCopyShortcutKeyboardEvent(event) {
  if (!event) return false;
  const key = typeof event.key === 'string' ? event.key.toLowerCase() : '';
  return key === 'c' && (event.ctrlKey || event.metaKey) && !event.altKey;
}

function isUndoShortcutKeyboardEvent(event) {
  if (!event) return false;
  if (!(event.ctrlKey || event.metaKey) || event.altKey) return false;
  const key = typeof event.key === 'string' ? event.key.toLowerCase() : '';
  const code = typeof event.code === 'string' ? event.code.toLowerCase() : '';
  const isUndoKey = key === 'z' || code === 'keyz';
  return isUndoKey && !event.shiftKey;
}

function isRedoShortcutKeyboardEvent(event) {
  if (!event) return false;
  if (!(event.ctrlKey || event.metaKey) || event.altKey) return false;
  const key = typeof event.key === 'string' ? event.key.toLowerCase() : '';
  const code = typeof event.code === 'string' ? event.code.toLowerCase() : '';
  const isRedoKey = key === 'y' || code === 'keyy';
  const isShiftedUndoKey = (key === 'z' || code === 'keyz') && event.shiftKey;
  return isRedoKey || isShiftedUndoKey;
}

function isUndoRedoShortcutKeyboardEvent(event) {
  return isUndoShortcutKeyboardEvent(event) || isRedoShortcutKeyboardEvent(event);
}

function setLanguage(language) {
  if (!model) return;
  monaco.editor.setModelLanguage(model, language);
}

function setText(text) {
  if (!model) return;
  isApplyingRemoteUpdate = true;
  model.setValue(text || '');
  isApplyingRemoteUpdate = false;
  renderRemoteCarets();
}

function setEditorReadOnly(isReadOnly) {
  if (!editor) return;
  editor.updateOptions({ readOnly: isReadOnly });
}

function setLanguageReadOnly(isReadOnly) {
  if (!languageSelect) return;
  languageSelect.disabled = isReadOnly;
}

function syncModelFromYjs() {
  if (!model || !ytext) return;
  const docText = ytext.toString();
  if (model.getValue() === docText) return;

  const hadBinding = monacoBinding;
  if (hadBinding) {
    monacoBinding.destroy();
    monacoBinding = null;
  }

  setText(docText);

  if (hadBinding && editor) {
    monacoBinding = new MonacoBinding(ytext, model, new Set([editor]), awareness);
    if (displayName && awareness) {
      awareness.setLocalStateField('user', { name: displayName });
    }
  }
}

function scheduleYjsUpdateSend(update) {
  if (!update) return;
  pendingYjsUpdates.push(update);
  if (pendingYjsStateTimer) return;
  pendingYjsStateTimer = setTimeout(async () => {
    if (connection.state !== signalR.HubConnectionState.Connected) {
      pendingYjsStateTimer = null;
      return;
    }
    const updates = pendingYjsUpdates;
    pendingYjsUpdates = [];
    pendingYjsStateTimer = null;
    if (!ydoc || !ytext) return;
    try {
      const merged = updates.length === 1
        ? updates[0]
        : (Y.mergeUpdates ? Y.mergeUpdates(updates) : Y.encodeStateAsUpdate(ydoc));
      const updateBase64 = base64FromUint8(merged);
      const stateBase64 = base64FromUint8(Y.encodeStateAsUpdate(ydoc));
      const snapshot = ytext.toString();
      await connection.invoke('UpdateYjs', roomId, updateBase64, stateBase64, snapshot);
    } catch (err) {
      console.error('Failed to send Yjs update.', err);
      joinError.textContent = 'Failed to sync changes. Try refreshing.';
      joinError.classList.remove('hidden');
    }
  }, 60);
}



function initializeYjs(text, stateBase64) {
  if (!model) return;
  if (monacoBinding) {
    monacoBinding.destroy();
    monacoBinding = null;
  }
  if (ydoc) {
    ydoc.destroy();
  }
  ydoc = new Y.Doc();
  ytext = ydoc.getText('monaco');
  awareness = new Awareness(ydoc);
  if (displayName) {
    awareness.setLocalStateField('user', { name: displayName });
  }
  monacoBinding = new MonacoBinding(ytext, model, new Set([editor]), awareness);

  ydoc.on('update', (update, origin) => {
    if (isBootstrapping) return;
    if (origin === 'remote') return;
    scheduleYjsUpdateSend(update);
  });

  isBootstrapping = true;
  if (stateBase64) {
    const update = uint8FromBase64(stateBase64);
    if (update.length > 0) {
      isApplyingRemoteUpdate = true;
      Y.applyUpdate(ydoc, update, 'remote');
      isApplyingRemoteUpdate = false;
    }
  } else if (text) {
    ydoc.transact(() => {
      ytext.insert(0, text);
    }, 'init');
  }
  setText(ytext.toString());
  isBootstrapping = false;
}

function applyRemoteCursorAdjustment(changes) {
  if (!changes || !changes.length || !model) return;
  const sorted = [...changes].sort((a, b) => (a.rangeOffset ?? 0) - (b.rangeOffset ?? 0));
  for (const change of sorted) {
    const start = typeof change.rangeOffset === 'number'
      ? change.rangeOffset
      : model.getOffsetAt({ lineNumber: change.range.startLineNumber, column: change.range.startColumn });
    const length = typeof change.rangeLength === 'number'
      ? change.rangeLength
      : model.getOffsetAt({ lineNumber: change.range.endLineNumber, column: change.range.endColumn }) - start;
    const end = start + length;
    const delta = (change.text || '').length - length;

    for (const [name, offset] of Object.entries(cursorPositions)) {
      if (name === displayName || typeof offset !== 'number') continue;
      if (offset <= start) continue;
      if (offset >= end) {
        cursorPositions[name] = offset + delta;
      } else {
        cursorPositions[name] = start + Math.max(0, (change.text || '').length);
      }
    }
  }
}

function initMonaco() {
  const amdRequire = window.require;
  amdRequire.config({ paths: { vs: 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.47.0/min/vs' } });
  amdRequire(['vs/editor/editor.main'], () => {
    model = monaco.editor.createModel('', languageSelect?.value || 'plaintext');
    editor = monaco.editor.create(editorHost, {
      model,
      theme: 'vs-dark',
      minimap: { enabled: false },
      automaticLayout: true,
      fixedOverflowWidgets: true,
      emptySelectionClipboard: false,
      fontFamily: 'IBM Plex Mono, monospace',
      fontSize: 14,
      readOnly: true
    });

    editor.onDidChangeModelContent((event) => {
      if (isApplyingRemoteUpdate) return;
      hideRemoteCaretsTemporarily();
      applyRemoteCursorAdjustment(event.changes);
      markActiveEditor(displayName);
      scheduleCursorSend();
    });

    editor.onKeyDown((keyboardEvent) => {
      const browserEvent = keyboardEvent.browserEvent;
      if (isUndoRedoShortcutKeyboardEvent(browserEvent)) {
        browserEvent.preventDefault();
        browserEvent.stopImmediatePropagation();
        keyboardEvent.preventDefault();
        keyboardEvent.stopPropagation();
        return;
      }
      if (!isCopyShortcutKeyboardEvent(browserEvent)) return;
      if (hasNonEmptyEditorSelection()) return;
      browserEvent.preventDefault();
      browserEvent.stopImmediatePropagation();
      keyboardEvent.preventDefault();
      keyboardEvent.stopPropagation();
    });

    editor.onDidChangeCursorSelection(() => {
      if (!displayName || connection.state !== signalR.HubConnectionState.Connected) {
        return;
      }
      scheduleCursorSend();
      const sel = editor.getSelection();
      if (!sel) return;
      const isMultiLine = sel.startLineNumber !== sel.endLineNumber;
      connection.invoke('UpdateSelection', roomId, isMultiLine).catch((err) => {
        console.warn('Failed to send selection update.', err);
      });
    });

    editorReadyResolve();
  });
}

async function joinRoom() {
  joinError.classList.add('hidden');
  try {
    const result = await connection.invoke('JoinRoom', roomId, displayName);
    const name = result.Name ?? result.name;
    const language = result.Language ?? result.language;
    const text = result.Text ?? result.text;
    const version = result.Version ?? result.version;
    const users = result.Users ?? result.users;
    const createdBy = result.CreatedBy ?? result.createdBy;
    const yjsState = result.YjsState ?? result.yjsState;
    const joinedDisplayName = result.JoinedDisplayName ?? result.joinedDisplayName;
    if (joinedDisplayName) {
      displayName = joinedDisplayName;
      displayNameInput.value = joinedDisplayName;
    }
    roomNameEl.textContent = name;
    if (createdByEl) {
      createdByEl.textContent = createdBy || 'unknown';
    }
    ensureLanguageOption(language);
    languageSelect.value = language;
    setLanguage(language);
    initializeYjs(text || '', yjsState);
    currentVersion = version;
    versionNumberEl.textContent = currentVersion;
    renderUsers(users);
    hasJoinedRoom = true;
    rejoinRetryAttempt = 0;
    clearRejoinRetryTimer();
    joinOverlay.classList.add('hidden');
    sendCursor();
    return true;
  } catch (err) {
    joinError.textContent = err?.message || 'Failed to join room.';
    joinError.classList.remove('hidden');
    return false;
  }
}

connection.onreconnected(async () => {
  if (hasJoinedRoom && displayName && !roomClosed) {
    await rejoinRoomWithRetry();
    return;
  }
  setStatus('connected');
});
connection.onreconnecting(() => {
  clearRejoinRetryTimer();
  isRejoiningRoom = false;
  setStatus('reconnecting');
  setEditorReadOnly(true);
  setLanguageReadOnly(true);
});
connection.onclose(() => {
  clearRejoinRetryTimer();
  isRejoiningRoom = false;
  setStatus('disconnected');
  setEditorReadOnly(true);
  setLanguageReadOnly(true);
});

window.addEventListener('offline', () => {
  if (!hasJoinedRoom || roomClosed) return;
  setStatus('reconnecting');
  setEditorReadOnly(true);
  setLanguageReadOnly(true);
});

window.addEventListener('online', () => {
  if (!hasJoinedRoom || roomClosed) return;
  if (connection.state === signalR.HubConnectionState.Connected) {
    setStatus('connected');
    setEditorReadOnly(false);
    setLanguageReadOnly(false);
    return;
  }
  setStatus('reconnecting');
  ensureConnectionStarted()
    .then((started) => {
      if (!started) return;
      return rejoinRoomWithRetry();
    })
    .catch((err) => {
      console.warn('Failed to recover after going online.', err);
    });
});

connection.on('UserJoined', (name, users) => {
  cursorPositions[name] ??= 0;
  renderUsers(users);
  renderRemoteCarets();
});

connection.on('UserLeft', (name, users) => {
  delete cursorPositions[name];
  caretClassMap.delete(name);
  delete caretColors[name];
  activeEditors.delete(name);
  selectingUsers.delete(name);
  copyingUsers.delete(name);
  renderUsers(users);
  renderRemoteCarets();
});

connection.on('YjsUpdated', (updateBase64, version, author, stateBase64) => {
  currentVersion = version;
  versionNumberEl.textContent = currentVersion;
  renderUsers(currentUsers);
  if (!ydoc) return;
  if (author === displayName) return;
  markActiveEditor(author);
  const update = uint8FromBase64(updateBase64);
  if (update.length === 0) return;
  const beforeText = ytext ? ytext.toString() : '';
  isApplyingRemoteUpdate = true;
  Y.applyUpdate(ydoc, update, 'remote');
  isApplyingRemoteUpdate = false;
  const afterText = ytext ? ytext.toString() : '';
  if (stateBase64 && beforeText === afterText) {
    const fullState = uint8FromBase64(stateBase64);
    if (fullState.length > 0) {
      isApplyingRemoteUpdate = true;
      Y.applyUpdate(ydoc, fullState, 'remote');
      isApplyingRemoteUpdate = false;
    }
  }
  syncModelFromYjs();
});

connection.on('LanguageUpdated', (language, version) => {
  languageSelect.value = language;
  setLanguage(language);
  currentVersion = version;
  versionNumberEl.textContent = currentVersion;
});

connection.on('CursorUpdated', (name, position) => {
  if (!name) return;
  cursorPositions[name] = position;
  renderUsers(currentUsers);
  renderRemoteCarets();
});

connection.on('UserSelection', (name, isMultiLine) => {
  markSelecting(name, isMultiLine);
});

connection.on('UserCopy', (name) => {
  markCopying(name);
});

connection.on('RoomKilled', (reason) => {
  roomClosed = true;
  hasJoinedRoom = false;
  clearRejoinRetryTimer();
  setEditorReadOnly(true);
  joinError.textContent = reason || 'Room closed.';
  joinError.classList.remove('hidden');
  joinOverlay.classList.remove('hidden');
});

languageSelect.addEventListener('change', async () => {
  if (!displayName) return;
  if (!languageSelect.value) return;
  setLanguage(languageSelect.value);
  if (connection.state !== signalR.HubConnectionState.Connected) return;
  try {
    await connection.invoke('SetLanguage', roomId, languageSelect.value);
  } catch (err) {
    console.warn('Failed to update language.', err);
  }
});

document.addEventListener('copy', (event) => {
  if (!editor) return;
  const target = event.target;
  const isEditorCopy = editor.hasTextFocus() || (target instanceof Node && editorHost.contains(target));
  if (!isEditorCopy) return;
  if (!hasNonEmptyEditorSelection()) {
    event.preventDefault();
    event.stopImmediatePropagation();
    return;
  }
  if (!displayName) return;
  if (connection.state !== signalR.HubConnectionState.Connected) return;
  markCopying(displayName);
  connection.invoke('UpdateCopy', roomId).catch(() => {});
}, true);

joinBtn.addEventListener('click', async () => {
  displayName = displayNameInput.value.trim();
  if (!displayName) {
    joinError.textContent = 'Display name is required.';
    joinError.classList.remove('hidden');
    return;
  }
  await editorReady;
  if (connection.state === signalR.HubConnectionState.Disconnected) {
    const started = await ensureConnectionStarted();
    if (!started) {
      setStatus('disconnected');
      joinError.textContent = 'Failed to connect. Try again.';
      joinError.classList.remove('hidden');
      return;
    }
  }
  setStatus('connected');
  setLanguageReadOnly(true);
  const joined = await joinRoom();
  if (joined) {
    setEditorReadOnly(false);
    setLanguageReadOnly(false);
  } else {
    setEditorReadOnly(true);
    setLanguageReadOnly(true);
  }
});

async function joinWithPlatformToken() {
  displayName = readPlatformDisplayName();
  if (!displayName) return;
  displayNameInput.value = displayName;
  joinOverlay.classList.add('hidden');
  await editorReady;
  const started = await ensureConnectionStarted();
  if (!started) {
    setStatus('disconnected');
    joinError.textContent = 'Failed to connect. Try again.';
    joinError.classList.remove('hidden');
    joinOverlay.classList.remove('hidden');
    return;
  }
  setStatus('connected');
  setLanguageReadOnly(true);
  const joined = await joinRoom();
  if (joined) {
    setEditorReadOnly(false);
    setLanguageReadOnly(false);
    return;
  }
  setEditorReadOnly(true);
  setLanguageReadOnly(true);
  joinOverlay.classList.remove('hidden');
}

setStatus('disconnected');
loadLanguages().finally(() => {
  setLanguageReadOnly(true);
});
initMonaco();

const platformDisplayName = readPlatformDisplayName();
if (platformDisplayName) {
  window.setTimeout(() => {
    joinWithPlatformToken().catch((err) => {
      console.warn('Failed to join with platform token.', err);
      joinOverlay.classList.remove('hidden');
    });
  }, 0);
}
