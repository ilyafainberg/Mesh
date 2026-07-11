// Securely loads generated mini-app HTML into an opaque-origin sandboxed iframe.
window.sandboxFrame = (function () {
  'use strict';

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

  function clearBlob(iframe) {
    var url = iframe && iframe.dataset ? iframe.dataset.blobUrl : null;
    if (url) {
      URL.revokeObjectURL(url);
      delete iframe.dataset.blobUrl;
    }
  }

  function onMessage(event) {
    var data = event.data;
    if (!data || (data.type !== 'mesh-widget-error' && data.type !== 'mesh-widget-ready')) return;
    var frames = document.querySelectorAll('iframe[data-widget-nonce]');
    for (var i = 0; i < frames.length; i++) {
      var frame = frames[i];
      if (frame.contentWindow !== event.source || frame.dataset.widgetNonce !== data.nonce) continue;
      if (data.type === 'mesh-widget-error') {
        var message = data.message || 'Unknown widget error';
        frame.dataset.widgetError = message;
        var oldNotice = frame.parentElement && frame.parentElement.querySelector('.widget-runtime-error');
        if (oldNotice) oldNotice.remove();
        if (frame.parentElement) {
          var notice = document.createElement('div');
          notice.className = 'widget-runtime-error';
          notice.setAttribute('role', 'alert');
          notice.style.cssText = 'padding:10px 12px;background:#fde7e9;color:#8a1520;font:13px system-ui,sans-serif;border-top:1px solid #e8a1a8;white-space:normal';
          notice.textContent = 'Widget failed to start: ' + message;
          frame.insertAdjacentElement('afterend', notice);
        }
        frame.dispatchEvent(new CustomEvent('mesh-widget-error', { detail: data }));
        console.error('Widget failed:', message, data.line || '', data.column || '');
      } else {
        frame.dataset.widgetReady = 'true';
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
        clearBlob(iframe);
        var nonce = randomNonce();
        iframe.dataset.widgetNonce = nonce;
        delete iframe.dataset.widgetError;
        delete iframe.dataset.widgetReady;
        var content = secureDocument(html, nonce);
        if ('srcdoc' in iframe) {
          iframe.removeAttribute('src');
          iframe.srcdoc = content;
          return;
        }
        var blob = new Blob([content], { type: 'text/html' });
        var url = URL.createObjectURL(blob);
        iframe.dataset.blobUrl = url;
        iframe.src = url;
      } catch (e) {
        console.error('sandboxFrame.setFrameHtml failed', e);
        throw e;
      }
    },

    revokeFrame: function (iframe) {
      if (!iframe) return;
      try {
        clearBlob(iframe);
        var notice = iframe.parentElement && iframe.parentElement.querySelector('.widget-runtime-error');
        if (notice) notice.remove();
        iframe.removeAttribute('src');
        if ('srcdoc' in iframe) iframe.removeAttribute('srcdoc');
        delete iframe.dataset.widgetNonce;
        delete iframe.dataset.widgetError;
        delete iframe.dataset.widgetReady;
      } catch (e) {
        console.error('sandboxFrame.revokeFrame failed', e);
      }
    }
  };
})();
