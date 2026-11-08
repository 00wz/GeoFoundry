mergeInto(LibraryManager.library, {
  $GFPM: { next: 1, reads: {} },
  GF_PM_Begin__deps: ['$GFPM'],
  GF_PM_Begin: function(urlPtr, offsetPtr, length) {
    var url = UTF8ToString(urlPtr), offset = Number(UTF8ToString(offsetPtr));
    var id = GFPM.next++, state = { status: 0, total: 0, bytes: null, error: '', controller: new AbortController() };
    GFPM.reads[id] = state;
    (async function() {
      var reader, timeout = setTimeout(function() { state.controller.abort(); }, 30000);
      try {
        var response = await fetch(url, { headers: { Range: 'bytes=' + offset + '-' + (offset + length - 1) },
          signal: state.controller.signal, cache: 'no-store' });
        if (response.status !== 206) throw new Error('Server must support HTTP Range (206); received ' + response.status + '. Use the supplied range server.');
        var encoding = response.headers.get('Content-Encoding');
        if (encoding && encoding !== 'identity') throw new Error('Content-Encoding is not allowed for PMTiles.');
        var range = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(response.headers.get('Content-Range') || '');
        if (!range || Number(range[1]) !== offset || Number(range[2]) !== offset + length - 1 ||
            !Number.isSafeInteger(Number(range[3])) || Number(range[3]) < offset + length)
          throw new Error('Invalid/missing Content-Range. Cross-origin servers must expose this header through CORS.');
        state.total = Number(range[3]);
        // Never response.arrayBuffer(): reject oversized/truncated bodies while streaming.
        if (!response.body) throw new Error('Streaming Fetch API is required.');
        reader = response.body.getReader();
        var bytes = new Uint8Array(length), count = 0;
        while (true) {
          var part = await reader.read();
          if (part.done) break;
          if (part.value.length > length - count) throw new Error('HTTP range body exceeds requested length.');
          bytes.set(part.value, count); count += part.value.length;
        }
        if (count !== length) throw new Error('Truncated HTTP range.');
        if (GFPM.reads[id] !== state) return;
        state.bytes = bytes; state.status = 1;
      } catch (error) {
        state.error = String(error.message || error); state.status = 2;
      } finally {
        clearTimeout(timeout);
        state.controller.abort();
        if (reader) { try { await reader.cancel(); } catch (_) {} reader.releaseLock(); }
      }
    })();
    return id;
  },
  GF_PM_State__deps: ['$GFPM'],
  GF_PM_State: function(id) { return GFPM.reads[id].status; },
  GF_PM_Length__deps: ['$GFPM'],
  GF_PM_Length: function(id) { return GFPM.reads[id].total; },
  GF_PM_Error__deps: ['$GFPM'],
  GF_PM_Error: function(id, ptr, capacity) {
    var error = GFPM.reads[id].error;
    stringToUTF8(error, ptr, capacity);
    return Math.min(lengthBytesUTF8(error), capacity - 1);
  },
  GF_PM_Copy__deps: ['$GFPM'],
  GF_PM_Copy: function(id, ptr, length) { HEAPU8.set(GFPM.reads[id].bytes.subarray(0, length), ptr); },
  GF_PM_Release__deps: ['$GFPM'],
  GF_PM_Release: function(id) {
    var state = GFPM.reads[id];
    if (!state) return;
    delete GFPM.reads[id]; state.controller.abort(); state.bytes = null;
  }
});
