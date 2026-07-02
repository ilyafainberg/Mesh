// Mesh UI helpers (auto-scroll chat panes to the bottom on new messages).
window.meshUI = {
  scrollToBottom: function (el) {
    if (!el) return;
    // Defer to the next frame so newly rendered content is measured.
    requestAnimationFrame(function () {
      el.scrollTop = el.scrollHeight;
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
  }
};
