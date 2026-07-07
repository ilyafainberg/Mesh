// Mesh UI helpers (auto-scroll chat panes to the bottom on new messages).
window.meshUI = {
  scrollToBottom: function (el) {
    if (!el) return;
    // Defer across two frames so newly rendered (and re-flowed) content is measured before we
    // scroll: a single frame can fire before a long markdown reply has finished laying out.
    requestAnimationFrame(function () {
      el.scrollTop = el.scrollHeight;
      requestAnimationFrame(function () {
        el.scrollTop = el.scrollHeight;
      });
    });
  },
  downloadFile: function (name, mime, b64) {
    try {
      var bin = atob(b64);
      var arr = new Uint8Array(bin.length);
      for (var i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
      var blob = new Blob([arr], { type: mime || 'application/octet-stream' });
      var url = URL.createObjectURL(blob);
      var a = document.createElement('a');
      a.href = url;
      a.download = name || 'file';
      document.body.appendChild(a);
      a.click();
      setTimeout(function () { URL.revokeObjectURL(url); a.remove(); }, 1000);
    } catch (e) { console.error('downloadFile failed', e); }
  },

  // Wire a chat composer textarea: Enter sends (calls .NET SendFromComposer), Shift+Enter inserts a
  // newline, and the box auto-grows up to maxRows lines then scrolls. Idempotent per element.
  composer: function (el, dotnetRef, maxRows) {
    if (!el || el.dataset.meshComposer) return;
    el.dataset.meshComposer = '1';
    var rows = maxRows || 5;
    function grow() {
      el.style.height = 'auto';
      var cs = getComputedStyle(el);
      var line = parseFloat(cs.lineHeight) || 20;
      var pad = parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom) + 2;
      var max = line * rows + pad;
      var h = Math.min(el.scrollHeight, max);
      el.style.height = h + 'px';
      el.style.overflowY = el.scrollHeight > max ? 'auto' : 'hidden';
    }
    el.addEventListener('input', grow);
    el.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        dotnetRef.invokeMethodAsync('SendFromComposer');
      } else if (e.key === 'Enter' && e.shiftKey) {
        setTimeout(grow, 0);
      }
    });
    grow();
  },

  // Reset a composer's height after its text is cleared programmatically (send clears the binding
  // without firing an input event).
  resetComposer: function (el) {
    if (!el) return;
    el.style.height = 'auto';
    el.style.overflowY = 'hidden';
  }
};
