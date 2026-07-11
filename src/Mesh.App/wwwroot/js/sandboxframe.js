// Renders generated mini-app HTML into a sandboxed iframe.
//
// The frame keeps the restrictive sandbox (allow-scripts allow-forms
// allow-modals) WITHOUT allow-same-origin, so the widget script cannot reach
// the host document. To make inline <script> actually execute inside that
// sandbox on BOTH WebView2 (Windows) and Android System WebView, we assign the
// HTML to iframe.srcdoc rather than pointing iframe.src at a blob: URL.
//
// Why not blob: - a blob: URL gives the frame an opaque origin (good), but
// Android WebView (and some WebView2 builds) block blob: navigation inside a
// sandboxed iframe entirely, so the document never loads and its scripts never
// run. srcdoc loads the document in-place with the same opaque sandbox origin
// and reliably runs inline scripts under allow-scripts on every WebView we
// target. A blob: fallback is kept only for the rare engine that lacks srcdoc.
window.sandboxFrame = {
  // Load html into the sandboxed iframe. Prefers srcdoc; falls back to a blob:
  // URL if the engine does not support srcdoc.
  setFrameHtml: function (iframe, html) {
    if (!iframe) return;
    try {
      // Clean up any blob URL a previous (fallback) load may have left behind.
      var prev = iframe.dataset ? iframe.dataset.blobUrl : null;
      if (prev) { URL.revokeObjectURL(prev); if (iframe.dataset) { delete iframe.dataset.blobUrl; } }

      var content = html || '';

      if ('srcdoc' in iframe) {
        iframe.removeAttribute('src');
        iframe.srcdoc = content;
        return;
      }

      // Fallback for engines without srcdoc support.
      var blob = new Blob([content], { type: 'text/html' });
      var url = URL.createObjectURL(blob);
      if (iframe.dataset) { iframe.dataset.blobUrl = url; }
      iframe.src = url;
    } catch (e) {
      console.error('sandboxFrame.setFrameHtml failed', e);
    }
  },

  // Clear the iframe content and revoke any blob URL left by the fallback path.
  // Safe to call on a null element or a frame that was never set.
  revokeFrame: function (iframe) {
    if (!iframe) return;
    try {
      var url = iframe.dataset ? iframe.dataset.blobUrl : null;
      if (url) {
        URL.revokeObjectURL(url);
        if (iframe.dataset) { delete iframe.dataset.blobUrl; }
      }
      iframe.removeAttribute('src');
      if ('srcdoc' in iframe) { iframe.removeAttribute('srcdoc'); }
    } catch (e) {
      console.error('sandboxFrame.revokeFrame failed', e);
    }
  }
};
