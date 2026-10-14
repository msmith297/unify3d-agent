import { useEffect, useRef, useState, type ReactNode } from "react";
import { Caret, IconOk } from "./SwIcons";

export interface MenuItem<T extends string> {
  id: T;
  label: string;
  note?: string;
}

/** Toolbar flyout in the style of SolidWorks command drop-downs. Opens upward from the composer. */
export function Dropdown<T extends string>({
  value,
  items,
  onChange,
  title,
  icon,
  align = "left",
  disabled,
}: {
  value: T;
  items: MenuItem<T>[];
  onChange: (id: T) => void;
  title: string;
  icon?: ReactNode;
  align?: "left" | "right";
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useOutside(ref, () => setOpen(false));
  const current = items.find((i) => i.id === value);

  return (
    <div ref={ref} className="relative">
      <button type="button" title={title} disabled={disabled} aria-pressed={open} onClick={() => setOpen((o) => !o)} className="sw-tool text-[11.5px]">
        {icon}
        <span className="max-w-[110px] truncate">{current?.label ?? value}</span>
        <Caret open className="opacity-70" />
      </button>
      {open && (
        <div
          className={`absolute bottom-full z-30 mb-1 min-w-[220px] rounded-[3px] border border-line-strong bg-card py-0.5 shadow-[0_4px_12px_rgba(0,0,0,0.35)] ${align === "right" ? "right-0" : "left-0"}`}
          role="menu"
        >
          <div className="sw-rollout-head mb-0.5 !min-h-[20px] text-[11px]">{title}</div>
          {items.map((item) => (
            <button
              key={item.id}
              role="menuitemradio"
              aria-checked={item.id === value}
              onClick={() => {
                onChange(item.id);
                setOpen(false);
              }}
              className="flex w-full items-start gap-2 px-2 py-1 text-left hover:bg-accent-soft"
            >
              <span className="mt-0.5 flex w-3.5 shrink-0 justify-center">{item.id === value && <IconOk size={11} />}</span>
              <span className="min-w-0">
                <span className="block text-[12px] text-text">{item.label}</span>
                {item.note && <span className="block text-[11px] text-text-3">{item.note}</span>}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** CommandManager-style icon button: flat until hovered, then raised. */
export function IconButton({
  title,
  onClick,
  children,
  active,
  disabled,
  className = "",
}: {
  title: string;
  onClick?: () => void;
  children: ReactNode;
  active?: boolean;
  disabled?: boolean;
  className?: string;
}) {
  return (
    <button type="button" title={title} aria-label={title} aria-pressed={active} disabled={disabled} onClick={onClick} className={`sw-tool ${className}`}>
      {children}
    </button>
  );
}

export function useOutside(ref: React.RefObject<HTMLElement | null>, onOutside: () => void) {
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onOutside();
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [ref, onOutside]);
}
