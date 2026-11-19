/*
 * DPT.Engine launcher - behaviour.
 *
 * ES5 only. This page runs inside MSHTML, which ships with Windows XP and has no
 * ES6 support: no let/const, no arrow functions, no template literals, no Promise,
 * no fetch, no Array.from. Everything below is var/function and string
 * concatenation.
 *
 * The page never talks to the master itself. It asks the host, which does the
 * socket work on a worker thread, and the answer arrives through dptReceive. That
 * split exists because a page that blocked on a dead master would freeze the whole
 * launcher, and because MSHTML has no usable network API for a binary protocol.
 */

(function () {
  'use strict';

  // ---------------------------------------------------------------- bridge
  //
  // The host drives the page with dptReceive(verb, payload); the page drives the
  // host by parking a payload and navigating to dptengine://<verb>. The payload is
  // not in the URL because it can be arbitrarily long, and MSHTML lower-cases the
  // authority of a URI - so the host dispatches case-insensitively.

  var hostPayload = null;

  function hostCall(verb, payload) {
    hostPayload = (payload === null || typeof payload === 'undefined') ? '' : String(payload);

    try {
      window.location.href = 'dptengine://' + verb;
    } catch (error) {
      // A browser with no handler for the scheme throws or ignores this; neither
      // is fatal, the page just stays in standalone mode.
      hostPayload = null;
    }
  }

  window.dptengineTakePayload = function () {
    var value = hostPayload;
    hostPayload = null;
    return value === null ? '' : value;
  };

  window.dptReceive = function (verb, payload) {
    handleHost(verb, payload);
  };

  // ---------------------------------------------------------------- state

  var projects = [];     // local files: { path, name, bytes }
  var online = [];       // master catalogue: { id, name, author, kind, url, bytes }
  var selected = null;   // { source: 'local'|'online', item: {...} }
  var hasRuntime = false;

  // ---------------------------------------------------------------- dom

  function $(id) { return document.getElementById(id); }

  var dom = {};

  function collectDom() {
    dom.master = $('master');
    dom.theme = $('theme');
    dom.projects = $('projects');
    dom.online = $('online');
    dom.details = $('details');
    dom.status = $('status');
    dom.statusRight = $('statusRight');
    dom.aboutRuntime = $('aboutRuntime');
    dom.aboutData = $('aboutData');
    dom.btnPlay = $('btnPlay');
    dom.btnEdit = $('btnEdit');
    dom.btnRefresh = $('btnRefresh');
    dom.btnConnect = $('btnConnect');
    dom.btnFolder = $('btnFolder');
  }

  // ---------------------------------------------------------------- helpers

  function str(value) {
    return (value === null || typeof value === 'undefined') ? '' : String(value);
  }

  function formatBytes(bytes) {
    var value = Number(bytes) || 0;

    if (value < 1024) return value + ' B';
    if (value < 1024 * 1024) return (value / 1024).toFixed(1) + ' KB';
    if (value < 1024 * 1024 * 1024) return (value / (1024 * 1024)).toFixed(1) + ' MB';

    return (value / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
  }

  function clearNode(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function setStatus(text) {
    if (dom.status) dom.status.innerHTML = '';
    if (dom.status) dom.status.appendChild(document.createTextNode(str(text)));
  }

  // ---------------------------------------------------------------- rendering

  function renderProjects() {
    clearNode(dom.projects);

    if (projects.length === 0) {
      var empty = document.createElement('div');
      empty.className = 'empty';
      empty.appendChild(document.createTextNode('No .dptproj files found.'));
      dom.projects.appendChild(empty);
      return;
    }

    for (var i = 0; i < projects.length; i++) {
      dom.projects.appendChild(buildItem('local', projects[i], projects[i].name, formatBytes(projects[i].bytes), ''));
    }
  }

  function renderOnline() {
    clearNode(dom.online);

    if (online.length === 0) {
      var empty = document.createElement('div');
      empty.className = 'empty';
      empty.appendChild(document.createTextNode(
        dom.master && dom.master.value ? 'Nothing published.' : 'No master configured.'));
      dom.online.appendChild(empty);
      return;
    }

    for (var i = 0; i < online.length; i++) {
      var entry = online[i];
      var meta = formatBytes(entry.bytes) + '  ' + str(entry.downloads) + ' downloads';
      dom.online.appendChild(buildItem('online', entry, entry.name, meta, str(entry.kind)));
    }
  }

  function buildItem(source, item, name, meta, kind) {
    var row = document.createElement('div');
    row.className = 'item';

    if (selected && selected.source === source && selected.item === item) {
      row.className += ' selected';
    }

    if (kind) {
      var kindNode = document.createElement('span');
      kindNode.className = 'kind';
      kindNode.appendChild(document.createTextNode(kind));
      row.appendChild(kindNode);
    }

    var nameNode = document.createElement('div');
    nameNode.className = 'name';
    nameNode.appendChild(document.createTextNode(str(name)));
    row.appendChild(nameNode);

    var metaNode = document.createElement('div');
    metaNode.className = 'meta';
    metaNode.appendChild(document.createTextNode(str(meta)));
    row.appendChild(metaNode);

    row.onclick = function () {
      select(source, item);
    };

    return row;
  }

  function select(source, item) {
    selected = { source: source, item: item };

    renderProjects();
    renderOnline();
    renderDetails();
    updateActions();
  }

  function renderDetails() {
    clearNode(dom.details);

    if (!selected) {
      var empty = document.createElement('div');
      empty.className = 'empty';
      empty.appendChild(document.createTextNode('Select something to see its details.'));
      dom.details.appendChild(empty);
      return;
    }

    var item = selected.item;
    var list = document.createElement('dl');

    addRow(list, 'Name', item.name);
    addRow(list, 'Source', selected.source === 'local' ? 'This computer' : 'Master server');

    if (selected.source === 'local') {
      addRow(list, 'Path', item.path);
      addRow(list, 'Size', formatBytes(item.bytes));
    } else {
      addRow(list, 'Author', item.author);
      addRow(list, 'Kind', item.kind);
      addRow(list, 'Size', formatBytes(item.bytes));
      addRow(list, 'Downloads', item.downloads);
      addRow(list, 'Download', item.url);
    }

    dom.details.appendChild(list);
  }

  function addRow(list, label, value) {
    var dt = document.createElement('dt');
    dt.appendChild(document.createTextNode(label));

    var dd = document.createElement('dd');
    dd.appendChild(document.createTextNode(str(value)));

    list.appendChild(dt);
    list.appendChild(dd);
  }

  function updateActions() {
    var canPlay = selected && selected.source === 'local' && hasRuntime;

    dom.btnPlay.disabled = !canPlay;
    dom.btnEdit.disabled = !(selected && selected.source === 'local');
  }

  // ---------------------------------------------------------------- actions

  function play() {
    if (!selected || selected.source !== 'local') return;

    hostCall('launch', selected.item.path);
  }

  function edit() {
    if (!selected || selected.source !== 'local') return;

    hostCall('editor', selected.item.path);
  }

  function connect() {
    var value = dom.master.value;
    hostCall('setmaster', value);
    hostCall('refresh', '');
  }

  function refresh() {
    hostCall('refresh', '');
  }

  // ---------------------------------------------------------------- host messages

  function handleHost(verb, payload) {
    var name = str(verb).toLowerCase();
    var text = str(payload);

    if (name === 'projects') {
      applyProjects(text);
      return;
    }

    if (name === 'settings') {
      applySettings(text);
      return;
    }

    if (name === 'status') {
      setStatus(text || 'Ready.');
      return;
    }

    if (name === 'error') {
      setStatus(text || 'An error occurred.');
      alert(text);
      return;
    }

    if (name === 'hasruntime') {
      hasRuntime = text === '1';
      dom.aboutRuntime.appendChild(document.createTextNode(''));
      dom.aboutRuntime.innerHTML = '';
      dom.aboutRuntime.appendChild(document.createTextNode(
        hasRuntime ? 'found' : 'not found next to the launcher'));
      updateActions();
      return;
    }

    if (name === 'running') {
      if (text === '1') setStatus('The engine is running.');
      return;
    }
  }

  function applySettings(text) {
    // "host|port"
    var parts = str(text).split('|');

    if (parts.length > 0 && parts[0]) dom.master.value = parts[0];
  }

  function applyProjects(text) {
    // Two different shapes arrive under the same verb: the host sends a JSON
    // array from either the local scan or the master catalogue, and which one it
    // is depends on whether a master is configured.
    var parsed = parseJson(text);

    if (!parsed || typeof parsed.length !== 'number') return;

    var first = parsed.length > 0 ? parsed[0] : null;

    if (first && typeof first.path !== 'undefined') {
      projects = parsed;
      renderProjects();
      return;
    }

    if (first && typeof first.id !== 'undefined') {
      online = parsed;
      renderOnline();
      return;
    }

    // An empty array is ambiguous: apply it to whichever list is relevant.
    if (parsed.length === 0) {
      if (dom.master && dom.master.value) {
        online = [];
        renderOnline();
      } else {
        projects = [];
        renderProjects();
      }
    }
  }

  function parseJson(text) {
    if (!text) return null;

    try {
      // JSON.parse exists in IE8 and later; MSHTML on XP is IE6-class, so fall
      // back to eval on a string that came from our own host.
      if (window.JSON && window.JSON.parse) return window.JSON.parse(text);

      /*jslint evil: true */
      return eval('(' + text + ')');
    } catch (error) {
      setStatus('Could not read the answer from the host.');
      return null;
    }
  }

  // ---------------------------------------------------------------- theme

  function applyTheme(name) {
    var value = str(name).toLowerCase();

    if (value !== 'dark' && value !== 'oled') value = 'light';

    document.body.className = value;

    try {
      if (window.localStorage) window.localStorage.setItem('dptengine.launcher.theme', value);
    } catch (error) {
      // A locked-down profile has no storage; the theme just does not persist.
    }
  }

  function restoreTheme() {
    var saved = null;

    try {
      if (window.localStorage) saved = window.localStorage.getItem('dptengine.launcher.theme');
    } catch (error) {
      saved = null;
    }

    applyTheme(saved || 'light');

    if (dom.theme) dom.theme.value = document.body.className;
  }

  // ---------------------------------------------------------------- boot

  function boot() {
    collectDom();
    restoreTheme();

    if (dom.theme) {
      dom.theme.onchange = function () {
        applyTheme(dom.theme.value);
      };
    }

    if (dom.btnPlay) dom.btnPlay.onclick = play;
    if (dom.btnEdit) dom.btnEdit.onclick = edit;
    if (dom.btnRefresh) dom.btnRefresh.onclick = refresh;
    if (dom.btnConnect) dom.btnConnect.onclick = connect;
    if (dom.btnFolder) dom.btnFolder.onclick = function () { hostCall('openfolder', ''); };

    if (dom.master) {
      dom.master.onkeydown = function (event) {
        var key = event ? event.keyCode : window.event.keyCode;
        if (key === 13) connect();
      };
    }

    if (dom.aboutData) {
      dom.aboutData.appendChild(document.createTextNode('next to the launcher'));
    }

    updateActions();

    // Announce ourselves. In a plain browser there is no host, so the navigation
    // is ignored and the page stays usable in standalone mode.
    hostCall('ready', '');
  }

  if (document.addEventListener) {
    document.addEventListener('DOMContentLoaded', boot, false);
  } else {
    window.onload = boot;
  }

})();
