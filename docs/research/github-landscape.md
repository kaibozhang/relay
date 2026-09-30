# GitHub landscape — preliminary evidence, not completed GitHub research

**Status: paused at user request. Research only; no application changes.** The user chose to repair GitHub/search access before proceeding. This document preserves already-retrieved first-party evidence; it does **not** satisfy the requested repository-level comparison or authorize implementation.

## Retrieval scope and limitations

- The official AutoGLM search script was invoked with bundled Python and query `site:github.com AppFlowy SiYuan Joplin super-productivity memos ActivityWatch Flow Launcher local first license`. It failed before search: local token service connection refused (`WinError 10061`). No search results were received.
- A direct fetch of `https://github.com/AppFlowy-IO/AppFlowy` was refused because `github.com` resolved to a non-public IP. The reported earlier `api.github.com` refusal was not retried or bypassed. No GitHub README, source, issues, release history, or LICENSE file was retrieved in this research.
- Independent official product/documentation domains below returned usable content. They are the actual evidence, not proxies used to evade GitHub restrictions. Statements are vendor-documented capabilities, not independently tested behavior.
- AppFlowy main site redirected to a Cloudflare 403; its separately published documentation was accessible. Some guessed Joplin license/API URLs returned 404; they are not evidence. Flow documentation returned an empty JavaScript shell. Microsoft Workspaces documentation was also refused as non-public-IP; no conclusions about it are verified here.
- No stars, latest-version comparisons, maintainer-activity ratings, or novelty claims are made. Current component licenses must be checked at the exact intended commit before reuse.

## Intermediate findings

1. **Offline/local ownership + notes/tasks + mobile is already crowded.** Joplin explicitly documents Windows/Android/iOS, offline-first data, Markdown notes, to-dos, E2EE sync, plugins and a web clipper.[1] Its architecture explicitly uses local SQLite on all applications.[2]
2. **Tasks + focus + capture + cross-device continuation is not a new bundle.** Super Productivity documents subtasks/notes/projects, time tracking, Pomodoro, keyboard capture, plugins, and optional sync.[3]
3. **Windows global invocation and file search are established capabilities.** Flow Launcher documents configurable global activation (default Alt+Space), file/content search via Everything or Windows Search, and a plugin ecosystem.[4]
4. **Privacy and disk storage alone do not differentiate.** SiYuan says data stays entirely on-device and is usable without network; ActivityWatch records locally; Memos documents a persistent server folder.[5][6][7]
5. **No evidence establishes that the proposed recovery workflow is absent from these projects or their plugins.** This is a hypothesis requiring repository and hands-on comparison.

## Seven-project comparison

GitHub links identify comparison targets only; **none were retrieved here**. “Not established” means missing evidence, not absence of a feature.

| Project / repository target | Retrieved strengths and substantial overlap | Windows, mobile and storage evidence | License evidence and remaining gap |
| --- | --- | --- | --- |
| [AppFlowy](https://github.com/AppFlowy-IO/AppFlowy) | Docs describe notes/wikis, task/project databases, Kanban and calendar. Highly overlapping general workspace; docs describe extensibility and data control.[8][9] | Official docs state Flutter + Rust, local installation, offline mode and a native/multiple-platform approach. Specific Windows/mobile behavior and persistence format not verified in retrieved pages. | Exact license **not verified**. Docs say open source/open core; these are not substitutes for LICENSE review. |
| [SiYuan](https://github.com/siyuan-note/siyuan) | Block editing, block references/backlinks, graph, database relations/rollups, flashcards. A much stronger knowledge-management baseline than generic linked notes.[5] | Official page offers Windows downloads and Android/iOS links; says data entirely on-device, offline use, E2EE sync and mobile-server mode. File format not verified. | Official user agreement explicitly says **AGPLv3**, and reproduces “version 3 or later” language.[10] Component/package scope still needs repository review. |
| [Joplin](https://github.com/laurent22/joplin) | Markdown notes + to-dos, attachments/import, search, tags, plugins, browser clipping, E2EE sync. Strong extend-first candidate.[1] | Windows/Linux/macOS/Android/iOS; local SQLite in all apps. Desktop Electron/React; mobile React Native. Disk-backed does not necessarily mean native Windows UI or a user-editable Markdown folder.[1][2] | Exact current app/component license **not verified** in retrieved pages. Do not inherit or distribute code until checked. |
| [Super Productivity](https://github.com/johannesjo/super-productivity) | Tasks, subtasks, notes, projects, boards, focus/Pomodoro, time tracking, keyboard capture, plugins, GitHub/GitLab/Jira integration. Closest baseline for existing tasks+focus product.[3] | Site lists desktop, web, Android/iOS; optional Super Sync/WebDAV/Dropbox and local-by-default/offline use. Specific per-platform storage implementation not inspected. | Official project guide explicitly states **MIT**.[11] Exact repository/dependency licenses remain unchecked. |
| [Memos](https://github.com/usememos/memos) | Short titleless capture, Markdown, tags/search/date navigation, photos/files, related memos, checklists, web clipper, Telegram capture and APIs.[7] | Self-hosted server; official Docker example persists a host folder. This is not evidence of a Windows-native client or offline-first phone application. A plausible inbox/integration component, not proven full recovery solution. | Official homepage explicitly states **MIT**.[7] Repository/component audit outstanding. |
| [ActivityWatch](https://github.com/ActivityWatch/activitywatch) | Automatically records active app/window title, optional browser/editor watchers, categories and time analysis. Strong activity-history overlap; different privacy posture from intentional capture.[6] | Windows/macOS/Linux/Android listed; local data, tray process and localhost UI documented. Page describes synchronization as in-progress, so do not assume complete cross-device sync. | Official FAQ explicitly states **MPL-2.0**.[6] Individual watcher/component licenses must be checked. |
| [Flow Launcher](https://github.com/Flow-Launcher/Flow.Launcher) | Global hotkey, app/file/content/bookmark search, shell/system commands and plugins. Strong reusable Windows entry point; launching is not identical to remembering next action.[4] | Windows 10+; installer/portable distribution documented. No retrieved evidence of mobile inbox, recovery cards, task persistence or clipboard-history functionality. | Exact license **not verified**. Free/open source statement alone is insufficient for copying code. |

## Proposed distinction — a testable hypothesis, not novelty

**An interruption-recovery card, rather than another notes/tasks dashboard.** The user intentionally captures: selected file/folder references, an explicitly chosen clipboard excerpt, the task, and one sentence stating the next concrete action. A global hotkey, tray action or Explorer context-menu action is the proposed entry point; a phone inbox contributes thoughts to review when back at the PC. Resume should show the next action and let the user reopen selected resources.

This combines existing ideas. “Global hotkey”, “offline-first”, “mobile capture”, “local files”, “clipboard” and “resume work” are not credible novelty claims. The possible value is fewer steps and less reconstruction after interruption, validated against existing tools. No inspected source proves the complete combination is missing elsewhere.

Privacy boundaries: no background clipboard history, window-title logging, screenshots, keystroke collection, or file crawling. Preview selected data before capture. File paths can reveal confidential information. A local path is a reference, not a phone-readable attachment; do not silently copy or sync the file. “Resume” must not promise restoration of unsaved application state. Missing/moved files should be explicit, and opening executable/command-like content must never be automatic. These are proposed requirements, not implemented features.

Windows-native should mean real OS integration and durable disk persistence, not merely an Electron window or responsive browser page. A phone inbox requires a defined transport, offline queue, conflict policy and threat model; mobile-sized CSS does not establish mobile functionality.

## Build versus extend — provisional recommendation

**Decision now: pause; do not build or fork until GitHub research is complete and the user approves the direction.** The existing [project README](../../README.md#L55-L75) explicitly says data lives in browser localStorage, with no sync or attachments. More polish on that generic browser product does not address the requested change.

When access is repaired, evaluate these options in order:

1. **Extend/integrate first (recommended evaluation path).** Test Joplin or Super Productivity as the existing note/task/mobile store, and Flow Launcher as an optional Windows entry point. This avoids rebuilding mature editors, search and sync. Current evidence establishes extensibility in broad terms, not that APIs can support the complete workflow. Verify those limits before selecting a base.
2. **Build a small standalone recovery companion only if the extension path fails the workflow test.** Own the intentional capture card and Windows integrations, not a replacement notes ecosystem. Durable storage/export and phone transport still create substantial reliability/security work. Reuse via APIs is different from copying upstream code; review both licensing and API constraints.
3. **Fork a full workspace only with a demonstrated requirement.** AppFlowy/SiYuan/Joplin-sized codebases bring maintenance and license obligations; changing branding and adding another dashboard has weak justification. ActivityWatch is a poor default base if the core principle is no passive monitoring.

Proposed evaluation, not a claimed result: interrupt a real task, save a selected document/folder plus next action, reboot, capture a phone thought offline, and resume without searching through unrelated notes. Compare capture effort, time to first productive action, data survival, moved-file handling and phone delivery/conflicts against configured existing tools. Agree success thresholds with the user before testing.

## Research still required before coding

- Retrieve the seven GitHub READMEs, exact LICENSE files and recent relevant issues/releases. Follow up on license scope, Windows integration, phone capture, export and persistence.
- Inspect Flow clipboard/capture plugins and existing work-context-resume tools; neither category has been adequately covered. Microsoft PowerToys Workspaces is a candidate to inspect, not a verified comparison in this report.
- Search specifically for manual recovery cards, working-set/task bundles, file references, next-action prompts, global capture and mobile inbox integrations. Failure to find a feature in a homepage is not negative evidence.
- Evaluate current APIs/plugins and inspect storage rather than relying on “local-first” marketing. Confirm Android versus iOS scope with the user.
- Verify licensing for any chosen upstream commit and dependencies. MIT, MPL and AGPL impose different obligations; this is not legal clearance.
- Return to the user with an evidence-backed build/extend decision. No implementation work is authorized by this preliminary report.

## Retrieved sources

1. [Joplin — What is Joplin?](https://joplinapp.org/help/) — offline-first, platforms, to-dos, plugins, E2EE and clipper.
2. [Joplin architecture](https://joplinapp.org/help/dev/spec/architecture/) — local SQLite, Electron desktop, React Native mobile, shared backend.
3. [Super Productivity official overview](https://super-productivity.com/) — tasks, focus, notes, plugins, local/offline and optional sync.
4. [Flow Launcher official overview](https://www.flowlauncher.com/) — global hotkey, Windows file search, plugins and Windows distribution.
5. [SiYuan official overview](https://b3log.org/siyuan/en/) — block references, graph/databases, local data, offline and E2EE/mobile-server functions.
6. [ActivityWatch official overview and FAQ](https://activitywatch.net/) — automatic tracking, local data, supported platforms and MPL-2.0 statement.
7. [Memos official overview](https://usememos.com/) — short capture, attachments, APIs, persistence folder and MIT statement. Returned HTML was partially clipped at the final FAQ, but the cited claims appeared in retrieved content.
8. [AppFlowy — Start here, official Markdown documentation](https://docs.appflowy.io/docs/appflowy/readme.md) — local data control, Flutter/Rust, offline/native and customization claims.
9. [AppFlowy — Welcome, official Markdown documentation](https://docs.appflowy.io/docs/appflowy/readme/welcome-to-appflowy.md) — notes/wikis, databases, Kanban and calendar.
10. [SiYuan user agreement](https://b3log.org/siyuan/en/eula.html) — explicit AGPLv3 license statement.
11. [Super Productivity — Open-source productivity guide](https://super-productivity.com/guides/open-source-productivity/) — explicit MIT statement. Promotional comparisons in this guide are not independent evidence.
