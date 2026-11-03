# FOX CODEX HUD — Quiet Graphite / corner.45

- Distinguish global quota resets from banked reset-card grants using the API event type. Card grants receive violet feedback and a persistent no-automatic-reset label, never spending guidance or a quota-refill pulse.
- Label combined events explicitly. Unknown types cannot encourage quota depletion. Preserve countdown/calendar semantics, carry type through notifications and recognize type corrections even when dates do not change.
- Prevent unrelated completion notifications from contaminating the currently displayed forecast. Keep normal quota rainbow and all display preferences.

- Couple flowing quota color to the displayed output rate with continuous phase and smooth acceleration/deceleration. Keep all numbers and timeline geometry exact.
- Color the speed headline, lit instrument segments, previous measurement and trend by output rate. Keep the estimate dashed; meter scale does not change the absolute speed color.
- Extend valid reset forecasts across the quota surface, heading, digits, rim and timeline. Preserve the unavailable interval's diagonal pattern and explicit dates. Freeze added motion in static and low-memory modes.

- Add a soft flowing rainbow to fresh, positive weekly quota digits and the percent sign. Keep the value and glyph geometry fixed; cached/unknown balances stay neutral and exhaustion retains its warning color.
- Link valid public reset forecasts to the quota surface with an upward light, stronger spectrum, arrival sweep and a use-priority label. Reuse existing event deduplication. Public completion briefly asks users to verify the account refill; it never fabricates a personal reset. Static preference and memory pressure stop all new motion.

- Add the missing Start menu shortcut for Windows app discovery and reopening. Desktop, Start menu and startup use the same installation; uninstall preserves entries belonging to another copy.

- Replace the capsule progress bar with an open calendar timeline. Actual date/hour ticks and a distinct depletion event mark the interval from now to the official weekly reset. Keep the estimated depletion calendar and full official reset time visible.
- Mark the forecast unavailable interval with diagonal hatching, a dashed axis and an explicit duration legend. Only the hatch drifts slowly; dates, event positions and numbers stay steady. Cached data, static preference and memory protection keep the hatch visible without movement.
- Center all three daily quota columns consistently: each label and the complete number/unit group share the same column center, including negative values and unavailable states. Preserve the panel footprint and existing graphite style.
- Anchor the approximate depletion date to the forecast's calculation timestamp, so held forecasts do not drift later. Qualify cached positions, suppress invalid projections and cap rounding at the official reset.

- Keep a signed daily recommendation: overspending displays a negative percentage with an explicit over-budget label. Existing daily ledgers restore the deficit without reallocating the budget.
- An obsidian inset, warm-white amount, amber rim, moving reflection and perimeter glint highlight over-budget usage. Effects share the existing clock and stop for cached data, static preference or memory pressure. No new bitmap caches, probes or timers.

- Recompose the complete HUD without increasing its footprint: aligned surfaces, stronger text hierarchy, a shared quota/runway row, today’s remaining quota recommendation beside two daily-budget columns and a separate public-radar strip.
- Quieter material and a unified response curve reduce competing highlights; incoming radar signals and urgent quota/route/activity states retain visible feedback. Calendar deadlines and estimate labels remain explicit.

- Show today’s remaining quota recommendation, cycle-average usage and remaining daily budget in one compact surface; compare estimated exhaustion with the personal reset and keep stable updates and honest unavailable states.
- Fade the click-through card on pointer hover and restore the saved opacity on exit; the tray toggle persists the preference.
- Restore the corner card above overlapping windows using the actual desktop Z-order, without stealing focus or disturbing active menus.
- Automatically reduces rendering under Windows memory pressure, recovers visuals after stable headroom, and handles GDI+ allocation failures without a fatal paint dialog.
- Restarts an unexpectedly failed managed HUD with bounded backoff while retaining manual-exit behavior.
- Stable task following and a measured recent-average headline prevent background tasks and tool pauses from repeatedly resetting the display.
- Reset radar leads with a live countdown and retains the API calendar reference. Coarse date forecasts count to the provided window end, clearly marked as an estimate.
- Fix periodic false dips at the trend tip by normalizing partial seconds; real pauses remain visible.
- Three-second live speed window with visibly qualified in-flight estimates and measured completion calibration.
- Recent speed weighted by output and duration; cache hits weighted by input tokens.
- 36-segment gauge, heat spring, comet trail, completion ripple and 60-second output trend.
- Persistent tray redline; 400×738 layout that scales to smaller work areas.
- Existing quota, deadline radar, AI 服务 / 上网节点, response chart and startup paths retained.
- Local Codex conversation integration only; no extra network probes or AI requests.

Unzip the Windows x64 package into a permanent directory and run `CodexTokenHud.exe`.
Python is bundled. Windows 10/11 and .NET Framework 4.x are required.
Use `Install.ps1` in the canonical installation folder to register startup.

Speed includes request waiting time and is derived from usage metadata, not per-token stream timestamps.
See [calculation rules and scope](SPEED.md). Inspired by [zoefix/agent-speed](https://github.com/zoefix/agent-speed), with MIT attribution included.
