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

  const deepElementFromPoint = (x, y) => {
    let el = document.elementFromPoint(x, y);
    let guard = 0;
    while (el && el.shadowRoot && guard < 16) {
      const inner = el.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === el) break;
      el = inner;
      guard++;
    }
    return el;
  };
""";

    public static string BuildExtractInteractiveSnapshotJs() => $$"""
(() => {
{{SharedHelpersJs}}
  const MAX_ELEMENTS = 150;
  const MAX_TEXT_CHARS = 10000;

  const isVisible = (el) => {
    if (!el || el.nodeType !== 1) return false;
    const style = window.getComputedStyle(el);
    if (!style || style.display === 'none' || style.visibility === 'hidden' || parseFloat(style.opacity || '1') === 0) {
      return false;
    }
    const rect = el.getBoundingClientRect();
    return rect.width > 1 && rect.height > 1;
  };

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
  let counter = 0;

  const walk = (root) => {
    if (!root || elements.length >= MAX_ELEMENTS) return;
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
    let node = walker.nextNode();
    while (node && elements.length < MAX_ELEMENTS) {
      if (isInteractive(node) && isVisible(node)) {
        counter += 1;
        const refId = `e${counter}`;
        refMap.set(refId, node);
        const rect = node.getBoundingClientRect();
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
          x: Math.round(rect.left + rect.width / 2),
          y: Math.round(rect.top + rect.height / 2),
          width: Math.round(rect.width),
          height: Math.round(rect.height)
        });
      }
      if (node.shadowRoot) {
        walk(node.shadowRoot);
      }
      node = walker.nextNode();
    }
  };

  walk(document.body || document.documentElement);

  const rawText = (document.body?.innerText || '').replace(/\n{3,}/g, '\n\n').trim();
  const pageText = rawText.length > MAX_TEXT_CHARS
    ? rawText.slice(0, MAX_TEXT_CHARS) + '\n... (页面正文已截断)'
    : rawText;

  return JSON.stringify({
    url: window.location.href,
    title: document.title || '',
    pageText,
    elements
  });
})()
""";

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
  const x = Math.round(r.left + r.width / 2);
  const y = Math.round(r.top + r.height / 2);
  const hit = deepElementFromPoint(x, y);
  const obscured = Boolean(hit && hit !== el && !el.contains(hit) && !hit.contains(el));
  return JSON.stringify({
    ok: true,
    x,
    y,
    role: inferRole(el),
    name: getLabelWithoutValue(el),
    isPassword: isSecretField(el),
    isSensitiveSubmit: isSensitiveSubmitElement(el),
    obscured
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
    return JSON.stringify({ tag: null, role: null, name: null, isEditable: false, isPassword: false, isSensitiveSubmitContext: false, inIframe: false });
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
    inIframe
  });
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
