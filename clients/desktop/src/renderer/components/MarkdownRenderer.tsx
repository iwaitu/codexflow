import React, { useEffect, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { Check, Copy } from 'lucide-react';
import { codeToHtml } from 'shiki';
import { useI18n } from '../lib/i18n';
import { usePreferencesStore } from '../stores/usePreferencesStore';

interface CodeBlockProps {
  language: string;
  code: string;
}

const CodeBlock: React.FC<CodeBlockProps> = ({ language, code }) => {
  const { t } = useI18n();
  const theme = usePreferencesStore((s) => s.theme);
  const [highlightedHtml, setHighlightedHtml] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    let active = true;
    const lang = language || 'text';
    void codeToHtml(code, {
      lang,
      theme: theme === 'light' ? 'github-light' : 'github-dark',
    })
      .then((html) => {
        if (active) setHighlightedHtml(html);
      })
      .catch(() => {
        if (active) setHighlightedHtml(null);
      });
    return () => {
      active = false;
    };
  }, [code, language, theme]);

  const handleCopy = () => {
    void navigator.clipboard?.writeText(code).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  };

  return (
    <div className="my-2 overflow-hidden rounded-lg border border-slate-800 bg-slate-950">
      <div className="flex items-center justify-between border-b border-slate-800 bg-slate-900/70 px-3 py-1 text-xs text-slate-400">
        <span className="font-mono">{language || 'text'}</span>
        <button
          type="button"
          onClick={handleCopy}
          className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-slate-300 hover:bg-slate-800"
        >
          {copied ? <Check className="h-3.5 w-3.5 text-emerald-400" /> : <Copy className="h-3.5 w-3.5" />}
          <span>{copied ? t('chat.copied') : t('chat.copy')}</span>
        </button>
      </div>
      {highlightedHtml ? (
        <div
          className="overflow-x-auto text-xs [&_pre]:m-0 [&_pre]:bg-transparent [&_pre]:p-3"
          dangerouslySetInnerHTML={{ __html: highlightedHtml }}
        />
      ) : (
        <pre className="overflow-x-auto p-3 text-xs text-slate-200">
          <code>{code}</code>
        </pre>
      )}
    </div>
  );
};

interface MarkdownRendererProps {
  content: string;
  allowImages?: boolean;
}

export const MarkdownRenderer: React.FC<MarkdownRendererProps> = ({
  content,
  allowImages = true,
}) => {
  const { t } = useI18n();
  return (
    <div className="markdown-body text-sm leading-relaxed text-slate-100">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          img({ src, alt }) {
            if (!allowImages) {
              return (
                <span className="italic text-slate-400">
                  {t('chat.remoteImageDisabled', { label: alt || src || 'image' })}
                </span>
              );
            }
            return <img src={src} alt={alt ?? ''} className="max-w-full rounded" />;
          },
          code({ className, children }) {
            const match = /language-(\w+)/.exec(className || '');
            const codeText = String(children).replace(/\n$/, '');
            if (match || codeText.includes('\n')) {
              return <CodeBlock language={match?.[1] ?? 'text'} code={codeText} />;
            }
            return <code className={className}>{children}</code>;
          },
          a({ href, children }) {
            return (
              <a
                href={href}
                onClick={(e) => {
                  e.preventDefault();
                  if (href) void window.codexflow?.openExternal(href);
                }}
                className="text-indigo-400 underline hover:text-indigo-300"
              >
                {children}
              </a>
            );
          },
          table({ children }) {
            return (
              <div className="my-3 overflow-x-auto rounded-lg border border-slate-700/80 bg-slate-950/60">
                <table className="min-w-full border-collapse text-left text-xs">{children}</table>
              </div>
            );
          },
          thead({ children }) {
            return <thead className="bg-slate-800/80 text-slate-200">{children}</thead>;
          },
          tbody({ children }) {
            return <tbody className="divide-y divide-slate-800/80">{children}</tbody>;
          },
          tr({ children }) {
            return <tr className="transition-colors even:bg-slate-900/35 hover:bg-slate-800/40">{children}</tr>;
          },
          th({ style, children }) {
            return (
              <th
                style={style}
                className="whitespace-nowrap border-b border-r border-slate-700/80 px-3 py-2 font-semibold text-slate-100 last:border-r-0"
              >
                {children}
              </th>
            );
          },
          td({ style, children }) {
            return (
              <td style={style} className="border-r border-slate-800/80 px-3 py-2 align-top text-slate-200 last:border-r-0">
                {children}
              </td>
            );
          },
        }}
      >
        {content}
      </ReactMarkdown>
    </div>
  );
};
