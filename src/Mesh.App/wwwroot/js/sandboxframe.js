// Renders generated mini-app HTML into a sandboxed iframe via a Blob URL.
// The embedded MAUI BlazorWebView (WebView2 on Windows / Android WebView) does
// not run inline <script> from an iframe's srcdoc reliably, so we hand the HTML
// to the frame through a blob: URL instead. A blob: URL gets its own opaque
// origin, so scripts execute WITHOUT needing allow-same-origin - the restrictive
// sandbox (allow-scripts allow-forms allow-modals) is preserved.
window.sandboxFrame = {
  // Point the iframe at a fresh blob: URL built from html. Any previous blob URL
  // stashed on the element is revoked first so we do not leak object URLs.
  setFrameHtml: function (iframe, html) {
    if (!iframe) return;
    try {
      var prev = iframe.dataset ? iframe.dataset.blobUrl : null;
      if (prev) { URL.revokeObjectURL(prev); }
      var blob = new Blob([html || ''], { type: 'text/html' });
      var url = URL.createObjectURL(blob);
      if (iframe.dataset) { iframe.dataset.blobUrl = url; }
      iframe.src = url;
    } catch (e) {
      console.error('sandboxFrame.setFrameHtml failed', e);
    }
  },

  // Revoke the blob URL previously assigned to the iframe (if any) and clear its
  // src. Safe to call on a null element or a frame that was never set.
  revokeFrame: function (iframe) {
    if (!iframe) return;
    try {
      var url = iframe.dataset ? iframe.dataset.blobUrl : null;
      if (url) {
        URL.revokeObjectURL(url);
        if (iframe.dataset) { delete iframe.dataset.blobUrl; }
      }
      iframe.removeAttribute('src');
    } catch (e) {
      console.error('sandboxFrame.revokeFrame failed', e);
    }
  }
};
