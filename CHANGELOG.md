# Changelog

## FOX CODEX HUD 1.3.0-beta.8.corner.45

Fix banked reset-card grants incorrectly activating quota-spending guidance.
Gate quota spectrum and refill feedback on explicit global/global_and_banked
semantics. Label card grants in violet with a persistent no-automatic-reset
caption. Preserve API dates/countdowns and distinguish forecasts, completions
and approaching notifications. Unknown types fail closed for quota guidance;
type corrections invalidate prior notice identity, and unrelated notices cannot
recolor the active event. Preserve all saved display preferences.


## FOX CODEX HUD 1.3.0-beta.8.corner.44

Bind quota rainbow velocity to the displayed output rate using continuous,
smoothed phase integration. Color the main speed digits and lit gauge by rate,
independently of the configured meter scale. Extend valid reset spectrum feedback
to the complete quota surface and timeline while retaining diagonal gap hatching,
calendar labels and exact values. Verify acceleration, pauses, static/memory modes,
all native interaction regressions and 8,000 complete-card redraws.


## FOX CODEX HUD 1.3.0-beta.8.corner.43

Add a slow pastel spectrum to live positive weekly quota digits without changing
their value or geometry. Valid reset forecasts strengthen the spectrum and link
radar and quota with an upward light, deduplicated arrival sweep and usage-priority
label. Public completion briefly requests account verification rather than
claiming a refill. Preserve neutral cached states, exhaustion warnings, static
preference, memory safeguards and the existing panel layout.

## FOX CODEX HUD 1.3.0-beta.8.corner.42

Add the missing per-user Start menu shortcut so Windows app search can find
and reopen the HUD. Keep desktop and Start menu entries pinned to the same
installation. Uninstall removes only shortcuts targeting its own executable.


## FOX CODEX HUD 1.3.0-beta.8.corner.41

Replace the capsule rail with an open timeline using actual calendar/clock ticks
and a depletion event marker. Distinguish the forecast unavailable interval with
diagonal hatching, a dashed baseline and an explicit duration legend. Slowly
move only the hatch; preserve its static pattern for cached data, reduced motion
and memory pressure. Center each daily quota label and its complete number/unit
group within three evenly spaced columns. Preserve forecast math and footprint.

## FOX CODEX HUD 1.3.0-beta.8.corner.40

Show only the future interval from now to the official reset. Explicitly label
solid mint coverage and the amber dark shortage segment with its duration;
remove elapsed time and day ticks. Normalize coverage against remaining time
and animate only the shortage, keeping numbers and usable coverage steady.
Cached data, static preference and memory protection suppress motion while
preserving clear text and surface differences. Keep forecast math, stable
depletion dates, the existing graphite style and the 400x738 footprint.

## FOX CODEX HUD 1.3.0-beta.8.corner.39

Integrate a continuous seven-day runway rail with the existing graphite/mint
visual language. Subtle day ticks, current and depletion markers distinguish
elapsed time, projected coverage and the gap until reset; percentage remains
the hero value. Show a qualified depletion date anchored to the held forecast's
calculation timestamp and the official reset calendar. Handle cached, missing,
near-reset and exhausted states without implying false precision. Keep the
400x738 footprint, existing type scale and memory safeguards.

## FOX CODEX HUD 1.3.0-beta.8.corner.38

Replace the unlabeled runway-ratio strip with an explicit usage assessment:
estimated early depletion, sufficient runway until reset, or a qualified waiting
state. The quota progress bar now exclusively represents actual weekly balance.
Move the over-budget glint along the closed bezel instead of a separate horizontal
track. Quota calculations, footprint and motion/resource safeguards are unchanged.

## FOX CODEX HUD 1.3.0-beta.8.corner.37

Preserve negative daily budget balances instead of clamping overspending to zero.
Show an explicit over-budget label and the signed quota amount; restore deficits
from existing local ledgers without changing allocation or double-charging the
next day. Add a compact obsidian warning with warm-white text, amber rim breathing,
a moving reflection and travelling filament. Reuse the existing animation clock;
cached advice, reduced motion and memory protection retain a static warning.

## FOX CODEX HUD 1.3.0-beta.8.corner.36

Replace relative reduction advice with today's remaining recommended quota.
Freeze a local-day allocation from an observed balance and the time until
midnight/reset, then debit actual quota loss. Restore the ledger across restarts,
reuse existing observations on upgrade, and rebase only for a new day, a detected
refill or an official cycle change. Never infer an unobserved midnight balance.
Cached advice expires after three minutes or at midnight/reset, whichever comes
first. The weekly runway calculation and panel footprint remain unchanged.

## FOX CODEX HUD 1.3.0-beta.8.corner.35

Replace the fixed seven-day reference in the quota card with actionable usage
advice. Show the approximate relative reduction needed for cycle usage to fit
the daily budget until reset, in five-percentage-point steps. Use the held,
unrounded forecast ratio so the recommendation shares the stable publication
interval. Sufficient, depleted, observing and cached states remain explicit;
the weekly forecasting method is unchanged.

## FOX CODEX HUD 1.3.0-beta.8.corner.34

Unify the native HUD around a quiet graphite surface and a consistent 20-pixel
gutter without enlarging its 400×738 footprint. Weekly balance and runway now
share the main row, with the seven-day baseline, cycle pace and daily allowance
in aligned columns. Move the public reset radar into its own horizontal strip,
keeping its countdown and complete calendar deadline separate from account reset.
Group both routes on one surface, simplify the response curve, improve caption
contrast and reserve stronger lighting for risk and incoming radar signals.
Retain status explanations, estimate labels, hover fade, click-through, topmost
recovery, reduced motion and memory protection. Quota and speed formulas are unchanged.

## FOX CODEX HUD 1.3.0-beta.8.corner.33

Keep the displayed runway, time to reset and shortfall arithmetically consistent
after rounding. When both rounded times are equal, use a near-reset label instead
of inventing a precise shortfall. The seven-day baseline and cycle pace are
unchanged; the forecast is recalculated immediately when upgrading.

## FOX CODEX HUD 1.3.0-beta.8.corner.32

Use the seven-day quota window as the runway baseline. Show 100% / 7 as a
reference, calculate the cycle's average daily usage from the official remaining
balance and reset time, and show the daily budget needed to last until reset.
The predicted runway follows the cycle pace rather than a short burst; detected
mid-cycle balance increases start a new observation period. Estimates and
timeline share the same held, rounded pace and remain explicitly approximate.

## FOX CODEX HUD 1.3.0-beta.8.corner.31

Combine weekly remaining quota, estimated runway and recent consumption into a
single compact surface. A small time marker and status capsule retain the
projected shortfall without a separate oversized card. The panel is 90 logical
pixels shorter while preserving the reset radar and existing measurements.

## FOX CODEX HUD 1.3.0-beta.8.corner.30

The weekly quota runway now compares the estimated exhaustion time against the
personal weekly reset on a labeled timeline, with the estimated shortfall and
observed consumption rate. Orange highlights projected early exhaustion and red
adds a gentle pulse when six hours or less remain. Cached and insufficient data
are labeled without live alarms; reduced motion and low-memory modes stay usable.
The marker uses unrounded times while visible durations remain coarse and held.

## FOX CODEX HUD 1.3.0-beta.8.corner.29

The weekly quota card estimates how long the remaining balance could last at
the recently observed account quota consumption rate. It uses up to six hours
of actual percentage observations, requires at least one hour and two percentage
points of change, and holds rounded estimates for ten minutes unless the balance
falls substantially. Resets, refills, expired data and insufficient samples do
not reuse an obsolete estimate. A forecast beyond the weekly reset is labeled as
lasting to that reset. No token-to-quota conversion or new network request is used.

## FOX CODEX HUD 1.3.0-beta.8.corner.28

The click-through corner card now fades to 25% opacity when the pointer is over
the card and restores the user's normal opacity on exit. The transition respects
reduced motion, supports multiple monitors and remains available in low-memory
mode. The tray menu can disable the feature; temporary interaction and tray menus
restore normal opacity. Pointer coordinates are checked locally and never logged.

## FOX CODEX HUD 1.3.0-beta.8.corner.27

The corner card now checks the actual overlapping desktop Z-order, rather than
relying only on the topmost style flag. When another regular or topmost window
covers it, the card restores its topmost position without activating or moving.
Checks are rate-limited and wait while the tray menu or an active transient
menu, tooltip, or owned dialog needs to remain above the card.

## FOX CODEX HUD 1.3.0-beta.8.corner.26

The Windows corner card now monitors system-wide commit and physical memory
headroom. During sustained memory pressure it releases cached bitmap effects,
pauses motion, and refreshes less often while retaining current data. Full
visuals resume after 30 seconds of adequate headroom. GDI+ allocation failures
are caught and retried with lighter rendering; a minimal emergency view is used
if the retry fails. The managed watcher restarts a HUD that exits with a nonzero
code, with bounded backoff, while honoring clean manual exits.

## FOX CODEX HUD 1.3.0-beta.8.corner.25

Automatic following now favors the most recently started user task instead of
alternating whenever another task writes a log entry. Internal subagents do not
steal automatic selection; manual binding remains available. The speed instrument
leads with a duration-weighted average of up to ten completed responses, retains
it across tool pauses and idle periods, and labels it as a recent average. Live
activity and estimated history remain in the trend graph, with no estimated tokens
added to confirmed totals. Missing or stale measurements still display as unavailable.

## FOX CODEX HUD 1.3.0-beta.8.corner.24

The reset radar now displays a live countdown prominently, while keeping the
publisher's calendar reference underneath. For date-only forecasts with an API
window, the countdown uses the supplied window end and labels it as an estimate;
the exact reset time remains unknown. A deadline or inferred point counts to its
provided timestamp. Elapsed, missing and stale forecasts do not invent countdowns.
Notification text distinguishes the window end from an actual reset time.

## FOX CODEX HUD 1.3.0-beta.8.corner.23 — 2026-10-26

Fix a false dip at the right edge of the speed trend: partial-second token amounts
are normalized by their observed duration, and an exact second boundary no longer
adds an unobserved zero point. Constant generation therefore stays level while
genuine pauses remain visible. Trend values are explicitly tok/s; confirmed
token counters and the three-second headline speed remain unchanged.

## FOX CODEX HUD 1.3.0-beta.8.corner.22 — 2026-10-26

Live throughput now uses a three-second window over request intervals, including
first-output waiting and excluding explicit tool execution. In-flight estimates
use duration-weighted recent measured responses from the selected conversation
and model, remain visibly qualified, and are replaced on usage completion.
Estimates never inflate confirmed counters. Cache hit rates are input-weighted.
Cold replay preserves model context and excludes copied fork history.

A 36-segment heat instrument, analytic spring, speed-sensitive comet, measured
completion ripples and a 60-second measured/estimated trend lead the 400×688 card.
The tray persists the instrument redline; small work areas scale the card to fit.
Quota, deadline radar, both independent routes, click-through and motion settings
remain available. Source integration is local/read-only, with no added probes or
AI requests. Agent Speed attribution and MIT notice ship with the release.

## FOX CODEX HUD 1.3.0-beta.8.corner.21 — 2026-10-24

The tray now offers full motion, system preference and static display independently
of the Windows client-area animation switch. The default still follows Windows;
an explicit full-motion choice persists across restarts. Runtime diagnostics expose
the selected mode, effective state, system preference and animated frame count.

Moving ambient light, fine fixed grain, reflective rims and inset edges give the
graphite material depth in ordinary operation. Fresh route samples illuminate the
matching tile for 2.4 seconds. Radar scanning remains visible without a forecast;
it does not create a signal or notification. Actual forecasts retain stronger
gold sweeps and confirmed completions retain mint feedback. Static mode preserves
legibility and signal emphasis with no decorative motion. No system setting changes.

## FOX CODEX HUD 1.3.0-beta.8.corner.20 — 2026-10-24

The left route now probes the merged AI服务 group. The right 上网节点 tile
continues probing 苏菲家宽 independently, including when the AI route fails.

Graphite Glass redesign: a 400×536 logical-pixel card with a restrained ambient
material, large quota typography, a dedicated radar surface, paired route cards,
separate throughput/cache metrics and an expanded response chart. Signal-driven
radar sweeps and rings complement the existing 12-second arrival emphasis and
deduplicated system notifications. Numeric readings stay exact. Reduced motion,
click-through, tray controls, deadlines and source-freshness checks are retained.
The static material is cached; animation performs no I/O or additional probes.

## FOX CODEX HUD 1.3.0-beta.8.corner.18 — 2026-10-22

Every valid reset forecast now keeps a warm highlighted radar tile with a gentle
breathing outline; confirmed public completions use mint. New or changed signals
briefly emphasize the full card border and show a twelve-second arrival caption.
Near forecasts strengthen to orange. Reduced-motion mode keeps static emphasis.
Notifications now recognize deadline/type/window-end changes under the same event
ID while suppressing unchanged repeats. Animation adds no network requests or
custom sound playback.

## FOX CODEX HUD 1.3.0-beta.8.corner.17 — 2026-10-22

Public deadline predictions now show the API's upper-bound date directly as
"最晚 MM/dd HH:mm 前", with an estimate label and remaining time. Inferred point
estimates retain their supplied date. Passed predictions keep their reference
date with an expired label until the website updates; confirmed completion
records still take priority. The compact card fits calendar labels without
changing its size, and notifications distinguish deadlines from reset times.

## FOX CODEX HUD 1.3.0-beta.8.corner.16 — 2026-10-22

The public reset radar now follows the Windows system HTTP proxy for HTTPS
requests, with verified TLS through CONNECT. Explicit HTTPS_PROXY and NO_PROXY
settings remain authoritative. Unrelated applications' proxy environment
variables no longer suppress Windows proxy discovery. Polling intervals,
rate-limit handling and source freshness checks are retained.

## FOX CODEX HUD 1.3.0-beta.8.corner.15 — 2026-10-21

The compact activity card now plots the same latest 20 completed responses used by its
mean, with a mean guide, peak label, duration colors, 900 ms sample transitions and
new-sample endpoint ripples. Active illumination follows the real curve; prolonged
activity waits strengthen the card contour. Session changes reset animation, missing
history stays empty, and reduced-motion preferences are respected. No extra network
requests or AI calls. The 368 × 374 layout and activity timer are retained.

## FOX CODEX HUD 1.3.0-beta.8.corner.14 — 2026-10-21

Active route probes now run every 10 seconds, retaining the five-second probe timeout
and non-overlapping rounds. The UI interval label follows the collector snapshot.

## FOX CODEX HUD 1.3.0-beta.8.corner.13 — 2026-10-20

The reset radar now retains recent public completion records for 10 hours when no
new schedule exists. A mint "已重置" status shows how long ago the publisher announced
or recorded completion. Banked credits have a separate label; stale sources and
other-plan/five-hour-only events cannot claim a fresh Pro weekly reset.
The general network tile is now labeled "上网节点", while its configured proxy target remains unchanged.

## FOX CODEX HUD 1.3.0-beta.8.corner.12 — 2026-10-20

Windows corner edition with soft vector halos, severity-dependent pulses, timeout ripple contours,
smooth latency colors, per-route failure/recovery notices and a moving activity light.
Animations run independently of telemetry at a maximum of 25 FPS (10 FPS in remote sessions),
pause for tray menus, and respect Windows animation preferences. No animation library or extra probe traffic.
New installations default to the compact, translucent, click-through corner card.

Includes weekly quota, public reset radar, two local Clash/Mihomo route probes, the latest
20 timed model-response average, long-session recognition and the GDI+ tiny-arc fix.
Preserves the upstream MIT license and existing local data paths for upgrades.

## 1.3.0-beta.8 — 2026-10-13

Windows data is shared through `%USERPROFILE%\.codex-token-hud`, avoiding MSIX AppData redirection between Codex and Task Scheduler. Existing choices migrate without stale telemetry; legacy watcher actions migrate as well. Windows watcher startup is owned by Task Scheduler, so installers and terminals can return normally. Source-process integration checks wait for readiness on cold macOS runtimes.

Replaced direct logon HUD startup with application following. A small user-session watcher waits for Codex Desktop or a native Codex backend used by CLI/IDE clients; the HUD and collector start only after a client appears. After the final client closes, the watcher asks only its own HUD to exit. Manually opened windows are preserved, and manually exiting suppresses relaunch until the current clients have closed. Windows migrates this copy's legacy task and uses explicit latest-activity binding outside its foreground Desktop window; manual pinning remains authoritative.

Windows, macOS and Linux provide the same follow option. User LaunchAgents/XDG autostart or a headless systemd user service keep only the watcher available after login. WSL/SSH follow clients on the log host, with a dependency-free local dashboard on headless hosts. Exact custom executable paths and a generic `--run CLIENT [ARGS...]` launcher cover renamed or additional compatible clients and environments without an autostart manager. Process arguments, credentials and message bodies are not read by detection. Offline logs remain manually viewable.

## 1.3.0-beta.5 — 2026-10-10

Added a native Qt desktop HUD for macOS and Linux, with ready-to-run Intel/Apple Silicon and Linux x64/ARM64 build jobs. The strip stays above normal windows where supported, moves/resizes, saves its layout, restores after repeat launch, and offers tray/menu actions with a safe fallback when no tray exists. macOS panels remain visible when inactive and join Spaces with the full-screen auxiliary flag. XWayland is preferred when available; native Wayland retains system drag/resize, with compositor-specific stacking/placement limits.

Choose and pin exact local conversation IDs, or explicitly follow the latest recorded activity. This mode does not infer the foreground conversation. Browser range snapshots, return-to-latest and pin/unpin changes synchronize with the strip. Force refresh rescans and rereads logs with visible acknowledgement. Per-chat weekly allowance remains unavailable. Browser labels now reflect the attached native platform and follow mode; the original Windows desktop-binding behavior is retained.

Added isolated selection/freshness/window/IPC regressions and target-native bundled-launch checks using synthetic telemetry. Optional Qt dependencies are kept separate from the dependency-free browser/terminal tools; desktop packages retain replaceable shared libraries and third-party notices.

## 1.3.0-beta.4 — 2026-10-10

Reworked the local statistics dashboard around conversation/range selection and prominent weighted results. Return-to-latest, Windows pin and unpin actions now have filled, bordered buttons visible without hovering, with 44px targets and explicit focus/busy/disabled states. Windows controls report automatic/current/other-conversation binding and update from live metadata. Added keyboard range tabs, inline validation that retains inputs, and invalidation of a selected message when its query is edited. The layout adapts to narrow screens and the mini-window; calculation details are collapsible.

Fixed the redundant **显示状态栏** command resetting a strip that was already visible: both menus disable the command while displayed, and repeat-launch/tray show requests preserve the current conversation. Hidden/minimized window recovery remains available. Added three own-form regression cases. Shared token/time/cache formulas are unchanged.

## 1.3.0-beta.3 — 2026-10-10

Fixed the Windows process remaining alive while its strip was hidden or covered. Display recovery checks native visibility and topmost state, resets an inconsistent native z-order band, and restores minimized launches without taking focus. The launcher and newly created shortcuts request normal display. Starting another copy asks the existing instance to show; the tray now offers **显示状态栏**, and double-click shows and refreshes. Startup keeps a waiting strip accessible until Codex is foreground. Diagnostics include the HUD handle, actual visibility/topmost/minimized state, DWM cloaking and display errors. Added focused native-state regressions; shared Python statistics are unchanged.

## 1.3.0-beta.2 — 2026-10-10

Fixed the Windows strip disappearing after unpinning while automatic window discovery is pending. The strip remains visible with an explicit waiting state, clears the former conversation's measurements and keeps its position. The ID-pinning entry is now always available in both the strip and tray menus. The HUD no longer assigns a foreign Codex window as its native owner, preventing that window's destruction from destroying the HUD. Obsolete inspection results are discarded after mode changes.

Added 12 targeted own-form binding/lifecycle checks, including the discovered window remaining in the background after unpinning. Waiting-strip movement does not overwrite the separately saved automatic layout. Shared Python statistics are unchanged.

## 1.3.0-beta.1 — 2026-10-10

Added a local browser panel for selecting a time interval or the first/last user messages. Confirmation calculates output-token/time-weighted speed and input-token-weighted cache fraction over complete history, including the last selected message's responses. Missing telemetry is excluded separately with coverage counts. Confirmed results are fixed snapshots, synchronized to the matching Windows HUD; users can return to the latest response.

Added standard-library-only browser and terminal entry points for Python 3.10+ hosts, including macOS/Linux/Windows, offline JSONL, archived logs, CLI/IDE local sessions, WSL and SSH forwarding. The browser mini-window is movable and resizable by the OS. File discovery works without a compatible database and combines resumed segments by conversation ID. Native Windows builds are now AnyCPU, with x64/ARM64/x86 private-Python packaging options; untested host architectures remain marked as such.

Fixed manual refresh to rescan active rollouts, even when the database's saved path has not changed, and publish an acknowledgement. The HUD shows pending/success/unavailable/no-new-count feedback and reconnects an unresponsive collector. Added targeted range, refresh and loopback-access checks plus macOS/Linux CI configuration. No per-conversation weekly quota estimate was introduced.

Added exact-ID pinning from the dashboard to keep the Windows HUD visible for Desktop/CLI/IDE chats when automatic title discovery is unavailable. Automatic and pinned modes keep separate saved layouts. Fixed same-size log rewrites so changed telemetry is reread rather than retaining an earlier sample.

## 1.2.0-beta.1 — 2026-10-06

First public distribution candidate. Windows x64 portable ZIP includes a pinned, checksum-verified private Python runtime. Added MIT license, English and Chinese documentation, privacy guidance, automated Windows test/build and draft prerelease workflows, and an extracted-package smoke test with synthetic telemetry.

Moved writable HUD state to the current user's LocalAppData directory, preserving a legacy layout on first launch. Added `CODEX_HOME` and settings overrides, relative runtime discovery, database version/schema discovery, and explicit unavailable/incompatible database states. No individual-chat weekly quota estimate is supplied.

## 1.1.0 — 2026-10-06

Fixed resumed chats continuing to read their previous rollout file, and mismatched canonical/legacy cumulative counters suppressing new statistics. Added source replacement/truncation handling, bounded recent history, 100 ms tail polling, sample age, stale/new-turn display guards, manual refresh, and collector heartbeat recovery.

## 1.0.0 — 2026-10-02

Initial local Windows HUD: current model, response-average output token speed, input cache fraction, draggable/resizable native strip, saved layout, tray menu, and optional logon startup.
