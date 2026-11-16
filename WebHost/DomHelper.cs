namespace TixlWebHost;

/// <summary>
/// JavaScript injected into every loaded page. It applies batches of DOM changes from the TiXL Web operators
/// (CssProperty, CssNumber, CssColor, ElementAttribute, ElementVisibility) and reports selectors that match nothing.
/// </summary>
internal static class DomHelper
{
    public const string Source = """
        (function () {
          'use strict';
          if (window.__tixlDom) return;

          // Applies batches of [kind, selector, name, value] operations to the page.
          //   kind 's': inline style property (value null removes it)   kind 'a': attribute (value null removes it)
          // Matching elements are cached per selector and re-queried when the document structure changes, so references
          // stay valid when the page adds or removes elements. Returns a JSON array of selectors that matched nothing.
          var cache = new Map();
          var observer = null;

          function invalidate() { cache.clear(); }

          function observe() {
            if (observer || !document.documentElement || typeof MutationObserver === 'undefined') return;
            observer = new MutationObserver(invalidate);
            observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'id'] });
          }

          function find(selector) {
            var list = cache.get(selector);
            if (list === undefined) {
              try { list = Array.prototype.slice.call(document.querySelectorAll(selector)); } catch (e) { list = null; }
              cache.set(selector, list);
            }
            return list;
          }

          function apply(ops, calls) {
            observe();
            var missing = [];
            for (var i = 0; i < ops.length; i++) {
              var op = ops[i];
              var kind = op[0], selector = op[1], name = op[2], value = op[3];
              var list = find(selector);
              if (list === null) {
                var label = '(invalid) ' + selector;
                if (missing.indexOf(label) < 0) missing.push(label);
                continue;
              }
              if (list.length === 0) {
                if (missing.indexOf(selector) < 0) missing.push(selector);
                continue;
              }
              if (kind === 't') {
                if (value === null) continue;
                for (var j = 0; j < list.length; j++) {
                  try { list[j].textContent = value; } catch (e) { }
                }
                continue;
              }
              for (var j = 0; j < list.length; j++) {
                var element = list[j];
                try {
                  if (kind === 's') {
                    if (value === null) element.style.removeProperty(name); else element.style.setProperty(name, value);
                  } else if (kind === 'a') {
                    if (value === null) element.removeAttribute(name); else element.setAttribute(name, value);
                  }
                } catch (e) { }
              }
            }
            if (calls) {
              for (var k = 0; k < calls.length; k++) {
                var fn = calls[k][0], raw = calls[k][1];
                try {
                  var f = window[fn];
                  if (typeof f !== 'function') continue;
                  var arg = raw;
                  try { arg = raw === '' ? undefined : JSON.parse(raw); } catch (e) { }
                  f(arg);
                } catch (e) { }
              }
            }
            return JSON.stringify(missing);
          }

          window.__tixlDom = { apply: apply };
        })();
        """;
}


