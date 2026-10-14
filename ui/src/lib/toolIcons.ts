import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ToolIcon } from "../components/SwIcons";
import { rpc } from "./bridge";
import type { UserToolInfo } from "./types";

/**
 * SolidWorks ribbon buttons need bitmaps, but tool icons are SVG (designed by the agent, or one of the built-in
 * glyphs). The panel renders each icon to a 128 px PNG and hands it to the add-in, which builds the ribbon strips.
 */

/** data: URL for showing an agent-designed SVG icon in an <img> (scripts never run there). */
export function svgDataUrl(svg: string): string {
  const withNs = /xmlns=/.test(svg) ? svg : svg.replace(/^<svg/i, '<svg xmlns="http://www.w3.org/2000/svg"');
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(withNs)}`;
}

function iconSvg(tool: UserToolInfo): string {
  if (tool.iconSvg) return tool.iconSvg;
  return renderToStaticMarkup(createElement(ToolIcon, { name: tool.icon, size: 128 }));
}

async function rasterize(svg: string, size = 128): Promise<string> {
  const img = new Image(size, size);
  img.src = svgDataUrl(svg.replace(/^<svg([^>]*?)\swidth="[^"]*"/i, "<svg$1").replace(/^<svg([^>]*?)\sheight="[^"]*"/i, "<svg$1").replace(/^<svg/i, `<svg width="${size}" height="${size}"`));
  await img.decode();
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  canvas.getContext("2d")!.drawImage(img, 0, 0, size, size);
  return canvas.toDataURL("image/png").split(",")[1];
}

const pending = new Set<string>();

/** Render and send the PNG for every tool whose ribbon icon is missing or out of date. */
export function syncRibbonIcons(tools: UserToolInfo[]) {
  for (const t of tools) {
    if (t.hasPng !== false || pending.has(t.name)) continue;
    pending.add(t.name);
    rasterize(iconSvg(t))
      .then((png) => rpc("userTools.setIcon", { name: t.name, png }))
      .catch((err) => console.warn(`Icon for tool ${t.name} could not be rendered`, err))
      .finally(() => pending.delete(t.name));
  }
}
