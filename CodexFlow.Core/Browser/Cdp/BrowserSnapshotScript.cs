// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodexFlow.Core.Protocols;

namespace CodexFlow.Core.Browser.Cdp;

/// <summary>
/// Isolated-world DOM interactive snapshot, ref verification, and active-element inspection scripts.
/// Refs are stored exclusively inside an isolated world's private <c>Map</c> (invisible and tamper-proof
/// to page scripts), supporting open Shadow DOM and clickjacking detection via deep <c>elementFromPoint</c>.
/// </summary>
internal static partial class BrowserSnapshotScript
{
    private const string SharedHelpersJs = """
  const SENSITIVE_SUBMIT_RE = /(支付|付款|转账|删除|注销|提交订单|确认下单|立即购买|购买|下单|提现|充值|授权|pay\b|payment|purchase|checkout|delete\b|transfer|withdraw|subscribe|place\s+order|buy\s+now|confirm)/i;
  const SENSITIVE_URL_RE = /(checkout|pay|payment|order|transfer|withdraw|billing|delete|account\/close)/i;
  const SECRET_FIELD_RE = /(password|passwd|pwd|passcode|pin\b|cvv|cvc|otp|one[-_ ]?time|verification[-_ ]?code|2fa|totp|mfa|captcha|cc[-_ ]?(number|csc|exp|code)|security[-_ ]?code|密码|口令|验证码|安全码|支付密码|动态码)/i;
  const SECRET_AUTOCOMPLETE_RE = /(current-password|new-password|one-time-code|cc-number|cc-csc|cc-exp|cc-name)/i;

  const inferRole = (el) => {
    const explicit = (el.getAttribute('role') || '').trim().toLowerCase();
    if (explicit) return explicit;
    const tag = el.tagName.toLowerCase();
    if (tag === 'a') return 'link';
    if (tag === 'button') return 'button';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'select') return 'combobox';
    if (tag === 'input') {
      const t = (el.type || 'text').toLowerCase();
      if (t === 'submit' || t === 'button' || t === 'reset') return 'button';
      if (t === 'checkbox') return 'checkbox';
      if (t === 'radio') return 'radio';
      if (t === 'search') return 'searchbox';
      if (t === 'file') return 'file_input';
      return 'textbox';
    }
    return tag;
  };

  const getLabelWithoutValue = (el) => {
    const aria = (el.getAttribute('aria-label') || '').trim();
    if (aria) return aria;
    const labelledBy = el.getAttribute('aria-labelledby');
    if (labelledBy) {
      const parts = labelledBy.split(/\s+/).map(id => document.getElementById(id)?.innerText?.trim()).filter(Boolean);
      if (parts.length) return parts.join(' ');
    }
    const id = el.getAttribute('id');
    if (id) {
      const root = el.getRootNode && el.getRootNode();
      const scope = (root && root.querySelector) ? root : document;
      const label = scope.querySelector(`label[for="${CSS.escape(id)}"]`);
      if (label && label.innerText) return label.innerText.trim();
    }
    const placeholder = (el.getAttribute('placeholder') || '').trim();
    const title = (el.getAttribute('title') || '').trim();
    const alt = (el.getAttribute('alt') || '').trim();
    const text = (el.tagName.toLowerCase() === 'input' || el.tagName.toLowerCase() === 'textarea')
      ? ''
      : (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
    return (text || placeholder || title || alt).slice(0, 120);
  };

  const isSecretField = (el) => {
    if (!el || el.nodeType !== 1) return false;
    const tag = el.tagName.toLowerCase();
    const role = inferRole(el);
    const editable = tag === 'input' || tag === 'textarea' || el.isContentEditable || role === 'textbox' || role === 'searchbox';
    if (!editable) return false;
    const inputType = tag === 'input' ? (el.type || 'text').toLowerCase() : '';
    if (inputType === 'password') return true;
    const ac = (el.getAttribute('autocomplete') || '').toLowerCase();
    if (SECRET_AUTOCOMPLETE_RE.test(ac)) return true;
    const label = getLabelWithoutValue(el);
    const meta = `${el.id || ''} ${el.getAttribute('name') || ''} ${el.getAttribute('data-testid') || ''} ${ac} ${label}`;
    return SECRET_FIELD_RE.test(meta);
  };

  const isSensitiveSubmitElement = (el) => {
    if (!el || el.nodeType !== 1) return false;
    const tag = el.tagName.toLowerCase();
    const inputType = tag === 'input' ? (el.type || '').toLowerCase() : '';
    const role = inferRole(el);
    const label = getLabelWithoutValue(el);
    const isClickableSubmit = role === 'button' || role === 'link' || inputType === 'submit' || tag === 'button';
    if (isClickableSubmit && SENSITIVE_SUBMIT_RE.test(label)) return true;
    const form = el.closest ? el.closest('form') : null;
    if (form) {
      const formAction = `${form.getAttribute('action') || ''} ${window.location.href || ''}`;
      if (SENSITIVE_URL_RE.test(formAction) && (inputType === 'submit' || role === 'button' || tag === 'button')) {
        return true;
      }
    }
    return false;
  };

  const getViewportRect = (el) => {
    const r = el.getBoundingClientRect();
    if (!el.ownerDocument || el.ownerDocument === document) {
      return { left: r.left, top: r.top, right: r.right, bottom: r.bottom, width: r.width, height: r.height };
    }
    let left = r.left;
    let top = r.top;
    let win = el.ownerDocument.defaultView;
    let guard = 0;
    while (win && guard < 6) {
      let frameEl = null;
      try { frameEl = win.frameElement; } catch { frameEl = null; }
      if (!frameEl) break;
      const fr = frameEl.getBoundingClientRect();
      left += fr.left + (frameEl.clientLeft || 0);
      top += fr.top + (frameEl.clientTop || 0);
      if (frameEl.ownerDocument === document) break;
      win = frameEl.ownerDocument && frameEl.ownerDocument.defaultView;
      guard++;
    }
    return { left, top, right: left + r.width, bottom: top + r.height, width: r.width, height: r.height };
  };

  const deepElementFromPointDoc = (doc, x, y) => {
    if (!doc || typeof doc.elementFromPoint !== 'function') return null;
    let el = doc.elementFromPoint(x, y);
    let guard = 0;
    while (el && el.shadowRoot && guard < 16) {
      const inner = el.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === el) break;
      el = inner;
      guard++;
    }
    return el;
  };

  const deepElementFromPoint = (x, y) => deepElementFromPointDoc(document, x, y);

  const isVisible = (el) => {
    if (!el || el.nodeType !== 1) return false;
    if (el.tagName.toLowerCase() === 'input' && (el.type || '').toLowerCase() === 'file') {
      return !el.disabled;
    }
    const win = el.ownerDocument?.defaultView || window;
    const style = win.getComputedStyle(el);
    if (!style || style.display === 'none' || style.visibility === 'hidden' || parseFloat(style.opacity || '1') === 0) {
      return false;
    }
    const rect = el.getBoundingClientRect();
    return rect.width > 1 && rect.height > 1;
  };

  const isObscuredInHierarchy = (el) => {
    const localRect = el.getBoundingClientRect();
    if (localRect.width <= 1 || localRect.height <= 1) return true;
    let curEl = el;
    let curX = Math.round(localRect.left + localRect.width / 2);
    let curY = Math.round(localRect.top + localRect.height / 2);
    let guard = 0;
    while (curEl && guard < 6) {
      const doc = curEl.ownerDocument || document;
      const hit = deepElementFromPointDoc(doc, curX, curY);
      if (hit && hit !== curEl && !curEl.contains(hit) && !hit.contains(curEl)) {
        return true;
      }
      if (doc === document) break;
      let frameEl = null;
      try { frameEl = doc.defaultView ? doc.defaultView.frameElement : null; } catch { frameEl = null; }
      if (!frameEl) break;
      const fr = frameEl.getBoundingClientRect();
      curX = Math.round(fr.left + (frameEl.clientLeft || 0) + curX);
      curY = Math.round(fr.top + (frameEl.clientTop || 0) + curY);
      curEl = frameEl;
      guard++;
    }
    return false;
  };
""";

    public static string BuildExtractInteractiveSnapshotJs(int startIndex = 0) => $$"""
(() => {
{{SharedHelpersJs}}
  const MAX_ELEMENTS = 150;
  const MAX_SECRET_RECTS = 60;
  const MAX_TEXT_CHARS = 10000;

  const isInteractive = (el) => {
    const tag = el.tagName.toLowerCase();
    if (['a', 'button', 'input', 'textarea', 'select', 'summary', 'option'].includes(tag)) {
      if (tag === 'input' && (el.type || '').toLowerCase() === 'hidden') return false;
      return !el.disabled;
    }
    const role = (el.getAttribute('role') || '').toLowerCase();
    if (['button', 'link', 'textbox', 'searchbox', 'checkbox', 'radio', 'combobox', 'listbox', 'menuitem', 'tab', 'switch', 'option'].includes(role)) {
      return el.getAttribute('aria-disabled') !== 'true';
    }
    if (el.isContentEditable) return true;
    const tabIndex = el.getAttribute('tabindex');
    return tabIndex !== null && tabIndex !== '-1';
  };

  const refMap = new Map();
  globalThis.__cfRefMap = refMap;
  const elements = [];
  const secretRects = [];
  const crossOriginFrames = [];
  const inlineFrameTexts = [];
  let counter = {{startIndex}};

  const walk = (root, frameDepth, frameLabel) => {
    if (!root) return;
    const doc = root.ownerDocument || document;
    const NodeFilterRef = doc.defaultView?.NodeFilter || NodeFilter;
    const walker = doc.createTreeWalker(root, NodeFilterRef.SHOW_ELEMENT);
    let node = walker.nextNode();
    while (node) {
      if (secretRects.length < MAX_SECRET_RECTS && isSecretField(node) && isVisible(node)) {
        const sr = getViewportRect(node);
        if (sr.width > 1 && sr.height > 1) {
          secretRects.push({
            x: Math.round(sr.left),
            y: Math.round(sr.top),
            width: Math.round(sr.width),
            height: Math.round(sr.height)
          });
        }
      }

      if (elements.length < MAX_ELEMENTS && isInteractive(node) && isVisible(node)) {
        counter += 1;
        const refId = `e${counter}`;
        refMap.set(refId, node);
        const vr = getViewportRect(node);
        const tag = node.tagName.toLowerCase();
        const inputType = tag === 'input' ? (node.type || 'text').toLowerCase() : null;
        const role = inferRole(node);
        const isPassword = isSecretField(node);
        const name = getLabelWithoutValue(node);
        const isSensitiveSubmit = isSensitiveSubmitElement(node);
        let value = null;
        if (!isPassword && (tag === 'input' || tag === 'textarea' || tag === 'select')) {
          value = String(node.value ?? '').slice(0, 120);
        }
        elements.push({
          ref: refId,
          role,
          tag,
          name,
          value,
          href: tag === 'a' ? (node.getAttribute('href') || '').slice(0, 200) : null,
          inputType,
          isPassword,
          isSensitiveSubmit,
          x: Math.round(vr.left + vr.width / 2),
          y: Math.round(vr.top + vr.height / 2),
          width: Math.round(vr.width),
          height: Math.round(vr.height),
          frameLabel: frameLabel || null
        });
      }

      if (node.shadowRoot) {
        walk(node.shadowRoot, frameDepth, frameLabel);
      }

      const tagLower = node.tagName.toLowerCase();
      if ((tagLower === 'iframe' || tagLower === 'frame') && frameDepth < 3 && isVisible(node)) {
        const ifrRect = getViewportRect(node);
        let childDoc = null;
        try { childDoc = node.contentDocument; } catch { childDoc = null; }
        if (childDoc && (childDoc.body || childDoc.documentElement)) {
          const childText = (childDoc.body?.innerText || '').replace(/\n{3,}/g, '\n\n').trim();
          if (childText) {
            inlineFrameTexts.push(childText.slice(0, 2500));
          }
          walk(childDoc.body || childDoc.documentElement, frameDepth + 1, 'iframe');
        } else {
          crossOriginFrames.push({
            src: String(node.getAttribute('src') || '').slice(0, 300),
            url: String(node.src || '').slice(0, 300),
            name: String(node.getAttribute('name') || node.getAttribute('id') || '').slice(0, 80),
            x: Math.round(ifrRect.left),
            y: Math.round(ifrRect.top),
            width: Math.round(ifrRect.width),
            height: Math.round(ifrRect.height)
          });
        }
      }

      node = walker.nextNode();
    }
  };

  walk(document.body || document.documentElement, 0, null);

  const mainText = (document.body?.innerText || '').replace(/\n{3,}/g, '\n\n').trim();
  const combinedText = inlineFrameTexts.length > 0
    ? [mainText, ...inlineFrameTexts.map((t, i) => `[iframe#${i + 1}]\n${t}`)].filter(Boolean).join('\n\n')
    : mainText;
  const pageText = combinedText.length > MAX_TEXT_CHARS
    ? combinedText.slice(0, MAX_TEXT_CHARS) + '\n... (页面正文已截断)'
    : combinedText;

  return JSON.stringify({
    url: window.location.href,
    title: document.title || '',
    pageText,
    elements,
    secretRects,
    crossOriginFrames
  });
})()
""";

    public static string BuildExtractSecretRectsJs() => $$"""
(() => {
{{SharedHelpersJs}}
  const MAX_SECRET_RECTS = 60;
  const rects = [];
  const collect = (root, depth) => {
    if (!root || rects.length >= MAX_SECRET_RECTS) return;
    const doc = root.ownerDocument || document;
    const NodeFilterRef = doc.defaultView?.NodeFilter || NodeFilter;
    const walker = doc.createTreeWalker(root, NodeFilterRef.SHOW_ELEMENT);
    let node = walker.nextNode();
    while (node && rects.length < MAX_SECRET_RECTS) {
      if (isSecretField(node) && isVisible(node)) {
        const vr = getViewportRect(node);
        if (vr.width > 1 && vr.height > 1) {
          rects.push({
            x: Math.round(vr.left),
            y: Math.round(vr.top),
            width: Math.round(vr.width),
            height: Math.round(vr.height)
          });
        }
      }
      if (node.shadowRoot) {
        collect(node.shadowRoot, depth);
      }
      const tagLower = node.tagName.toLowerCase();
      if ((tagLower === 'iframe' || tagLower === 'frame') && depth < 3 && isVisible(node)) {
        let childDoc = null;
        try { childDoc = node.contentDocument; } catch { childDoc = null; }
        if (childDoc && (childDoc.body || childDoc.documentElement)) {
          collect(childDoc.body || childDoc.documentElement, depth + 1);
        }
      }
      node = walker.nextNode();
    }
  };
  collect(document.body || document.documentElement, 0);
  return JSON.stringify(rects);
})()
""";

    public static string BuildInstallVisualOverlayJs(string overlaySpecJson) => $$"""
(() => {
  document.querySelectorAll('[data-cf-visual-overlay]').forEach(el => el.remove());
  const spec = JSON.parse("{{EscapeJsLiteral(overlaySpecJson)}}");
  const vw = Math.max(window.innerWidth || document.documentElement?.clientWidth || 1280, 1);
  const vh = Math.max(window.innerHeight || document.documentElement?.clientHeight || 800, 1);
  const dpr = Math.min(window.devicePixelRatio || 1, 2);

  const host = document.createElement('codexflow-visual-layer');
  host.setAttribute('data-cf-visual-overlay', '1');
  const shadow = host.attachShadow({ mode: 'closed' });
  const cssText = ':host { position: fixed !important; inset: 0 !important; width: 100vw !important; height: 100vh !important; z-index: 2147483647 !important; pointer-events: none !important; display: block !important; background: transparent !important; } canvas { display: block; width: 100vw; height: 100vh; }';
  if (typeof CSSStyleSheet === 'function' && shadow.adoptedStyleSheets) {
    try {
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(cssText);
      shadow.adoptedStyleSheets = [sheet];
    } catch {
      const style = document.createElement('style');
      style.textContent = cssText;
      shadow.appendChild(style);
    }
  } else {
    const style = document.createElement('style');
    style.textContent = cssText;
    shadow.appendChild(style);
  }

  const canvas = document.createElement('canvas');
  canvas.width = Math.round(vw * dpr);
  canvas.height = Math.round(vh * dpr);
  shadow.appendChild(canvas);

  const ctx = canvas.getContext('2d');
  if (ctx) {
    ctx.scale(dpr, dpr);
    ctx.font = 'bold 11px ui-monospace, SFMono-Regular, Menlo, Consolas, monospace';
    ctx.textBaseline = 'middle';

    const annotations = Array.isArray(spec.annotations) ? spec.annotations : [];
    for (const a of annotations) {
      if (a.width <= 1 || a.height <= 1) continue;
      if (a.x + a.width < 0 || a.y + a.height < 0 || a.x > vw || a.y > vh) continue;
      const stroke = a.isSensitive ? '#f59e0b' : (a.role === 'textbox' || a.role === 'searchbox' || a.role === 'combobox' ? '#10b981' : '#0ea5e9');
      ctx.lineWidth = 2;
      ctx.strokeStyle = stroke;
      ctx.strokeRect(a.x, a.y, a.width, a.height);

      const label = `[${a.ref}]`;
      const tw = ctx.measureText(label).width;
      const badgeW = tw + 8;
      const badgeH = 16;
      const bx = Math.max(0, Math.min(vw - badgeW, a.x));
      const by = a.y >= badgeH + 2 ? a.y - badgeH : Math.max(0, Math.min(vh - badgeH, a.y));
      ctx.fillStyle = '#0f172a';
      ctx.fillRect(bx, by, badgeW, badgeH);
      ctx.strokeStyle = stroke;
      ctx.lineWidth = 1;
      ctx.strokeRect(bx, by, badgeW, badgeH);
      ctx.fillStyle = '#f8fafc';
      ctx.fillText(label, bx + 4, by + badgeH / 2);
    }

    // Draw opaque secret masks LAST so masked fields can never be occluded/exposed by annotations.
    const secrets = Array.isArray(spec.secretRects) ? spec.secretRects : [];
    for (const r of secrets) {
      if (r.width <= 1 || r.height <= 1) continue;
      const pad = 3;
      const rx = Math.max(0, r.x - pad);
      const ry = Math.max(0, r.y - pad);
      const rw = Math.min(vw - rx, r.width + pad * 2);
      const rh = Math.min(vh - ry, r.height + pad * 2);
      if (rw <= 2 || rh <= 2) continue;
      ctx.fillStyle = '#0f172a';
      ctx.fillRect(rx, ry, rw, rh);
      ctx.lineWidth = 2;
      ctx.strokeStyle = '#f59e0b';
      ctx.strokeRect(rx, ry, rw, rh);
      if (rw >= 64 && rh >= 16) {
        ctx.fillStyle = '#fbbf24';
        ctx.fillText('[PROTECTED]', rx + 6, ry + rh / 2);
      }
    }
  }

  (document.documentElement || document.body).appendChild(host);
  return new Promise((resolve) => {
    let settled = false;
    const finish = () => {
      if (!settled) {
        settled = true;
        resolve('ok');
      }
    };
    setTimeout(finish, 250);
    requestAnimationFrame(() => requestAnimationFrame(finish));
  });
})()
""";

    public static string BuildRemoveVisualOverlayJs() => """
(() => {
  document.querySelectorAll('[data-cf-visual-overlay]').forEach(el => el.remove());
  return 'ok';
})()
""";

    public static string BuildSafeSerializeObjectFunctionJs() => """
function() {
  const seen = new WeakSet();
  const summarize = (val, depth) => {
    if (val === null) return null;
    const t = typeof val;
    if (t === 'undefined') return '[undefined]';
    if (t === 'string' || t === 'number' || t === 'boolean') return val;
    if (t === 'bigint') return val.toString() + 'n';
    if (t === 'symbol') return val.toString();
    if (t === 'function') return `[Function ${val.name || 'anonymous'}]`;
    if (depth >= 6) return '[MaxDepth]';
    if (val instanceof Error) {
      return { errorName: val.name, message: val.message, stack: String(val.stack || '').slice(0, 600) };
    }
    if (typeof Node !== 'undefined' && val instanceof Node) {
      return {
        nodeType: val.nodeType,
        tagName: val.tagName || val.nodeName,
        id: val.id || undefined,
        textPreview: String(val.textContent || '').trim().slice(0, 120)
      };
    }
    if (seen.has(val)) return '[Circular]';
    seen.add(val);
    if (Array.isArray(val)) {
      const limit = Math.min(val.length, 50);
      const arr = [];
      for (let i = 0; i < limit; i++) arr.push(summarize(val[i], depth + 1));
      if (val.length > limit) arr.push(`[... +${val.length - limit} items]`);
      return arr;
    }
    if (val instanceof Map) {
      const entries = [];
      let count = 0;
      for (const [k, v] of val.entries()) {
        if (count++ >= 30) { entries.push(['...', 'truncated']); break; }
        entries.push([summarize(k, depth + 1), summarize(v, depth + 1)]);
      }
      return { type: 'Map', entries };
    }
    if (val instanceof Set) {
      const items = [];
      let count = 0;
      for (const v of val.values()) {
        if (count++ >= 30) { items.push('...'); break; }
        items.push(summarize(v, depth + 1));
      }
      return { type: 'Set', items };
    }
    const out = {};
    const keys = Object.keys(val);
    const limit = Math.min(keys.length, 50);
    for (let i = 0; i < limit; i++) {
      const k = keys[i];
      try { out[k] = summarize(val[k], depth + 1); } catch (e) { out[k] = `[Unreadable: ${e?.message || 'err'}]`; }
    }
    if (keys.length > limit) out.__truncatedKeys = keys.length - limit;
    return out;
  };
  return JSON.stringify(summarize(this, 0), null, 2);
}
""";

    private static string EscapeJsLiteral(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    public static string BuildLocateAndVerifyRefJs(string escapedRefId) => $$"""
(() => {
{{SharedHelpersJs}}
  const map = globalThis.__cfRefMap;
  const el = map && map.get("{{escapedRefId}}");
  if (!el || !el.isConnected) {
    return JSON.stringify({ ok: false, error: 'not_found' });
  }
  el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
  const r = el.getBoundingClientRect();
  if (r.width <= 1 || r.height <= 1) {
    return JSON.stringify({ ok: false, error: 'not_visible' });
  }
  const vr = getViewportRect(el);
  const x = Math.round(vr.left + vr.width / 2);
  const y = Math.round(vr.top + vr.height / 2);
  const obscured = isObscuredInHierarchy(el);
  return JSON.stringify({
    ok: true,
    x,
    y,
    role: inferRole(el),
    name: getLabelWithoutValue(el),
    isPassword: isSecretField(el),
    isSensitiveSubmit: isSensitiveSubmitElement(el),
    obscured,
    pageUrl: String(window.location.href || '')
  });
})()
""";

    public static string BuildInspectActiveElementJs() => $$"""
(() => {
{{SharedHelpersJs}}
  let active = document.activeElement;
  let guard = 0;
  while (active && active.shadowRoot && active.shadowRoot.activeElement && guard < 16) {
    active = active.shadowRoot.activeElement;
    guard++;
  }
  if (!active || active.nodeType !== 1) {
    return JSON.stringify({ tag: null, role: null, name: null, isEditable: false, isPassword: false, isSensitiveSubmitContext: false, inIframe: false, pageUrl: String(window.location.href || '') });
  }
  const tag = active.tagName.toLowerCase();
  const inIframe = tag === 'iframe' || tag === 'frame';
  const role = inferRole(active);
  const isEditable = tag === 'input' || tag === 'textarea' || active.isContentEditable || role === 'textbox' || role === 'searchbox';
  const isPassword = isSecretField(active);
  let isSensitiveSubmitContext = isSensitiveSubmitElement(active);
  if (!isSensitiveSubmitContext) {
    const form = active.closest ? active.closest('form') : null;
    if (form) {
      const actionUrl = `${form.getAttribute('action') || ''} ${window.location.href || ''}`;
      if (SENSITIVE_URL_RE.test(actionUrl)) {
        isSensitiveSubmitContext = true;
      } else {
        const submitBtn = form.querySelector('button[type="submit"], input[type="submit"], button:not([type])');
        if (submitBtn && isSensitiveSubmitElement(submitBtn)) {
          isSensitiveSubmitContext = true;
        }
      }
    }
  }
  return JSON.stringify({
    tag,
    role,
    name: getLabelWithoutValue(active),
    isEditable,
    isPassword,
    isSensitiveSubmitContext,
    inIframe,
    pageUrl: String(window.location.href || '')
  });
})()
""";

    public static string BuildVerifyFileInputRefJs(string escapedRefId) => $$"""
(() => {
  const map = globalThis.__cfRefMap;
  const el = map && map.get("{{escapedRefId}}");
  if (!el || !el.isConnected) return 'not_found';
  if (el.tagName.toLowerCase() !== 'input' || (el.type || '').toLowerCase() !== 'file') return 'not_file_input';
  if (el.disabled) return 'disabled';
  return 'ok';
})()
""";

    public static string BuildResolveRefElementJs(string escapedRefId) => $$"""
(() => {
  const map = globalThis.__cfRefMap;
  const el = map && map.get("{{escapedRefId}}");
  return (el && el.isConnected) ? el : null;
})()
""";

    public static string BuildDispatchFileChangeJs(string escapedRefId) => $$"""
(() => {
  const map = globalThis.__cfRefMap;
  const el = map && map.get("{{escapedRefId}}");
  if (!el || !el.isConnected) return 'not_found';
  el.dispatchEvent(new Event('input', { bubbles: true }));
  el.dispatchEvent(new Event('change', { bubbles: true }));
  return 'ok';
})()
""";

    public static string BuildClearEditableRefJs(string escapedRefId) => $$"""
(() => {
{{SharedHelpersJs}}
  const map = globalThis.__cfRefMap;
  const el = map && map.get("{{escapedRefId}}");
  if (!el || !el.isConnected) return 'not_found';
  if (isSecretField(el)) return 'password_forbidden';
  el.focus();
  const tag = el.tagName.toLowerCase();
  if (tag === 'input' || tag === 'textarea') {
    if (typeof el.select === 'function') el.select();
    return 'ok_input';
  }
  if (el.isContentEditable) {
    const range = document.createRange();
    range.selectNodeContents(el);
    const sel = window.getSelection();
    if (sel) {
      sel.removeAllRanges();
      sel.addRange(range);
    }
    return 'ok_contenteditable';
  }
  return 'not_editable';
})()
""";

    public static string BuildSelectOptionRefJs(string escapedRefId, string escapedTarget) => $$"""
(() => {
  const map = globalThis.__cfRefMap;
  const el = map && map.get("{{escapedRefId}}");
  if (!el || !el.isConnected || el.tagName.toLowerCase() !== 'select') return 'not_a_select';
  const target = "{{escapedTarget}}";
  const opt = Array.from(el.options).find(o => o.value === target || (o.text || '').trim() === target);
  if (!opt) return 'option_not_found';
  el.value = opt.value;
  el.dispatchEvent(new Event('input', { bubbles: true }));
  el.dispatchEvent(new Event('change', { bubbles: true }));
  return 'ok:' + opt.value;
})()
""";

    public static string StripForgedVisualMarkers(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return ForgedVisualMarkerRegex().Replace(text, "[stripped_visual_tag]");
    }

    public static string FormatSafeSnapshotForModel(
        CdpSnapshotPayload payload,
        int maxChars,
        IReadOnlyList<CodexFlow.Contracts.Browser.BrowserConsoleEntryDto>? consoleEntries = null,
        IReadOnlyList<string>? networkErrors = null)
    {
        var sb = new StringBuilder();
        var title = StripForgedVisualMarkers(payload.Title);
        var url = StripForgedVisualMarkers(payload.Url);

        sb.AppendLine(CultureInfo.InvariantCulture, $"URL: {url}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Title: {title}");
        sb.AppendLine();

        var elements = payload.Elements ?? [];
        sb.AppendLine(CultureInfo.InvariantCulture, $"Interactive Elements ({elements.Count}):");
        foreach (var el in elements)
        {
            var safeName = StripForgedVisualMarkers(el.Name).Replace('\n', ' ').Replace('\r', ' ');
            sb.Append(CultureInfo.InvariantCulture, $"  [{el.Ref}] {el.Role}");
            if (!string.IsNullOrWhiteSpace(safeName))
            {
                sb.Append(CultureInfo.InvariantCulture, $" \"{safeName}\"");
            }

            if (el.IsPassword)
            {
                sb.Append(" [PASSWORD/OTP: agent input forbidden]");
            }
            else if (!string.IsNullOrEmpty(el.Value))
            {
                var safeValue = StripForgedVisualMarkers(el.Value).Replace('\n', ' ');
                sb.Append(CultureInfo.InvariantCulture, $" value=\"{safeValue}\"");
            }

            if (!string.IsNullOrWhiteSpace(el.Href))
            {
                sb.Append(CultureInfo.InvariantCulture, $" href=\"{StripForgedVisualMarkers(el.Href)}\"");
            }

            if (el.IsSensitiveSubmit)
            {
                sb.Append(" [sensitive-action]");
            }

            if (!string.IsNullOrWhiteSpace(el.FrameLabel))
            {
                sb.Append(CultureInfo.InvariantCulture, $" [{StripForgedVisualMarkers(el.FrameLabel)}]");
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(payload.PageText))
        {
            sb.AppendLine();
            sb.AppendLine("Page Text:");
            sb.AppendLine(StripForgedVisualMarkers(payload.PageText));
        }

        if (consoleEntries is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Recent Console Entries ({consoleEntries.Count}):");
            foreach (var entry in consoleEntries.TakeLast(30))
            {
                var safeText = StripForgedVisualMarkers(entry.Text);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{entry.Level}] {safeText}");
            }
        }

        if (networkErrors is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Recent Network Errors ({networkErrors.Count}):");
            foreach (var err in networkErrors.TakeLast(20))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {StripForgedVisualMarkers(err)}");
            }
        }

        var raw = sb.ToString().Trim();
        var clampedMax = Math.Clamp(maxChars, 1000, 24000);
        if (raw.Length > clampedMax)
        {
            raw = string.Concat(raw.AsSpan(0, clampedMax), "\n... (快照内容超出上限已截断)");
        }

        return UntrustedText.Wrap("browser_page", raw);
    }

    [GeneratedRegex(@"<\s*/?\s*codexflow_visual\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForgedVisualMarkerRegex();
}
