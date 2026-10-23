import type { ModelCaps } from "../../lib/models";
import type { ContentBlockParam, StoredMessage } from "../../lib/types";

const NO_IMAGE = "[An image was left out: this model can't read images.]";
const NO_PDF = "[A PDF was left out: this model can't read PDF documents.]";

/**
 * The history a model can read: images and PDFs become a short note when the model lacks vision or PDF support
 * (caps). Every model in the registry has both, so this returns the same array (and the same request bytes) for them.
 */
export function readableHistory(messages: StoredMessage[], caps: Pick<ModelCaps, "vision" | "pdf">): StoredMessage[] {
  if (caps.vision && caps.pdf) return messages;
  const swap = (b: ContentBlockParam): ContentBlockParam => {
    if (b.type === "image" && !caps.vision) return { type: "text", text: NO_IMAGE };
    if ((b.type as string) === "document" && !caps.pdf) return { type: "text", text: NO_PDF };
    if (b.type === "tool_result" && Array.isArray(b.content)) return { ...b, content: b.content.map((c) => swap(c as ContentBlockParam)) as typeof b.content };
    return b;
  };
  return messages.map((m) => (typeof m.content === "string" ? m : { ...m, content: m.content.map(swap) }));
}

/** The note for an attachment the model can't read, or null. */
export function unreadableNote(kind: "image" | "pdf", label: string, caps: Pick<ModelCaps, "vision" | "pdf">): string | null {
  if (kind === "image" && !caps.vision) return `${label} can't read images. Pick another model, or the image is left out.`;
  if (kind === "pdf" && !caps.pdf) return `${label} can't read PDFs. Pick another model, or the PDF is left out.`;
  return null;
}
