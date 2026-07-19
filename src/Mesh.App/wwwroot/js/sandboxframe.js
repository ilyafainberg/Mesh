// Securely loads generated mini-app HTML into an opaque-origin sandboxed iframe.
window.sandboxFrame = (function () {
  'use strict';

  var readyTimeoutMs = 5000;
  var generation = 0;
  var frameStates = new WeakMap();
  var csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
    "img-src data: blob:; media-src data: blob:; font-src data:; connect-src 'none'; " +
    "frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

  function escapeAttr(value) {
    return value.replace(/&/g, '&amp;').replace(/"/g, '&quot;');
  }

  function bootstrap(nonce) {
    // The nonce and event.source let the host associate diagnostics with this frame.
    return "<script>(function(){'use strict';" +
      "var n=" + JSON.stringify(nonce) + ";" +
      "var data=Object.create(null);" +
      "window.meshStorage={getItem:function(k){k=String(k);return Object.prototype.hasOwnProperty.call(data,k)?data[k]:null}," +
      "setItem:function(k,v){data[String(k)]=String(v)},removeItem:function(k){delete data[String(k)]}," +
      "clear:function(){data=Object.create(null)},key:function(i){return Object.keys(data)[i]||null}};" +
      "Object.defineProperty(window.meshStorage,'length',{get:function(){return Object.keys(data).length}});" +
      "function send(t,m,l,c){parent.postMessage({type:t,nonce:n,message:String(m||'Unknown widget error'),line:l||0,column:c||0},'*')}" +
      "addEventListener('error',function(e){send('mesh-widget-error',e.message,e.lineno,e.colno)});" +
      "addEventListener('unhandledrejection',function(e){send('mesh-widget-error',e.reason&&e.reason.message||e.reason)});" +
      "addEventListener('DOMContentLoaded',function(){parent.postMessage({type:'mesh-widget-ready',nonce:n},'*')});" +
      "})();<\/script>";
  }

  function secureDocument(html, nonce) {
    var policy = '<meta http-equiv="Content-Security-Policy" content="' + escapeAttr(csp) + '">';
    var injected = policy + bootstrap(nonce);
    var source = html || '';
    var head = /<head(?:\s[^>]*)?>/i;
    if (head.test(source)) return source.replace(head, function (m) { return m + injected; });
    var root = /<html(?:\s[^>]*)?>/i;
    if (root.test(source)) return source.replace(root, function (m) { return m + '<head>' + injected + '</head>'; });
    return '<!doctype html><html><head>' + injected + '</head><body>' + source + '</body></html>';
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
    if (url) {
      URL.revokeObjectURL(url);
      if (state) state.blobUrl = null;
      if (iframe.dataset.blobUrl === url) delete iframe.dataset.blobUrl;
    }
  }

  function clearAttempt(iframe, state, revokeBlob) {
    if (!state) return;
    if (state.timer) {
      clearTimeout(state.timer);
      state.timer = null;
    }
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
    notice.style.cssText = 'position:absolute;left:10px;right:10px;bottom:10px;z-index:2;' +
      'padding:9px 11px;border-radius:6px;background:' + (isError ? '#fde7e9' : '#fff4ce') + ';' +
      'color:' + (isError ? '#8a1520' : '#5c4813') + ';border:1px solid ' +
      (isError ? '#e8a1a8' : '#e5c365') + ';font:13px system-ui,sans-serif;white-space:normal';
    notice.textContent = message;
  }

  function hideStatus(iframe) {
    var notice = statusElement(iframe);
    if (!notice) return;
    notice.hidden = true;
    notice.textContent = '';
  }

  function isMobileWebView() {
    var ua = navigator.userAgent || '';
    return !!(navigator.userAgentData && navigator.userAgentData.mobile) ||
      /Android|iPhone|iPad|iPod/i.test(ua);
  }

  function isCurrent(iframe, state) {
    return frameStates.get(iframe) === state && iframe.dataset.widgetGeneration === String(state.generation);
  }

  function failFrame(iframe, state) {
    if (!isCurrent(iframe, state)) return;
    clearAttempt(iframe, state, true);
    iframe.removeAttribute('src');
    iframe.removeAttribute('srcdoc');
    iframe.dataset.widgetError = 'rendering-failed';
    showStatus(iframe, 'Widget preview could not be rendered securely.', true);
    iframe.dispatchEvent(new CustomEvent('mesh-widget-error', {
      detail: { type: 'mesh-widget-render-error', generation: state.generation }
    }));
  }

  function tryNext(iframe, state) {
    if (!isCurrent(iframe, state)) return;
    clearAttempt(iframe, state, true);
    state.attempt++;
    if (state.attempt >= state.modes.length) {
      failFrame(iframe, state);
      return;
    }

    if (state.attempt > 0) {
      showStatus(iframe, 'Trying the secure compatibility renderer...', false);
    }

    var mode = state.modes[state.attempt];
    var nonce = randomNonce() + '-' + state.generation + '-' + state.attempt;
    var content = secureDocument(state.html, nonce);
    iframe.dataset.widgetNonce = nonce;
    delete iframe.dataset.widgetReady;

    state.loadHandler = function () {
      if (!isCurrent(iframe, state)) {
        clearAttempt(iframe, state, false);
        return;
      }
      clearAttempt(iframe, state, false);
      iframe.dataset.widgetReady = 'true';
      hideStatus(iframe);
      iframe.dispatchEvent(new CustomEvent('mesh-widget-ready', {
        detail: { type: 'mesh-widget-load-ready', generation: state.generation }
      }));
    };
    state.errorHandler = function () {
      if (isCurrent(iframe, state)) tryNext(iframe, state);
    };
    iframe.addEventListener('load', state.loadHandler);
    iframe.addEventListener('error', state.errorHandler);
    state.timer = setTimeout(function () {
      if (isCurrent(iframe, state)) tryNext(iframe, state);
    }, readyTimeoutMs);

    try {
      if (mode === 'srcdoc') {
        iframe.removeAttribute('src');
        iframe.srcdoc = content;
      } else if (mode === 'blob') {
        iframe.removeAttribute('srcdoc');
        var blob = new Blob([content], { type: 'text/html' });
        state.blobUrl = URL.createObjectURL(blob);
        iframe.dataset.blobUrl = state.blobUrl;
        iframe.src = state.blobUrl;
      } else {
        iframe.removeAttribute('srcdoc');
        iframe.src = 'data:text/html;charset=utf-8,' + encodeURIComponent(content);
      }
    } catch (e) {
      console.warn('Widget renderer failed; trying fallback', mode, e);
      tryNext(iframe, state);
    }
  }

  function onMessage(event) {
    var data = event.data;
    if (!data || (data.type !== 'mesh-widget-error' && data.type !== 'mesh-widget-ready')) return;
    var frames = document.querySelectorAll('iframe[data-widget-nonce]');
    for (var i = 0; i < frames.length; i++) {
      var frame = frames[i];
      if (frame.contentWindow !== event.source || frame.dataset.widgetNonce !== data.nonce) continue;
      var state = frameStates.get(frame);
      if (!state || !isCurrent(frame, state)) continue;
      clearAttempt(frame, state, false);
      if (data.type === 'mesh-widget-error') {
        var message = data.message || 'Unknown widget error';
        frame.dataset.widgetError = message;
        showStatus(frame, 'Widget failed to start: ' + message, true);
        frame.dispatchEvent(new CustomEvent('mesh-widget-error', { detail: data }));
        console.error('Widget failed:', message, data.line || '', data.column || '');
      } else {
        var wasReady = frame.dataset.widgetReady === 'true';
        frame.dataset.widgetReady = 'true';
        if (!frame.dataset.widgetError) hideStatus(frame);
        if (!wasReady)
          frame.dispatchEvent(new CustomEvent('mesh-widget-ready', { detail: data }));
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
        var state = {
          generation: generation,
          html: html || '',
          modes: isMobileWebView() ? ['srcdoc', 'blob', 'data'] : ['srcdoc', 'blob'],
          attempt: -1,
          timer: null,
          loadHandler: null,
          errorHandler: null,
          blobUrl: null
        };
        frameStates.set(iframe, state);
        iframe.dataset.widgetGeneration = String(state.generation);
        delete iframe.dataset.widgetError;
        delete iframe.dataset.widgetReady;
        hideStatus(iframe);
        tryNext(iframe, state);
      } catch (e) {
        console.error('sandboxFrame.setFrameHtml failed', e);
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
        delete iframe.dataset.widgetError;
        delete iframe.dataset.widgetReady;
        hideStatus(iframe);
      } catch (e) {
        console.error('sandboxFrame.revokeFrame failed', e);
      }
    }
  };
})();
