import { useStore } from "../store";
import { IconSearch } from "./SwIcons";
import { Rollout, Toggle } from "./SettingsPanel";

/** Options > Web research: whether the agent may search the web and read pages (Anthropic's web_search / web_fetch). */
export function WebResearchSettings() {
  const on = useStore((s) => s.settings.webResearch !== false);
  const { saveSettings } = useStore.getState();
  return (
    <Rollout title="Web research" icon={<IconSearch size={14} />}>
      <Toggle
        checked={on}
        onChange={(v) => saveSettings({ webResearch: v })}
        label="Allow web research (search and read web pages)"
        note="The agent can look things up, e.g. a project you want a plugin for. Searches run through Anthropic and cost a little extra."
      />
    </Rollout>
  );
}
