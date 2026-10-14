import { memo, useMemo } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import hljs from "highlight.js/lib/core";
import csharp from "highlight.js/lib/languages/csharp";
import json from "highlight.js/lib/languages/json";
import vbnet from "highlight.js/lib/languages/vbnet";
import xml from "highlight.js/lib/languages/xml";
import { rpc } from "../lib/bridge";

hljs.registerLanguage("csharp", csharp);
hljs.registerLanguage("cs", csharp);
hljs.registerLanguage("json", json);
hljs.registerLanguage("vbnet", vbnet);
hljs.registerLanguage("vba", vbnet);
hljs.registerLanguage("xml", xml);

export function highlight(code: string, lang = "csharp"): string {
  try {
    return hljs.getLanguage(lang) ? hljs.highlight(code, { language: lang }).value : escapeHtml(code);
  } catch {
    return escapeHtml(code);
  }
}

function escapeHtml(s: string) {
  return s.replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" })[c]!);
}

export const Code = memo(function Code({ code, lang = "csharp", className = "" }: { code: string; lang?: string; className?: string }) {
  const html = useMemo(() => highlight(code, lang), [code, lang]);
  return (
    <pre className={`selectable sw-input overflow-x-auto !bg-code px-2 py-1.5 font-mono text-[11.5px] leading-[1.5] ${className}`}>
      <code dangerouslySetInnerHTML={{ __html: html }} />
    </pre>
  );
});

export const Markdown = memo(function Markdown({ text, streaming = false }: { text: string; streaming?: boolean }) {
  return (
    <div className={`md ${streaming ? "caret" : ""}`}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: ({ href, children }) => (
            <a
              href={href}
              onClick={(e) => {
                e.preventDefault();
                if (href) rpc("app.openExternal", { url: href });
              }}
            >
              {children}
            </a>
          ),
          code: ({ className, children }) => {
            const lang = /language-(\w+)/.exec(className ?? "")?.[1];
            const text = String(children ?? "");
            if (!lang && !text.includes("\n")) return <code>{children}</code>;
            return <code dangerouslySetInnerHTML={{ __html: highlight(text.replace(/\n$/, ""), lang ?? "csharp") }} />;
          },
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
});
