// Enables pasting files into a composer textarea. Blazor does not surface clipboard file items, so we
// listen for the DOM paste event, read any pasted files as base64, and hand them to .NET which stages
// them (writing to a temp file so the existing path-based send flow works). Pasted text is left alone.
window.meshPaste = {
  // Attach a paste listener to a textarea. dotNetRef must expose an [JSInvokable] "OnPasteFile"
  // method taking (name, mime, base64Length? no -> name, mime, base64). Returns nothing.
  attach: function (el, dotNetRef) {
    if (!el || el.dataset.meshPasteBound) return;
    el.dataset.meshPasteBound = "1";
    el.addEventListener("paste", function (e) {
      try {
        var items = (e.clipboardData && e.clipboardData.items) || [];
        var files = [];
        for (var i = 0; i < items.length; i++) {
          if (items[i].kind === "file") {
            var f = items[i].getAsFile();
            if (f) files.push(f);
          }
        }
        if (files.length === 0) return; // plain text paste: let it through
        e.preventDefault();
        files.forEach(function (file) {
          var reader = new FileReader();
          reader.onload = function () {
            try {
              // reader.result is a data URL: strip the "data:...;base64," prefix.
              var res = reader.result || "";
              var comma = res.indexOf(",");
              var b64 = comma >= 0 ? res.substring(comma + 1) : res;
              var name = file.name || ("pasted-" + Date.now());
              var mime = file.type || "application/octet-stream";
              dotNetRef.invokeMethodAsync("OnPasteFile", name, mime, b64);
            } catch (err) { console.error("meshPaste read failed", err); }
          };
          reader.readAsDataURL(file);
        });
      } catch (err) {
        console.error("meshPaste paste handler failed", err);
      }
    });
  }
};
