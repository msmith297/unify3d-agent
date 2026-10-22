/**
 * What a pending question's timer does when it fires (store.armQuestionTimeout): the agent's question (ask_user /
 * ask_form) gets NO_ANSWER once its deadline passes, unless the user is typing an answer (the deadline moves back by a
 * full timeout) or pressed "Keep waiting" (no deadline). Pure, so lib.check.mjs covers it.
 */
export type QuestionTimerStep =
  | { kind: "stop" } // answered, replaced or set to wait: this timer ends
  | { kind: "extend"; deadline: number; wait: number } // the user is typing: a new deadline
  | { kind: "wait"; wait: number } // not yet due
  | { kind: "answer" }; // due: the agent goes on without an answer

export function questionTimerStep(
  q: { toolUseId: string; deadline?: number } | null | undefined,
  toolUseId: string,
  draft: string,
  nowMs: number,
  timeoutMs: number,
): QuestionTimerStep {
  if (!q || q.toolUseId !== toolUseId || q.deadline === undefined) return { kind: "stop" };
  if (draft.trim()) return { kind: "extend", deadline: nowMs + timeoutMs, wait: timeoutMs };
  const left = q.deadline - nowMs;
  return left > 0 ? { kind: "wait", wait: left } : { kind: "answer" };
}
