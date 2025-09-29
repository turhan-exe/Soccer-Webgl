mergeInto(LibraryManager.library, {
  // Unity -> JS: maç sonucu JSON'unu bir DOM event ile yayınla
  SendMessageToJS: function (strPtr) {
    try {
      var msg = UTF8ToString(strPtr);

      if (typeof window !== 'undefined') {
        // CustomEvent fallback (eski şablonlar için)
        var ev;
        try {
          ev = new window.CustomEvent('unityMatchFinished', { detail: msg });
        } catch (_e) {
          // IE tarzı createEvent yolu (genelde lazım olmaz ama ES5 güvenli)
          try {
            ev = document.createEvent('CustomEvent');
            ev.initCustomEvent('unityMatchFinished', false, false, msg);
          } catch (__e) {
            ev = null;
          }
        }

        if (ev && typeof window.dispatchEvent === 'function') {
          window.dispatchEvent(ev);
        }
      }
    } catch (e) {
      try { console.error('[MatchBridge.jslib] SendMessageToJS error:', e); } catch(_) {}
    }
  },

  // Unity (C#) -> JS: React'in bulabilmesi için window.MatchBridgeAPI'yi yayınla
  PublishMatchBridgeAPI: function () {
    try {
      var getUnity = function () {
        if (typeof unityInstance !== 'undefined' && unityInstance) return unityInstance;
        if (typeof gameInstance !== 'undefined' && gameInstance) return gameInstance;
        if (typeof window !== 'undefined') {
          if (window.__MGX__ && window.__MGX__.unityInstance) return window.__MGX__.unityInstance;
          if (window.MGX && window.MGX.unityInstance) return window.MGX.unityInstance;
          if (window.unityInstance) return window.unityInstance;
          if (window.gameInstance) return window.gameInstance;
          if (window.UnityLoader && window.UnityLoader.instances && window.UnityLoader.instances.length > 0) {
            return window.UnityLoader.instances[0];
          }
        }
        return null;
      };

      var ensureString = function (data) {
        if (typeof data === 'string') return data;
        try { return JSON.stringify(data); } catch (_) { return String(data); }
      };

      var send = function (method, data) {
        try {
          var u = getUnity();
          if (!u || !u.SendMessage) {
            try { console.error('[MatchBridge.jslib] Unity instance not found. Cannot call', method); } catch(_) {}
            return false;
          }
          var payload = ensureString(data);
          var lenInfo = (payload && typeof payload.length === 'number') ? ('len=' + payload.length) : payload;
          try { console.log('[MatchBridge.jslib] SendMessage', method, lenInfo); } catch(_) {}
          u.SendMessage('MatchBridge', method, payload);
          return true;
        } catch (e) {
          try { console.error('[MatchBridge.jslib] SendMessage failed for', method, e); } catch(_) {}
          return false;
        }
      };

      if (typeof window !== 'undefined') {
        if (!window.MatchBridgeAPI) {
          window.MatchBridgeAPI = {
            showTeams:     function (payload) { return send('ShowTeamsFromJSON', payload); },
            loadSquads:    function (payload) { return send('LoadSquadsFromJSON', payload); },
            loadByKeys:    function (payload) { return send('LoadMatchFromJSON', payload); },
            publishTeams:  function (payload) { return send('LoadTeamsToSelectionFromJSON', payload); },
            preselectMenu: function (payload) { return send('PreselectMenuFromJSON', payload); },
            selectTeam:    function (side)    { return send('SelectUserTeam', side); },
            hideOverlay:   function ()        { return send('HideOverlay', ''); }
          };
          try { console.log('[MatchBridge.jslib] window.MatchBridgeAPI published'); } catch(_) {}
        }
      }
    } catch (e) {
      try { console.error('[MatchBridge.jslib] PublishMatchBridgeAPI error:', e); } catch(_) {}
    }
  }
});
