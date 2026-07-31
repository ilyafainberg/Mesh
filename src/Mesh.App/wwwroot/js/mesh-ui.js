// Mesh UI helpers (auto-scroll chat panes to the bottom on new messages).
window.meshUI = {
  scrollToBottom: function (el) {
    if (!el) return;
    el._stick = true;
    var pin = function () {
      el.scrollTop = el.scrollHeight;
      if (el._meshUpdateScrollButton) el._meshUpdateScrollButton();
    };
    // Defer across two frames so newly rendered (and re-flowed) content is measured before we
    // scroll: a single frame can fire before a long markdown reply has finished laying out.
    requestAnimationFrame(function () {
      pin();
      requestAnimationFrame(pin);
    });
  },
  scrollToBottomIfPinned: function (el) {
    if (!el || el._stick === false) return;
    window.meshUI.scrollToBottom(el);
  },
  // Keeps a scroll container pinned to the bottom as its content GROWS (streaming step trace, a long
  // reply that lays out or loads images/iframes after the initial render). This is more reliable than
  // a one-shot scroll: a ResizeObserver re-pins on every size change. It is "sticky" - if the user
  // scrolls up to read, it stops auto-pinning until they return near the bottom. Idempotent per element.
  autoScroll: function (el, button) {
    if (!el) return;
    if (button) el._meshScrollButton = button;

    var bottomDistance = function () {
      return Math.max(0, el.scrollHeight - el.scrollTop - el.clientHeight);
    };
    var atBottom = function () { return bottomDistance() <= 24; };
    var updateButton = function () {
      var target = el._meshScrollButton;
      if (!target) return;
      var hidden = atBottom();
      target.hidden = hidden;
      target.setAttribute('aria-hidden', hidden ? 'true' : 'false');
    };
    el._meshUpdateScrollButton = updateButton;

    if (el._meshAuto) {
      updateButton();
      return;
    }

    el._meshAuto = true;
    el._stick = true;
    el.addEventListener('scroll', function () {
      el._stick = atBottom();
      updateButton();
    }, { passive: true });
    var pin = function () {
      if (el._stick) el.scrollTop = el.scrollHeight;
      updateButton();
    };
    try {
      var ro = new ResizeObserver(pin);
      // Observe the inner content wrapper so its height changes fire; fall back to the container.
      ro.observe(el.firstElementChild || el);
      el._meshRo = ro;
    } catch (e) { /* ResizeObserver unavailable: the per-render scrollToBottom still runs */ }
    requestAnimationFrame(pin);
  },

  // Thread screens live inside MobileShell's scrollable body. iOS treats that body as a native
  // scrolling layer, so a fixed child cannot reliably cover the sibling tab bar. Hide the tab bar
  // and suspend body scrolling while a thread is open, then restore the list position on close.
  setMobileThreadOpen: function (open) {
    var root = document.documentElement;
    var body = document.querySelector('.m-body');
    if (open) {
      if (body && !body.dataset.meshThreadScrollTop)
        body.dataset.meshThreadScrollTop = String(body.scrollTop || 0);
      if (body) body.scrollTop = 0;
      root.classList.add('mesh-mobile-thread-open');
      return;
    }

    root.classList.remove('mesh-mobile-thread-open');
    if (body && body.dataset.meshThreadScrollTop) {
      var top = Number(body.dataset.meshThreadScrollTop) || 0;
      delete body.dataset.meshThreadScrollTop;
      requestAnimationFrame(function () { body.scrollTop = top; });
    }
  },

  // Native-feeling mobile list actions. The row follows the finger while horizontal intent is
  // clear, vertical movement remains a normal scroll, and compositor-heavy action layers stay
  // hidden until an actual horizontal drag begins.
  swipeActions: function (container) {
    if (!container || container.dataset.meshSwipeActions) return;
    container.dataset.meshSwipeActions = '1';

    var actionWidth = 84;
    var drag = null;

    function rows() {
      return Array.prototype.slice.call(container.querySelectorAll('.mobile-swipe-shell'));
    }

    function content(row) {
      return row && row.querySelector('.mobile-swipe-content');
    }

    function action(row) {
      return row && row.querySelector('.mobile-swipe-action');
    }

    function setActionAccess(row, shown) {
      var button = action(row);
      if (!button) return;
      button.tabIndex = shown ? 0 : -1;
      button.setAttribute('aria-hidden', shown ? 'false' : 'true');
    }

    function applyOffset(row, offset, animate) {
      var foreground = content(row);
      if (!foreground) return;
      foreground.style.transition = animate ? 'transform .18s ease' : 'none';
      foreground.style.transform = 'translate3d(' + offset + 'px,0,0)';
      var shown = offset < -0.5;
      row.classList.toggle('swipe-action-visible', shown);
      if (!shown) row.classList.remove('swipe-revealed');
      setActionAccess(row, shown);
    }

    function settle(row, revealed) {
      if (!row) return;
      row.classList.remove('swipe-dragging');
      row.classList.toggle('swipe-revealed', revealed);
      applyOffset(row, revealed ? -actionWidth : 0, true);
      if (!revealed) {
        setTimeout(function () {
          if (!row.classList.contains('swipe-revealed'))
            row.classList.remove('swipe-action-visible');
        }, 190);
      }
    }

    function closeOthers(except) {
      rows().forEach(function (row) {
        if (row !== except && (row.classList.contains('swipe-revealed') ||
          row.classList.contains('swipe-action-visible')))
          settle(row, false);
      });
    }

    container.addEventListener('touchstart', function (event) {
      if (event.touches.length !== 1) return;
      var target = event.target;
      if (target && target.closest && target.closest('.mobile-swipe-action')) return;
      var row = target && target.closest ? target.closest('.mobile-swipe-shell') : null;
      if (!row || !container.contains(row)) return;
      var touch = event.touches[0];
      drag = {
        row: row,
        startX: touch.clientX,
        startY: touch.clientY,
        lastX: touch.clientX,
        lastTime: performance.now(),
        offset: row.classList.contains('swipe-revealed') ? -actionWidth : 0,
        axis: null
      };
    }, { passive: true });

    container.addEventListener('touchmove', function (event) {
      if (!drag || event.touches.length !== 1) return;
      var touch = event.touches[0];
      var dx = touch.clientX - drag.startX;
      var dy = touch.clientY - drag.startY;

      if (!drag.axis) {
        if (Math.max(Math.abs(dx), Math.abs(dy)) < 7) return;
        drag.axis = Math.abs(dx) > Math.abs(dy) * 1.15 ? 'x' : 'y';
        if (drag.axis === 'y') {
          settle(drag.row, false);
          drag = null;
          closeOthers(null);
          return;
        }
        closeOthers(drag.row);
        drag.row.classList.add('swipe-dragging', 'swipe-action-visible');
      }

      if (drag.axis !== 'x') return;
      event.preventDefault();
      var offset = Math.max(-actionWidth, Math.min(0, drag.offset + dx));
      applyOffset(drag.row, offset, false);
      drag.currentOffset = offset;
      drag.lastX = touch.clientX;
      drag.lastTime = performance.now();
    }, { passive: false });

    function finish(event, cancelled) {
      if (!drag) return;
      var current = drag;
      drag = null;
      if (current.axis !== 'x') return;

      var touch = event.changedTouches && event.changedTouches[0];
      var dx = touch ? touch.clientX - current.startX : 0;
      var offset = current.currentOffset == null ? current.offset : current.currentOffset;
      var reveal = !cancelled && (offset <= -actionWidth / 2 || dx < -36);
      if (!cancelled && current.offset < 0 && dx > 30) reveal = false;
      current.row._meshSuppressClickUntil = Date.now() + 500;
      settle(current.row, reveal);
    }

    container.addEventListener('touchend', function (event) { finish(event, false); }, { passive: true });
    container.addEventListener('touchcancel', function (event) { finish(event, true); }, { passive: true });

    // A tap on an open row closes it instead of navigating. Suppress the synthetic click after a
    // horizontal swipe so releasing the finger never opens the thread.
    container.addEventListener('click', function (event) {
      var target = event.target;
      var row = target && target.closest ? target.closest('.mobile-swipe-shell') : null;
      if (!row || !container.contains(row)) return;
      if (target.closest('.mobile-swipe-action') || target.closest('.swipe-accessible-pin')) return;
      if ((row._meshSuppressClickUntil || 0) > Date.now() || row.classList.contains('swipe-revealed')) {
        event.preventDefault();
        event.stopImmediatePropagation();
        settle(row, false);
      }
    }, true);
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

  initImagePreview: function (viewport, image) {
    if (!viewport || !image || viewport._meshImagePreview) return;
    var state = {
      scale: 1, x: 0, y: 0, pointers: new Map(), pinch: null, dragging: false
    };
    viewport._meshImagePreview = state;
    state.image = image;
    state.zoomLabel = viewport.closest('.image-preview')
      .querySelector('[data-image-preview-zoom]');

    function clamp() {
      if (state.scale <= 1) {
        state.x = 0;
        state.y = 0;
        return;
      }
      var imageWidth = image.clientWidth * state.scale;
      var imageHeight = image.clientHeight * state.scale;
      var maxX = Math.max(0, (imageWidth - viewport.clientWidth) / 2);
      var maxY = Math.max(0, (imageHeight - viewport.clientHeight) / 2);
      state.x = Math.max(-maxX, Math.min(maxX, state.x));
      state.y = Math.max(-maxY, Math.min(maxY, state.y));
    }

    function apply() {
      clamp();
      image.style.transform = 'translate3d(' + state.x + 'px,' + state.y +
        'px,0) scale(' + state.scale + ')';
      viewport.classList.toggle('image-preview-zoomed', state.scale > 1);
      viewport.classList.toggle('image-preview-dragging', state.dragging);
      if (state.zoomLabel) state.zoomLabel.textContent = Math.round(state.scale * 100) + '%';
    }

    function setScale(value, centerX, centerY) {
      var oldScale = state.scale;
      var next = Math.max(1, Math.min(8, value));
      if (next === oldScale) return;
      if (centerX != null && centerY != null && oldScale > 0) {
        var rect = viewport.getBoundingClientRect();
        var dx = centerX - (rect.left + rect.width / 2);
        var dy = centerY - (rect.top + rect.height / 2);
        var ratio = next / oldScale;
        state.x = dx - (dx - state.x) * ratio;
        state.y = dy - (dy - state.y) * ratio;
      }
      state.scale = next;
      apply();
    }

    function distance(a, b) {
      var dx = a.x - b.x;
      var dy = a.y - b.y;
      return Math.sqrt(dx * dx + dy * dy);
    }

    state.wheel = function (event) {
      event.preventDefault();
      setScale(state.scale * (event.deltaY < 0 ? 1.15 : 1 / 1.15),
        event.clientX, event.clientY);
    };
    state.pointerDown = function (event) {
      if (event.pointerType === 'mouse' && event.button !== 0) return;
      event.preventDefault();
      state.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      try { viewport.setPointerCapture(event.pointerId); } catch (_) { }
      if (state.pointers.size === 1) {
        state.lastX = event.clientX;
        state.lastY = event.clientY;
        state.dragging = state.scale > 1;
      } else if (state.pointers.size === 2) {
        var points = Array.from(state.pointers.values());
        state.pinch = {
          distance: distance(points[0], points[1]),
          scale: state.scale
        };
        state.dragging = false;
      }
      apply();
    };
    state.pointerMove = function (event) {
      if (!state.pointers.has(event.pointerId)) return;
      event.preventDefault();
      state.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      if (state.pointers.size === 2 && state.pinch) {
        var points = Array.from(state.pointers.values());
        var midpointX = (points[0].x + points[1].x) / 2;
        var midpointY = (points[0].y + points[1].y) / 2;
        setScale(state.pinch.scale * distance(points[0], points[1]) /
          Math.max(1, state.pinch.distance), midpointX, midpointY);
      } else if (state.pointers.size === 1 && state.scale > 1) {
        state.x += event.clientX - state.lastX;
        state.y += event.clientY - state.lastY;
        state.lastX = event.clientX;
        state.lastY = event.clientY;
        state.dragging = true;
        apply();
      }
    };
    state.pointerUp = function (event) {
      state.pointers.delete(event.pointerId);
      try { viewport.releasePointerCapture(event.pointerId); } catch (_) { }
      state.pinch = null;
      state.dragging = false;
      if (state.pointers.size === 1) {
        var remaining = Array.from(state.pointers.values())[0];
        state.lastX = remaining.x;
        state.lastY = remaining.y;
      }
      apply();
    };
    state.doubleClick = function (event) {
      event.preventDefault();
      setScale(state.scale > 1 ? 1 : 2, event.clientX, event.clientY);
    };
    state.resize = function () { apply(); };
    state.command = function (command) {
      if (command === 'in') setScale(state.scale * 1.25);
      else if (command === 'out') setScale(state.scale / 1.25);
      else if (command === 'reset') {
        state.scale = 1; state.x = 0; state.y = 0; apply();
      } else {
        var step = 48;
        if (command === 'left') state.x -= step;
        if (command === 'right') state.x += step;
        if (command === 'up') state.y -= step;
        if (command === 'down') state.y += step;
        apply();
      }
    };

    viewport.addEventListener('wheel', state.wheel, { passive: false });
    viewport.addEventListener('pointerdown', state.pointerDown);
    viewport.addEventListener('pointermove', state.pointerMove);
    viewport.addEventListener('pointerup', state.pointerUp);
    viewport.addEventListener('pointercancel', state.pointerUp);
    viewport.addEventListener('dblclick', state.doubleClick);
    window.addEventListener('resize', state.resize);
    apply();
  },

  imagePreviewCommand: function (viewport, command) {
    var state = viewport && viewport._meshImagePreview;
    if (state && state.command) state.command(command);
  },

  disposeImagePreview: function (viewport) {
    var state = viewport && viewport._meshImagePreview;
    if (!state) return;
    viewport.removeEventListener('wheel', state.wheel);
    viewport.removeEventListener('pointerdown', state.pointerDown);
    viewport.removeEventListener('pointermove', state.pointerMove);
    viewport.removeEventListener('pointerup', state.pointerUp);
    viewport.removeEventListener('pointercancel', state.pointerUp);
    viewport.removeEventListener('dblclick', state.doubleClick);
    window.removeEventListener('resize', state.resize);
    if (state.image) state.image.style.transform = '';
    delete viewport._meshImagePreview;
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

  // Re-measure after Blazor swaps in a saved draft without raising a browser input event.
  resizeComposer: function (el, maxRows) {
    if (!el) return;
    requestAnimationFrame(function () {
      el.style.height = 'auto';
      var cs = getComputedStyle(el);
      var line = parseFloat(cs.lineHeight) || 20;
      var pad = parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom) + 2;
      var max = line * (maxRows || 5) + pad;
      var h = Math.min(el.scrollHeight, max);
      el.style.height = h + 'px';
      el.style.overflowY = el.scrollHeight > max ? 'auto' : 'hidden';
    });
  },
  // Reset a composer's height after its text is cleared programmatically (send clears the binding
  // without firing an input event).
  resetComposer: function (el) {
    if (!el) return;
    el.style.height = 'auto';
    el.style.overflowY = 'hidden';
  },

  // Dismiss the software keyboard before a native picker takes over. Waiting for one animation
  // beat prevents WKWebView from restoring a half-shifted viewport when the picker closes.
  prepareForNativePicker: function (el) {
    if (el && typeof el.blur === 'function') el.blur();
    return new Promise(function (resolve) {
      requestAnimationFrame(function () { setTimeout(resolve, 100); });
    });
  },


  // Highlight already-normalized tool details. Text is read from textContent on every pass so
  // switching between formatted and raw views never re-highlights generated markup.
  highlightCode: function (root) {
    if (!root || !window.hljs) return;
    var maxHighlightChars = 50000;
    var autoLanguages = [
      'json', 'powershell', 'dos', 'bash', 'python', 'csharp', 'javascript',
      'typescript', 'xml', 'css', 'sql', 'markdown'
    ];
    root.querySelectorAll('code[data-mesh-highlight]').forEach(function (code) {
      var source = code.textContent || '';
      var language = code.dataset.language || '';
      if (source.length > maxHighlightChars || language === 'plaintext') {
        // Highlight.js expands text into many DOM spans and auto-detects against every grammar.
        // Keeping very large or plain output as text avoids WebView memory spikes on mobile.
        code.textContent = source;
        code.classList.add('hljs');
        return;
      }
      try {
        var result = language && window.hljs.getLanguage(language)
          ? window.hljs.highlight(source, { language: language, ignoreIllegals: true })
          : window.hljs.highlightAuto(source, autoLanguages);
        code.innerHTML = result.value;
        code.classList.add('hljs');
      } catch (error) {
        code.textContent = source;
        console.warn('Tool detail highlighting failed', error);
      }
    });
  }
};
