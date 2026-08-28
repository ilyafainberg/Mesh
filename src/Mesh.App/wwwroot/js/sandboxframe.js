// Securely loads generated mini-app HTML into an opaque-origin sandboxed iframe.
window.sandboxFrame = (function () {
  'use strict';

  var readyTimeoutMs = 5000;
  var selfTestTimeoutMs = 27000;
  var maxDiagnosticEntries = 250;
  var generation = 0;
  var frameStates = new WeakMap();
  var diagnosticEntries = [];
  var diagnosticBridge = null;
  var configuredPlatform = 'unknown';
  var csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
    "img-src data: blob:; media-src data: blob:; font-src data:; connect-src 'none'; " +
    "frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

  function normalizePlatform(value) {
    var normalized = String(value || 'unknown').trim().toLowerCase();
    return normalized || 'unknown';
  }

  function rendererModes(platform) {
    if (platform === 'ios') return ['host', 'srcdoc', 'blob', 'data'];
    if (platform === 'android') return ['srcdoc', 'blob', 'data'];
    if (platform === 'windows' || platform === 'macos') return ['srcdoc', 'blob'];
    return ['host', 'srcdoc', 'blob', 'data'];
  }

  function normalizedDetail(value) {
    var detail = String(value || '').replace(/[\r\n]+/g, ' ').trim();
    return detail.length <= 600 ? detail : detail.slice(0, 600) + ' [truncated]';
  }

  function persistDiagnostic(entry) {
    if (!diagnosticBridge || entry.persisted ||
      typeof diagnosticBridge.invokeMethodAsync !== 'function') return;
    entry.persisted = true;
    diagnosticBridge.invokeMethodAsync('RecordStage', entry.stage, entry.detail)
      .catch(function () { entry.persisted = false; });
  }

  function record(iframe, state, stage, detail) {
    var platform = state ? state.platform : configuredPlatform;
    var mode = state && state.mode ? state.mode : 'none';
    var purpose = state && state.purpose ? state.purpose : 'renderer';
    var currentGeneration = state ? state.generation : 0;
    var metadata = 'platform=' + normalizedDetail(platform) +
      '; mode=' + normalizedDetail(mode) +
      '; purpose=' + normalizedDetail(purpose) +
      '; generation=' + currentGeneration;
    var suffix = normalizedDetail(detail);
    var entry = {
      timestamp: new Date().toISOString(),
      stage: stage,
      detail: suffix ? metadata + '; ' + suffix : metadata,
      persisted: false
    };
    diagnosticEntries.push(entry);
    if (diagnosticEntries.length > maxDiagnosticEntries) diagnosticEntries.shift();
    persistDiagnostic(entry);
  }

  function initialize(dotNetRef, platform) {
    diagnosticBridge = dotNetRef || null;
    configuredPlatform = normalizePlatform(platform);
    for (var i = 0; i < diagnosticEntries.length; i++) persistDiagnostic(diagnosticEntries[i]);
    record(null, null, 'initialize',
      'origin=' + normalizedDetail(location.origin) +
      '; baseUri=' + normalizedDetail(document.baseURI) +
      '; userAgent=' + normalizedDetail(navigator.userAgent));
  }

  function escapeAttr(value) {
    return value.replace(/&/g, '&amp;').replace(/"/g, '&quot;');
  }

  function bootstrap(nonce) {
    // Opaque frames require "*" as targetOrigin. The parent also verifies the
    // WindowProxy identity, opaque origin, and this per-attempt nonce.
    return "<script>(function(){'use strict';" +
      "var n=" + JSON.stringify(nonce) + ";" +
      "var data=Object.create(null);" +
      "window.meshStorage={getItem:function(k){k=String(k);return Object.prototype.hasOwnProperty.call(data,k)?data[k]:null}," +
      "setItem:function(k,v){data[String(k)]=String(v)},removeItem:function(k){delete data[String(k)]}," +
      "clear:function(){data=Object.create(null)},key:function(i){return Object.keys(data)[i]||null}};" +
      "Object.defineProperty(window.meshStorage,'length',{get:function(){return Object.keys(data).length}});" +
      "function send(t,v){var p={type:t,nonce:n};v=v||{};for(var k in v){if(Object.prototype.hasOwnProperty.call(v,k))p[k]=v[k]}parent.postMessage(p,'*')}" +
      "function marker(){return document.body&&document.body.dataset.meshSelfTest||''}" +
      "send('mesh-widget-diagnostic',{stage:'payload-received'});" +
      "addEventListener('error',function(e){send('mesh-widget-error',{message:String(e.message||'Unknown widget error'),line:e.lineno||0,column:e.colno||0})});" +
      "addEventListener('unhandledrejection',function(e){send('mesh-widget-error',{message:String(e.reason&&e.reason.message||e.reason||'Unhandled promise rejection'),line:0,column:0})});" +
      "addEventListener('DOMContentLoaded',function(){" +
      "send('mesh-widget-diagnostic',{stage:'dom-ready',selfTest:marker()});" +
      "var done=false;function ready(source){if(done)return;done=true;var value=marker();" +
      "send('mesh-widget-diagnostic',{stage:'first-paint',selfTest:value,paintSource:source});" +
      "send('mesh-widget-ready',{selfTest:value,paintSource:source})}" +
      "if(typeof requestAnimationFrame==='function'){requestAnimationFrame(function(){requestAnimationFrame(function(){ready('raf')})})}" +
      "else{setTimeout(function(){ready('timer')},0)}setTimeout(function(){ready('timer-fallback')},1000);" +
      "});" +
      "})();<\/script>";
  }

  function secureDocument(html, nonce) {
    var policy = '<meta http-equiv="Content-Security-Policy" content="' + escapeAttr(csp) + '">';
    var source = html || '';
    var head = /<head(?:\s[^>]*)?>/i;
    if (head.test(source)) return source.replace(head, function (match) { return match + injected; });
    var root = /<html(?:\s[^>]*)?>/i;
    if (root.test(source)) return source.replace(root, function (match) { return match + '<head>' + injected + '</head>'; });
    return '<!doctype html><html><head>' + injected + '</head><body>' + source + '</body></html>';
  }

  function randomNonce() {
    if (self.crypto && crypto.getRandomValues) {
      var bytes = new Uint8Array(16);
      crypto.getRandomValues(bytes);
      return Array.prototype.map.call(bytes, function (value) {
        return value.toString(16).padStart(2, '0');
      }).join('');
    }
    return Date.now().toString(36) + Math.random().toString(36).slice(2);
  }

  function clearTimer(state) {
    if (!state || !state.timer) return;
    clearTimeout(state.timer);
    state.timer = null;
  }

  function clearAttempt(iframe, state) {
    if (!state) return;
    if (state.timer) {
      clearTimeout(state.timer);
      state.timer = null;
    }
    if (state.errorHandler) {
      iframe.removeEventListener('error', state.errorHandler);
      state.errorHandler = null;
    }
  }

  function statusElement(iframe) {
    return iframe.parentElement && iframe.parentElement.querySelector('.widget-render-status');
  }

  function showStatus(iframe, message, isError) {
    var notice = statusElement(iframe);
    if (!notice) return;
    notice.hidden = false;
    notice.setAttribute('role', isError ? 'alert' : 'status');
    notice.classList.toggle('widget-render-status--error', !!isError);
    notice.textContent = message;
  }

  function hideStatus(iframe) {
    var notice = statusElement(iframe);
    if (!notice) return;
    notice.hidden = true;
    notice.textContent = '';
    notice.classList.remove('widget-render-status--error');
  }

  function isCurrent(iframe, state) {
    return frameStates.get(iframe) === state &&
      iframe.dataset.widgetGeneration === String(state.generation);
  }

  function normalizedSelfTest(value) {
    if (value === 'script-ok') return 'script-ok';
    return value ? 'other' : 'none';
  }

  function normalizedPaintSource(value) {
    return value === 'raf' || value === 'timer' || value === 'timer-fallback'
      ? value : 'unknown';
  }

  function failFrame(iframe, state) {
    if (!isCurrent(iframe, state)) return;
    clearAttempt(iframe, state, true);
    record(iframe, state, 'renderer-failed', 'attempts=' + state.modes.length);
    iframe.removeAttribute('src');
    iframe.removeAttribute('srcdoc');
    iframe.dataset.widgetError = 'rendering-failed';
    showStatus(iframe, 'Widget preview could not be rendered securely.', true);
    iframe.dispatchEvent(new CustomEvent('mesh-widget-error', {
      detail: { type: 'mesh-widget-render-error', generation: state.generation, mode: state.mode }
    }));
  }

  function tryNext(iframe, state) {
    if (!isCurrent(iframe, state)) return;
    clearAttempt(iframe, state);
    state.attempt++;
    if (state.attempt >= state.modes.length) {
      failFrame(iframe, state);
      return;
    }

    if (state.attempt > 0) {
      showStatus(iframe, 'Trying the secure compatibility renderer...', false);
      record(iframe, state, 'fallback-start', 'nextAttempt=' + state.attempt);
    }

    state.mode = state.modes[state.attempt];
    state.awaitingHost = state.mode === 'host';
    var nonce = randomNonce() + '-' + state.generation + '-' + state.attempt;
    state.content = secureDocument(state.html, nonce);
    iframe.dataset.widgetNonce = nonce;
    iframe.dataset.widgetMode = state.mode;
    iframe.dataset.widgetStage = 'navigating';
    delete iframe.dataset.widgetReady;
    record(iframe, state, 'attempt-start', 'htmlLength=' + state.html.length);

    state.errorHandler = function () {
      if (!isCurrent(iframe, state)) return;
      record(iframe, state, 'frame-load-error', '');
      tryNext(iframe, state);
    };
    iframe.addEventListener('error', state.errorHandler);

    state.loadHandler = function () {
      if (!isCurrent(iframe, state)) return;
      iframe.dataset.widgetStage = 'document-loaded';
      diagnose(iframe, state, 'load');
      if (state.mode === 'host') return;
      clearTimer(state);
      state.timer = setTimeout(function () {
        acceptLoadedFrame(iframe, state);
      }, confirmationTimeoutMs);
    };
    iframe.addEventListener('load', state.loadHandler);

    state.timer = setTimeout(function () {
      if (!isCurrent(iframe, state)) return;
      record(iframe, state, 'timeout', 'timeoutMs=' + readyTimeoutMs);
      tryNext(iframe, state);
    }, readyTimeoutMs);

    try {
      if (state.mode === 'host') {
        iframe.removeAttribute('srcdoc');
        iframe.removeAttribute('src');
        iframe.src = new URL('widget-host.html', document.baseURI).href;
      } else if (state.mode === 'srcdoc') {
        iframe.removeAttribute('src');
        iframe.removeAttribute('srcdoc');
        iframe.srcdoc = content;
      } else if (state.mode === 'blob') {
        iframe.removeAttribute('srcdoc');
        iframe.removeAttribute('src');
        var blob = new Blob([content], { type: 'text/html' });
        state.blobUrl = URL.createObjectURL(blob);
        iframe.dataset.blobUrl = state.blobUrl;
        iframe.src = state.blobUrl;
      } else {
        iframe.removeAttribute('srcdoc');
        iframe.removeAttribute('src');
        iframe.src = 'data:text/html;charset=utf-8,' + encodeURIComponent(content);
      }
      record(iframe, state, 'document-assigned', '');
    } catch (error) {
      record(iframe, state, 'assignment-error',
        'errorType=' + normalizedDetail(error && error.name ? error.name : 'Error'));
      console.warn('Widget renderer failed; trying fallback', state.mode, error);
      tryNext(iframe, state);
    }
  }

  function findFrameForSource(source, requireNonce, nonce) {
    var frames = document.querySelectorAll('iframe[data-widget-generation]');
    for (var i = 0; i < frames.length; i++) {
      var frame = frames[i];
      if (frame.contentWindow !== source) continue;
      if (requireNonce && frame.dataset.widgetNonce !== nonce) continue;
      var state = frameStates.get(frame);
      if (state && isCurrent(frame, state)) return { frame: frame, state: state };
    }
    return null;
  }

  function onMessage(event) {
    var data = event.data;
    if (!data || typeof data.type !== 'string') return;

    if (data.type === 'mesh-widget-host-ready') {
      var hostMatch = findFrameForSource(event.source, false, null);
      if (!hostMatch || hostMatch.state.mode !== 'host' || !hostMatch.state.awaitingHost) return;
      var hostFrame = hostMatch.frame;
      var hostState = hostMatch.state;
      hostState.awaitingHost = false;
      clearAttempt(hostFrame, hostState, false);
      record(hostFrame, hostState, 'host-loaded', '');
      try {
        hostFrame.contentWindow.postMessage({
          type: 'mesh-widget-render',
          nonce: hostFrame.dataset.widgetNonce,
          html: hostState.content
        }, '*');
        record(hostFrame, hostState, 'handshake-sent', '');
        hostState.timer = setTimeout(function () {
          if (!isCurrent(hostFrame, hostState)) return;
          record(hostFrame, hostState, 'timeout', 'timeoutMs=' + readyTimeoutMs + '; phase=handshake');
          tryNext(hostFrame, hostState);
        }, readyTimeoutMs);
      } catch (error) {
        record(hostFrame, hostState, 'handshake-error',
          'errorType=' + normalizedDetail(error && error.name ? error.name : 'Error'));
        tryNext(hostFrame, hostState);
      }
      return;
    }

    if (data.type === 'mesh-widget-host-error') {
      var hostErrorMatch = findFrameForSource(event.source, true, data.nonce);
      if (!hostErrorMatch || hostErrorMatch.state.mode !== 'host') return;
      record(hostErrorMatch.frame, hostErrorMatch.state, 'host-render-error',
        'errorType=' + normalizedDetail(data.errorType || 'Error'));
      tryNext(hostErrorMatch.frame, hostErrorMatch.state);
      return;
    }

    if (data.type !== 'mesh-widget-error' &&
      data.type !== 'mesh-widget-ready' &&
      data.type !== 'mesh-widget-diagnostic') return;
    var match = findFrameForSource(event.source, true, data.nonce);
    if (!match) return;
    var frame = match.frame;
    var state = match.state;

    if (data.type === 'mesh-widget-diagnostic') {
      if (data.stage === 'payload-received') {
        record(frame, state, 'payload-received', '');
      } else if (data.stage === 'dom-ready') {
        record(frame, state, 'dom-ready', 'selfTest=' + normalizedSelfTest(data.selfTest));
      } else if (data.stage === 'first-paint') {
        record(frame, state, 'first-paint',
          'selfTest=' + normalizedSelfTest(data.selfTest) +
          '; paintSource=' + normalizedPaintSource(data.paintSource));
      }
      return;
    }

    clearAttempt(frame, state, false);
    if (data.type === 'mesh-widget-error') {
      var message = data.message || 'Unknown widget error';
      var line = Number.isFinite(Number(data.line)) ? Number(data.line) : 0;
      var column = Number.isFinite(Number(data.column)) ? Number(data.column) : 0;
      record(frame, state, 'runtime-error', 'line=' + line + '; column=' + column);
      frame.dataset.widgetError = message;
      showStatus(frame, 'Widget failed to start: ' + message, true);
      frame.dispatchEvent(new CustomEvent('mesh-widget-error', { detail: data }));
      console.error('Widget failed:', message, line, column);
    } else {
      var wasReady = frame.dataset.widgetReady === 'true';
      frame.dataset.widgetReady = 'true';
      record(frame, state, 'render-ready',
        'selfTest=' + normalizedSelfTest(data.selfTest) +
        '; paintSource=' + normalizedPaintSource(data.paintSource));
      if (!frame.dataset.widgetError) hideStatus(frame);
      if (!wasReady) {
        var detail = {
          type: data.type,
          nonce: data.nonce,
          selfTest: data.selfTest || '',
          paintSource: normalizedPaintSource(data.paintSource),
          generation: state.generation,
          mode: state.mode
        };
        frame.dispatchEvent(new CustomEvent('mesh-widget-ready', { detail: detail }));
      }
    }
  }

  window.addEventListener('message', onMessage);

  function setFrameHtmlInternal(iframe, html, platform, purpose) {
    if (!iframe) return;
    var previous = frameStates.get(iframe);
    clearAttempt(iframe, previous, true);
    generation++;
    var resolvedPlatform = normalizePlatform(platform || configuredPlatform);
    var state = {
      generation: generation,
      html: html || '',
      platform: resolvedPlatform,
      purpose: purpose || 'widget',
      modes: rendererModes(resolvedPlatform),
      attempt: -1,
      mode: '',
      content: '',
      timer: null,
      errorHandler: null,
      blobUrl: null,
      awaitingHost: false
    };
    frameStates.set(iframe, state);
    iframe.dataset.widgetGeneration = String(state.generation);
    delete iframe.dataset.widgetError;
    delete iframe.dataset.widgetReady;
    hideStatus(iframe);
    record(iframe, state, 'mount', 'htmlLength=' + state.html.length);
    tryNext(iframe, state);
  }

  function runSelfTest(iframe, html, platform) {
    return new Promise(function (resolve) {
      if (!iframe) {
        resolve('failed:no-frame');
        return;
      }
      var settled = false;
      var timer = null;

      function finish(result, stage) {
        if (settled) return;
        settled = true;
        if (timer) clearTimeout(timer);
        iframe.removeEventListener('mesh-widget-ready', onReady);
        iframe.removeEventListener('mesh-widget-error', onError);
        var state = frameStates.get(iframe);
        if (state) record(iframe, state, stage, 'result=' + result);
        resolve(result);
      }

      function onReady(event) {
        var detail = event.detail || {};
        if (detail.selfTest === 'script-ok') {
          finish('passed:' + (detail.mode || 'unknown'), 'self-test-passed');
        } else {
          finish('failed:script-marker-missing', 'self-test-failed');
        }
      }

      function onError() {
        finish('failed:renderer-error', 'self-test-failed');
      }

      iframe.addEventListener('mesh-widget-ready', onReady);
      iframe.addEventListener('mesh-widget-error', onError);
      timer = setTimeout(function () {
        finish('failed:self-test-timeout', 'self-test-failed');
      }, selfTestTimeoutMs);

      try {
        setFrameHtmlInternal(iframe, html, platform, 'self-test');
      } catch (error) {
        finish('failed:start-error', 'self-test-failed');
      }
    });
  }

  return {
    initialize: initialize,

    setFrameHtml: function (iframe, html, platform) {
      if (!iframe) return;
      try {
        setFrameHtmlInternal(iframe, html, platform, 'widget');
      } catch (error) {
        var state = frameStates.get(iframe);
        if (state) record(iframe, state, 'api-error',
          'errorType=' + normalizedDetail(error && error.name ? error.name : 'Error'));
        console.error('sandboxFrame.setFrameHtml failed', error);
        throw error;
      }
    },

    runSelfTest: runSelfTest,

    getDiagnostics: function () {
      var lines = [
        'Mesh widget diagnostics',
        'Generated: ' + new Date().toISOString(),
        'Configured platform: ' + configuredPlatform,
        'Contains renderer stage metadata only. Widget HTML and message text are not included.'
      ];
      for (var i = 0; i < diagnosticEntries.length; i++) {
        var entry = diagnosticEntries[i];
        lines.push('[' + entry.timestamp + '] [' + entry.stage + '] ' + entry.detail);
      }
      return lines.join('\n');
    },

    clearDiagnostics: function () {
      diagnosticEntries.length = 0;
    },

    revokeFrame: function (iframe) {
      if (!iframe) return;
      try {
        var state = frameStates.get(iframe);
        if (state) record(iframe, state, 'revoke', '');
        clearAttempt(iframe, state, true);
        frameStates.delete(iframe);
        iframe.removeAttribute('src');
        iframe.removeAttribute('srcdoc');
        delete iframe.dataset.widgetNonce;
        delete iframe.dataset.widgetGeneration;
        delete iframe.dataset.widgetMode;
        delete iframe.dataset.widgetStage;
        delete iframe.dataset.widgetError;
        delete iframe.dataset.widgetReady;
        hideStatus(iframe);
      } catch (error) {
        console.error('sandboxFrame.revokeFrame failed', error);
      }
    }
  };
})();
