import { IconMic, Spinner } from "./SwIcons";
import { talkKeyName, useStore } from "../store";

/**
 * Asked once, after the first-run tour: talk to the agent? Voice needs a speech model on this PC, so saying yes
 * downloads one (the recommended size) and turns voice on when it arrives.
 */
export function VoicePrompt() {
  const settings = useStore((s) => s.settings);
  const status = useStore((s) => s.voiceStatus);
  const want = useStore((s) => s.voiceWant);
  const { enableVoice, disableVoice, saveSettings } = useStore.getState();
  if (!settings.tourDone || !status?.available) return null;
  const installed = status.models.some((m) => m.installed);
  const downloading = !!want && status.download?.id === want;
  if (!downloading && (settings.voiceAsked || (settings.voiceEnabled && installed))) return null;

  const model = status.models.find((m) => m.id === "base.en") ?? status.models[0];
  const key = talkKeyName(settings.voiceKey);
  const progress = Math.round((status.download?.progress ?? 0) * 100);
  return (
    <div className="flex shrink-0 items-start gap-2 border-b border-line bg-accent-soft px-2 py-1.5 text-[11.5px] text-text shadow-[inset_0_1px_0_var(--bevel-hi)]">
      <span className="mt-px shrink-0">
        <IconMic size={14} />
      </span>
      {downloading ? (
        <span className="flex-1">
          <span className="flex items-center gap-1.5 font-semibold">
            <Spinner size={11} /> Setting up voice - downloading the speech model ({progress}%)
          </span>
          <span className="mt-1 block h-[4px] w-full overflow-hidden rounded-full bg-card-2">
            <span className="block h-full bg-accent transition-[width]" style={{ width: `${progress}%` }} />
          </span>
          <span className="mt-1 flex items-center gap-2 text-text-2">
            Keep working - voice turns on when it's done.
            <button className="text-text-3 hover:text-text" onClick={disableVoice}>
              Cancel
            </button>
          </span>
        </span>
      ) : (
        <span className="flex-1">
          <span className="font-semibold">Talk to the agent instead of typing?</span>{" "}
          <span className="text-text-2">
            Hold {key} in SolidWorks and speak while you select, sketch and turn the model. Speech is recognised on this PC, so it needs a one-time {model?.mb ?? 141} MB download.
          </span>
          <span className="mt-1 flex gap-1.5">
            <button className="sw-btn sw-btn-primary !h-[22px]" onClick={() => enableVoice(model?.id ?? "base.en")}>
              Set up voice
            </button>
            <button className="sw-btn !h-[22px]" onClick={() => saveSettings({ voiceAsked: true })}>
              Not now
            </button>
          </span>
        </span>
      )}
    </div>
  );
}
