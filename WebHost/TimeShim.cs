namespace TixlWebHost;

/// <summary>
/// JavaScript that is registered (Page.addScriptToEvaluateOnNewDocument) before the page's own scripts run when the
/// operator's "Sync To TiXL Time" is on. It replaces the page's clock with TiXL's animation time, so the page only
/// animates while TiXL plays or Idle Motion is on, and follows the timeline when it is scrubbed.
/// Covered: performance.now, Date.now, requestAnimationFrame, setTimeout/setInterval, CSS and Web Animations,
/// SVG (SMIL) animations. Not covered: new Date() without arguments, video/audio playback, iframes.
/// </summary>
internal static class TimeShim
{
    public const string Source = """
        (function () {
          'use strict';
          if (window !== window.top || window.__tixl) return;

          // Virtual clock driven by TiXL. While active, the page never sees wall-clock time:
          // performance.now / Date.now, requestAnimationFrame, setTimeout / setInterval, CSS + Web Animations and SVG (SMIL)
          // animations all advance only when TiXL calls __tixlStep(timeInMilliseconds).
          var realNow = Date.now.bind(Date);
          var realCancelRaf = window.cancelAnimationFrame.bind(window);
          var realClearTimeout = window.clearTimeout.bind(window);

          var nextId = 1000000000;
          var timeMs = 0;
          var dateBase = realNow();
          var rafCallbacks = new Map();
          var timers = new Map();
          var firstSeek = true;
          var stepping = false;

          function reportError(e) {
            try { console.error(e && e.stack ? e.stack : String(e)); } catch (ignored) { }
          }

          function call(fn, args) {
            try {
              if (typeof fn === 'string') { (0, eval)(fn); } else { fn.apply(window, args || []); }
            } catch (e) { reportError(e); }
          }

          function delayOf(value) {
            var d = Number(value);
            return isFinite(d) && d > 0 ? d : 0;
          }

          performance.now = function () { return timeMs; };
          Date.now = function () { return dateBase + timeMs; };

          window.requestAnimationFrame = function (callback) {
            var id = ++nextId;
            rafCallbacks.set(id, callback);
            return id;
          };

          window.cancelAnimationFrame = function (id) {
            if (!rafCallbacks.delete(id)) realCancelRaf(id);
          };

          window.setTimeout = function (fn, delay) {
            var id = ++nextId;
            var d = delayOf(delay);
            timers.set(id, { due: timeMs + d, delay: d, repeat: false, fn: fn, args: Array.prototype.slice.call(arguments, 2) });
            return id;
          };

          window.setInterval = function (fn, delay) {
            var id = ++nextId;
            var d = Math.max(1, delayOf(delay));
            timers.set(id, { due: timeMs + d, delay: d, repeat: true, fn: fn, args: Array.prototype.slice.call(arguments, 2) });
            return id;
          };

          window.clearTimeout = window.clearInterval = function (id) {
            if (!timers.delete(id)) realClearTimeout(id);
          };

          function runTimers() {
            for (var guard = 0; guard < 2000; guard++) {
              var nextKey = null;
              var next = null;
              timers.forEach(function (timer, key) {
                if (timer.due <= timeMs && (next === null || timer.due < next.due)) { next = timer; nextKey = key; }
              });
              if (next === null) return;

              if (next.repeat) {
                next.due += next.delay;
                if (next.due < timeMs - 1000) next.due = timeMs + next.delay; // do not replay long gaps
              } else {
                timers.delete(nextKey);
              }
              call(next.fn, next.args);
            }
          }

          function seekAnimations(t) {
            var list = document.getAnimations ? document.getAnimations() : [];
            for (var i = 0; i < list.length; i++) {
              var a = list[i];
              try {
                // Animations that exist when sync starts follow the absolute TiXL time; later ones start at their creation.
                if (a.__tixlStart === undefined) a.__tixlStart = firstSeek ? 0 : t;
                if (a.playState === 'running' || a.playState === 'pending') a.pause();
                a.currentTime = t - a.__tixlStart;
              } catch (e) { }
            }
            firstSeek = false;

            var svgs = document.getElementsByTagName('svg');
            for (var j = 0; j < svgs.length; j++) {
              try { svgs[j].pauseAnimations(); svgs[j].setCurrentTime(t / 1000); } catch (e) { }
            }
          }

          window.__tixlStep = function (t) {
            t = Number(t);
            if (!isFinite(t) || stepping) return;

            stepping = true;
            try {
              timeMs = t;
              runTimers();
              try { seekAnimations(t); } catch (e) { reportError(e); }

              var pending = rafCallbacks;
              rafCallbacks = new Map();
              pending.forEach(function (callback) { call(callback, [t]); });
            } finally {
              stepping = false;
            }
          };

          window.__tixl = { step: window.__tixlStep };
        })();
        """;
}
