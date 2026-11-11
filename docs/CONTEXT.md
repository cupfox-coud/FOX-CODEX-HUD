# Context reference and handoff reminders

The native HUD reads `event_msg/token_count.info.last_token_usage.input_tokens`
and `model_context_window` from the same local JSONL event. Cached input is
already included. Cumulative consumption and output tokens are not added.
The sample retains its timestamp and model. An unavailable window or a model
change is shown as unknown, never as zero; a zero explicitly reported by the
source remains valid. Repeated cumulative counters still update context.

This is last-request reference occupancy, not live server memory, a prediction
of the next request, or a measurement of model quality. The bar clamps its fill
at 100%, while the number can report values above 100%.

70% suggests preparing a handoff; 85% suggests saving that handoff before a new
conversation. These are adjustable-in-code product defaults, not universal
quality boundaries. A three-percentage-point hysteresis avoids flicker. Each
level's entry sweep runs once per session/model during the HUD process lifetime;
the active state retains its quiet breathing highlight. State is capped at 64
session/model keys. Reduced motion and low-memory mode retain static cues.

Only explicit `compacted` or `event_msg/context_compacted` records establish a
compaction marker. A decrease alone does not. The marker survives a later low
occupancy sample; pending compaction waits for a new sample before advising on
capacity. The bounded log tail cannot claim a lifetime compaction count.

The corner card follows the last viewed Windows Codex chat, including switches
to older conversations. Moving to another app or minimizing Codex preserves
that chat; background output cannot steal the selection. The native header is
checked about every 600 ms (2 seconds in resource-saving mode).

A unique title identifies the chat directly. For duplicate titles, up to 48
SHA-256 fingerprints of 32-character normalized visible text fragments are
matched against recent local user/assistant records. Only an exclusive match
identifies a chat. Shared history, unreadable text and conflicting evidence
remain ambiguous; archive status and recency are never identity proof. Text
is read transiently, not saved to runtime snapshots. The bounded resolver reads
at most the latest 2 MiB from each of four candidates and caches hash/ID results
for five seconds. No screenshots, OCR service or new web service are added.

Explicit tray pinning still overrides automatic following and is labeled
separately. Missing identities and stale collector data are qualified;
background activity contributes to global speed but never impersonates the
viewed conversation's context.
When identity is ambiguous, a separately labeled recent-task response can remain
visible. It never supplies the current-context percentage or triggers its alerts.

Algorithm reference: aris477/codex-context-monitor, revision
`9bc171799a45ad526ce172ddd33aa170643b59c7`, `src/usage-core.js`,
`public/context-overview.js` and `使用说明.md` (MIT). Our implementation is
independently written for the existing Python collector and native renderer.

References:
- https://github.com/aris477/codex-context-monitor
- https://www.anthropic.com/research/effective-context-engineering-for-ai-agents
- https://www.trychroma.com/research/context-rot
