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
  // Keeps a scroll container pinned to the bottom as its content GROWS (streaming step trace, a long
  // reply that lays out or loads images/iframes after the initial render). This is more reliable than
  // a one-shot scroll: a ResizeObserver re-pins on every size change. It is "sticky" - if the user
  // scrolls up to read, it stops auto-pinning until they return near the bottom. Idempotent per element.
  autoScroll: function (el) {
    if (!el || el._meshAuto) return;
    el._meshAuto = true;
    el._stick = true;
    var nearBottom = function () {
      return el.scrollHeight - el.scrollTop - el.clientHeight < 140;
    };
    el.addEventListener('scroll', function () { el._stick = nearBottom(); });
    var pin = function () { if (el._stick) el.scrollTop = el.scrollHeight; };
    try {
      var ro = new ResizeObserver(pin);
      // Observe the inner content wrapper so its height changes fire; fall back to the container.
      ro.observe(el.firstElementChild || el);
      el._meshRo = ro;
    } catch (e) { /* ResizeObserver unavailable: the per-render scrollToBottom still runs */ }
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

  // WebView engines can reject file:// navigation before MAUI raises UrlLoading.
  // Intercept local links in the DOM and pass them to the native host explicitly.
  initFileLinks: function (dotnetRef) {
    if (document.documentElement.dataset.meshFileLinks) return;
    document.documentElement.dataset.meshFileLinks = '1';
    document.addEventListener('click', function (e) {
      var target = e.target;
      var link = target && target.closest ? target.closest('a[href]') : null;
      if (!link) return;
      var href = link.getAttribute('href') || '';
      if (!/^file:/i.test(href)) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      dotnetRef.invokeMethodAsync('OpenLocalFile', link.href || href)
        .catch(function (err) { console.error('OpenLocalFile failed', err); });
    }, true);
  },

  // Pointer-based topic reordering. The grip is the only drag surface, so normal row clicks and
  // touch scrolling remain available everywhere else. Targets are calculated from row bounds rather
  // than elementFromPoint, which is unreliable under pointer capture in WebView2.
  threadReorder: function (container, dotnetRef) {
    if (!container || container._meshThreadReorder) return;
    container._meshThreadReorder = true;
    var drag = null;

    function rows() {
      return Array.prototype.slice.call(container.querySelectorAll('[data-thread-id]'));
    }
    function clearMarkers() {
      container.querySelectorAll('.thread-drop-before,.thread-drop-after').forEach(function (row) {
        row.classList.remove('thread-drop-before', 'thread-drop-after');
      });
    }
    function markAt(clientY) {
      clearMarkers();
      var candidates = rows().filter(function (row) { return !drag || row !== drag.row; });
      if (!candidates.length) return;

      // Choose the closest row, including when the pointer is above or below the list.
      var target = candidates[0];
      var best = Infinity;
      candidates.forEach(function (row) {
        var rect = row.getBoundingClientRect();
        var distance = clientY < rect.top ? rect.top - clientY
          : clientY > rect.bottom ? clientY - rect.bottom : 0;
        if (distance < best) { best = distance; target = row; }
      });
      var rect = target.getBoundingClientRect();
      target.classList.add(clientY < rect.top + rect.height / 2
        ? 'thread-drop-before' : 'thread-drop-after');
    }

    container.addEventListener('pointerdown', function (e) {
      var grip = e.target && e.target.closest ? e.target.closest('[data-thread-grip]') : null;
      var row = grip && grip.closest('[data-thread-id]');
      if (!row || !container.contains(row) || (e.pointerType === 'mouse' && e.button !== 0)) return;
      e.preventDefault();
      e.stopPropagation();
      drag = {
        grip: grip, row: row, id: row.dataset.threadId, pointerId: e.pointerId,
        startY: e.clientY, lastY: e.clientY, active: false
      };
      try { grip.setPointerCapture(e.pointerId); } catch (_) { }
    });

    container.addEventListener('pointermove', function (e) {
      if (!drag || e.pointerId !== drag.pointerId) return;
      e.preventDefault();
      drag.lastY = e.clientY;
      if (!drag.active && Math.abs(e.clientY - drag.startY) < 4) return;
      if (!drag.active) {
        drag.active = true;
        drag.row.classList.add('thread-dragging');
        container.classList.add('thread-reordering');
      }
      markAt(e.clientY);
    });

    function finish(e, cancelled) {
      if (!drag || e.pointerId !== drag.pointerId) return;
      var current = drag;
      drag = null;
      var marked = container.querySelector('.thread-drop-before,.thread-drop-after');
      current.row.classList.remove('thread-dragging');
      container.classList.remove('thread-reordering');
      if (!cancelled && current.active && marked) {
        var before = marked.classList.contains('thread-drop-before');
        dotnetRef.invokeMethodAsync('ReorderThread', current.id, marked.dataset.threadId, before)
          .catch(function (err) { console.error('ReorderThread failed', err); });
      }
      clearMarkers();
      try { current.grip.releasePointerCapture(e.pointerId); } catch (_) { }
    }
    container.addEventListener('pointerup', function (e) { finish(e, false); });
    container.addEventListener('pointercancel', function (e) { finish(e, true); });
    container.addEventListener('lostpointercapture', function (e) {
      if (drag && e.pointerId === drag.pointerId) finish(e, false);
    });
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
