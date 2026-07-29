// Securely loads generated mini-app HTML into an opaque-origin sandboxed iframe.
window.sandboxFrame = (function () {
  'use strict';

  var navigationTimeoutMs = 4000;
  var confirmationTimeoutMs = 1200;
  var generation = 0;
  var frameStates = new WeakMap();
  var csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
    "img-src data: blob:; media-src data: blob:; font-src data:; connect-src 'none'; " +
    "frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

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
      "function send(t,l,c){parent.postMessage({type:t,nonce:n,line:l||0,column:c||0},'*')}" +
      "addEventListener('error',function(e){send('mesh-widget-error',e.lineno,e.colno)});" +
      "addEventListener('unhandledrejection',function(){send('mesh-widget-error',0,0)});" +
      "addEventListener('DOMContentLoaded',function(){send('mesh-widget-ready',0,0)});" +
      "})();<\/script>";
  }

  function secureDocument(html, nonce) {
    var policy = '<meta http-equiv="Content-Security-Policy" content="' + escapeAttr(csp) + '">';
    var source = html || '';
    // Never search untrusted markup for an insertion point. A fake <head> inside a
    // comment could otherwise swallow the policy and bootstrap. The controlled
    // prefix is parsed before every untrusted byte, so later malformed tags can
    // only operate inside the already-sandboxed, policy-constrained document.
    return '<!doctype html><html><head>' + policy + bootstrap(nonce) +
      '</head><body>' + source + '</body></html>';
  }

  function randomNonce() {
    if (self.crypto && crypto.getRandomValues) {
      var bytes = new Uint8Array(16);
      crypto.getRandomValues(bytes);
      return Array.prototype.map.call(bytes, function (b) { return b.toString(16).padStart(2, '0'); }).join('');
    }
    return Date.now().toString(36) + Math.random().toString(36).slice(2);
  }

  function clearBlob(iframe, state) {
    var url = state && state.blobUrl;
    if (!url) url = iframe && iframe.dataset ? iframe.dataset.blobUrl : null;
    if (!url) return;
    URL.revokeObjectURL(url);
    if (state) state.blobUrl = null;
    if (iframe.dataset.blobUrl === url) delete iframe.dataset.blobUrl;
  }

  function clearTimer(state) {
    if (!state || !state.timer) return;
    clearTimeout(state.timer);
    state.timer = null;
  }

  function clearAttempt(iframe, state, revokeBlob) {
    if (!state) return;
    clearTimer(state);
    if (state.loadHandler) {
      iframe.removeEventListener('load', state.loadHandler);
      state.loadHandler = null;
    }
    if (state.errorHandler) {
      iframe.removeEventListener('error', state.errorHandler);
      state.errorHandler = null;
    }
    if (revokeBlob) clearBlob(iframe, state);
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

  function isMobileWebView() {
    var ua = navigator.userAgent || '';
    return !!(navigator.userAgentData && navigator.userAgentData.mobile) ||
      /Android|iPhone|iPad|iPod/i.test(ua);
  }

  function isIOSWebView() {
    return /iPhone|iPad|iPod/i.test(navigator.userAgent || '');
  }

  function platformLabel() {
    if (isIOSWebView()) return 'iOS WebKit';
    if (/Android/i.test(navigator.userAgent || '')) return 'Android WebView';
    return 'desktop WebView';
  }

  function isCurrent(iframe, state) {
    return frameStates.get(iframe) === state &&
      iframe.dataset.widgetGeneration === String(state.generation);
  }

  function detail(state, type) {
    return {
      type: type,
      generation: state.generation,
      mode: state.mode || 'none',
      attempt: state.attempt + 1,
      platform: platformLabel()
    };
  }

  function failFrame(iframe, state) {
    if (!isCurrent(iframe, state)) return;
    clearAttempt(iframe, state, true);
    iframe.dataset.widgetError = 'rendering-failed';
    iframe.dataset.widgetStage = 'failed';
    showStatus(iframe, 'Widget could not load securely. Renderers attempted: ' +
      state.modes.join(', ') + '. Platform: ' + platformLabel() + '.', true);
    var eventDetail = detail(state, 'mesh-widget-render-error');
    iframe.dispatchEvent(new CustomEvent('mesh-widget-error', { detail: eventDetail }));
    console.error('Widget rendering failed', eventDetail);
  }

  function acceptLoadedFrame(iframe, state) {
    if (!isCurrent(iframe, state) || iframe.dataset.widgetReady === 'true') return;
    clearAttempt(iframe, state, false);
    iframe.dataset.widgetReady = 'unconfirmed';
    iframe.dataset.widgetStage = 'loaded-unconfirmed';
    showStatus(iframe, 'Widget document loaded with ' + state.mode +
      ', but startup confirmation was not received on ' + platformLabel() + '.', false);
    var eventDetail = detail(state, 'mesh-widget-ready-unconfirmed');
    iframe.dispatchEvent(new CustomEvent('mesh-widget-warning', { detail: eventDetail }));
    console.warn('Widget loaded without startup confirmation', eventDetail);
  }

  function tryNext(iframe, state) {
    if (!isCurrent(iframe, state)) return;
    clearAttempt(iframe, state, true);
    state.attempt++;
    if (state.attempt >= state.modes.length) {
      failFrame(iframe, state);
      return;
    }

    state.mode = state.modes[state.attempt];
    var nonce = randomNonce() + '-' + state.generation + '-' + state.attempt;
    state.content = secureDocument(state.html, nonce);
    iframe.dataset.widgetNonce = nonce;
    iframe.dataset.widgetMode = state.mode;
    iframe.dataset.widgetStage = 'navigating';
    delete iframe.dataset.widgetReady;
    showStatus(iframe, 'Loading widget securely with ' + state.mode + ' (' +
      (state.attempt + 1) + ' of ' + state.modes.length + ')...', false);

    state.errorHandler = function () {
      if (isCurrent(iframe, state)) tryNext(iframe, state);
    };
    iframe.addEventListener('error', state.errorHandler);

    state.loadHandler = function () {
      if (!isCurrent(iframe, state)) return;
      iframe.dataset.widgetStage = 'document-loaded';
      if (state.mode === 'host') return;
      clearTimer(state);
      state.timer = setTimeout(function () {
        acceptLoadedFrame(iframe, state);
      }, confirmationTimeoutMs);
    };
    iframe.addEventListener('load', state.loadHandler);

    state.timer = setTimeout(function () {
      if (isCurrent(iframe, state)) tryNext(iframe, state);
    }, navigationTimeoutMs);

    try {
      if (state.mode === 'host') {
        iframe.removeAttribute('srcdoc');
        iframe.src = new URL('widget-host.html', document.baseURI).href + '#' + encodeURIComponent(nonce);
      } else if (state.mode === 'srcdoc') {
        iframe.removeAttribute('src');
        iframe.srcdoc = state.content;
      } else if (state.mode === 'blob') {
        iframe.removeAttribute('srcdoc');
        var blob = new Blob([state.content], { type: 'text/html' });
        state.blobUrl = URL.createObjectURL(blob);
        iframe.dataset.blobUrl = state.blobUrl;
        iframe.src = state.blobUrl;
      } else {
        iframe.removeAttribute('srcdoc');
        iframe.src = 'data:text/html;charset=utf-8,' + encodeURIComponent(state.content);
      }
    } catch (e) {
      console.warn('Widget renderer navigation failed',
        detail(state, 'mesh-widget-navigation-error'));
      tryNext(iframe, state);
    }
  }

  function onMessage(event) {
    var data = event.data;
    if (!data || (data.type !== 'mesh-widget-error' && data.type !== 'mesh-widget-ready' &&
      data.type !== 'mesh-widget-host-ready')) return;
    if (event.origin !== 'null') return;

    var frames = document.querySelectorAll('iframe[data-widget-nonce]');
    for (var i = 0; i < frames.length; i++) {
      var frame = frames[i];
      if (frame.contentWindow !== event.source || frame.dataset.widgetNonce !== data.nonce) continue;
      var state = frameStates.get(frame);
      if (!state || !isCurrent(frame, state)) continue;

      if (data.type === 'mesh-widget-host-ready') {
        if (state.mode !== 'host') continue;
        clearTimer(state);
        try {
          frame.contentWindow.postMessage({
            type: 'mesh-widget-render',
            nonce: data.nonce,
            html: state.content
          }, '*');
          frame.dataset.widgetStage = 'host-injected';
          state.timer = setTimeout(function () {
            if (isCurrent(frame, state)) tryNext(frame, state);
          }, navigationTimeoutMs);
        } catch (e) {
          tryNext(frame, state);
        }
        break;
      }

      clearAttempt(frame, state, false);
      if (data.type === 'mesh-widget-error') {
        frame.dataset.widgetError = 'script-error';
        frame.dataset.widgetStage = 'script-error';
        var location = data.line ? ' at line ' + data.line +
          (data.column ? ', column ' + data.column : '') : '';
        showStatus(frame, 'Widget script failed' + location + '. Renderer: ' +
          state.mode + '; platform: ' + platformLabel() + '.', true);
        var errorDetail = detail(state, 'mesh-widget-script-error');
        frame.dispatchEvent(new CustomEvent('mesh-widget-error', { detail: errorDetail }));
        console.error('Widget script failed', errorDetail,
          { line: data.line || 0, column: data.column || 0 });
      } else {
        var wasReady = frame.dataset.widgetReady === 'true';
        frame.dataset.widgetReady = 'true';
        frame.dataset.widgetStage = 'ready';
        if (!frame.dataset.widgetError) hideStatus(frame);
        if (!wasReady) {
          frame.dispatchEvent(new CustomEvent('mesh-widget-ready', {
            detail: detail(state, 'mesh-widget-ready')
          }));
        }
      }
      break;
    }
  }

  window.addEventListener('message', onMessage);

  return {
    setFrameHtml: function (iframe, html) {
      if (!iframe) return;
      try {
        var previous = frameStates.get(iframe);
        clearAttempt(iframe, previous, true);
        generation++;
        var ios = isIOSWebView();
        var state = {
          generation: generation,
          html: html || '',
          // Inline documents avoid MAUI's app:// handler in the nested frame.
          // The hosted page is only the final iOS compatibility fallback.
          modes: ios ? ['srcdoc', 'data', 'blob', 'host'] : ['srcdoc'],
          attempt: -1,
          mode: '',
          content: '',
          timer: null,
          loadHandler: null,
          errorHandler: null,
          blobUrl: null
        };
        frameStates.set(iframe, state);
        iframe.dataset.widgetGeneration = String(state.generation);
        delete iframe.dataset.widgetError;
        delete iframe.dataset.widgetReady;
        tryNext(iframe, state);
      } catch (e) {
        showStatus(iframe, 'Widget loader failed before navigation on ' +
          platformLabel() + '.', true);
        console.error('sandboxFrame.setFrameHtml failed without widget content');
        throw e;
      }
    },

    revokeFrame: function (iframe) {
      if (!iframe) return;
      try {
        var state = frameStates.get(iframe);
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
      } catch (e) {
        console.error('sandboxFrame.revokeFrame failed');
      }
    }
  };
})();
