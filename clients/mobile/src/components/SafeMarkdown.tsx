import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

const ALLOWED_URL_PROTOCOLS = new Set(['https:', 'http:', 'mailto:']);

export function sanitizeMarkdownUrl(rawUrl: string): string {
  const trimmed = (rawUrl ?? '').trim();
  if (!trimmed) {
    return '';
  }
  if (trimmed.startsWith('#') || (trimmed.startsWith('/') && !trimmed.startsWith('//'))) {
    return trimmed;
  }
  try {
    const parsed = new URL(trimmed);
    if (ALLOWED_URL_PROTOCOLS.has(parsed.protocol.toLowerCase())) {
      return parsed.toString();
    }
    return '';
  } catch {
    return '';
  }
}

export function SafeMarkdown({ content }: { readonly content: string }) {
  return (
    <div className="prose prose-invert prose-sm max-w-none break-words [&_pre]:overflow-x-auto [&_pre]:rounded-lg [&_pre]:bg-slate-900/90 [&_pre]:p-3 [&_code]:text-sky-300">
      <Markdown
        remarkPlugins={[remarkGfm]}
        urlTransform={(uri) => sanitizeMarkdownUrl(uri)}
        components={{
          a: ({ href, children }) => {
            const safeHref = href ? sanitizeMarkdownUrl(href) : '';
            if (!safeHref) {
              return <span className="text-slate-400 underline">{children}</span>;
            }
            return (
              <a
                href={safeHref}
                target="_blank"
                rel="noopener noreferrer"
                className="text-sky-400 underline underline-offset-2"
              >
                {children}
              </a>
            );
          },
          img: ({ alt }) => (
            <span className="inline-block rounded border border-slate-700 bg-slate-900 px-2 py-1 text-xs text-slate-400">
              [图片已拦截展示: {alt || 'image'}]
            </span>
          ),
        }}
      >
        {content}
      </Markdown>
    </div>
  );
}
