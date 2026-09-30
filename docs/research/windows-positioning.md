# Windows positioning: manual interruption recovery, not another notes app

## Decision

**Retain the idea only as a narrow Windows interruption-recovery companion, with a go/no-go benchmark against configured existing tools. Do not implement a generic notes/tasks dashboard.** The proposed benefit is reduced effort to leave an actionable checkpoint and regain the next action after an interruption—not a new category or an invented feature.

Serious overlap already exists: Task Coach combines descriptions, reminders and file/URL attachments; CherryTree combines notes and file/folder/web links; QOwnNotes provides C++/Qt, disk-backed Markdown and portable Windows operation; Ditto provides durable capture and a global hotkey; Flow provides global invocation and resource launching; Workona explicitly sells context switching and session recovery. Local storage, portable executables, references, hotkeys, and “pick up where you left off” are **not defensible novelty claims**.[1–9]

The must-have differentiator is an **action-first checkpoint → resume loop spanning real desktop work**, with one intentional capture surface, restart-safe storage, one safe resume action, and a small continuation strip visible beside the actual application. Keep the new tool only if this integrated loop measurably beats the best configured baseline without sacrificing safety. This is a workflow hypothesis, not a proven retention benefit.

## Scope and evidence quality

This bounded review follows the prior [GitHub landscape](<D:/DPH/docs/research/github-landscape.md>). The user now permits SourceForge and official documentation; GitHub access is **not** a prerequisite for this positioning decision. The earlier blanket “wait for GitHub” recommendation is superseded for research, not retroactively satisfied as a repository audit.

- Evidence below is content actually retrieved from official sites or project-maintained SourceForge listings. These are documented capabilities, **not hands-on empirical performance measurements**. No competitor was installed or exercised; no app code was changed.
- A feature not found in inspected pages is **unverified**, not absent. Plugin catalogs and the market were not exhausted. We cannot claim no existing tool implements the whole loop.
- Microsoft Learn's Workspaces URL was refused because its hostname resolved to a non-public IP. No alternate transport, proxy, IP, mirror, or authentication loop was used. PowerToys Workspaces remains an important **unverified** comparator, not evidence supporting a competitive gap.[13]
- No GitHub, Gitee, or GitLab source/release/license content was fetched. Official pages linking those sites were read for their own independently published content only.
- Ditto's project homepage returned only a small counter-like shell; its SourceForge project description supplied usable evidence. SourceForge responses were marked truncated, but cited descriptions and feature lists appeared in the returned body. Advertisements and user reviews were not used as proof.
- Task Coach's feature page has an old release banner while SourceForge contains different activity/migration information. Capability statements are used, not timestamps to infer maintenance health, compatibility, or latest versions.
- Canonical-domain redirects for Task Coach and Workona were followed directly. A prospective SmartWindows address failed DNS; TaskLayout's vendor page returned HTTP 503 with an anti-bot message and a guessed SourceForge project returned 404; a Vivaldi session-help fetch failed twice. These are not capability evidence. No challenge was bypassed; no transient request exceeded three attempts.
- Source/dependency licenses must be checked before reuse/distribution. Project license labels are not a component audit. This report does not recommend copying or forking code.

## Documented overlap and remaining questions

| Alternative | Retrieved capability and overlap | Configure-first use case | Gap to validate, not an absence claim |
| --- | --- | --- | --- |
| **Task Coach** | Windows; tasks/subtasks with subject, description, dates and optional reminder; drag/drop files, email and URLs onto tasks/notes/categories; XML task file; persistent settings and reopening last file; removable-media operation; time tracking.[1][2] | Make the subject the next action, context the description, attach resources and set a reminder. Strongest small task-oriented baseline. | Does configured capture/resume require more navigation? Can it keep the action visible beside another app with comparable friction? Dedicated global capture and integrated safe bulk-resume were not established. Do not call it slow without timing it. |
| **QOwnNotes** | C++/Qt Windows notes; plain-text/Markdown files, scripting, dockable/floating panels, portable mode. Windows docs explicitly say unzip/run without installation. Shortcuts cover new note, links, attachments and opening links.[3][4][5] | A checkpoint template and script, with explicit links rather than copied attachments, may suffice. Strong option for existing Markdown users. | A floating panel is not automatically an always-on-top continuation strip. Main-window shortcuts do not prove Windows global capture. Attachment insertion copies files, so reference-only behavior must be deliberate. |
| **CherryTree** | Hierarchical notes in XML/SQLite or multiple files; links to webpages, nodes, files and folders; paste a copied file list to create links; to-do lists; Windows installer and portable archive.[6] | A “Paused work” node template already holds next action, context and references. | Dedicated interruption hotkey, action-first resume transaction and scheduled recovery flow were not established. Full note editing may impose extra attention; measure rather than assume. |
| **Ditto** | Windows clipboard extension saves clipboard items; configurable saved types; search/paste; tray or global hotkey; SQLite; optional cross-computer clipboard sync.[7] | If recovering copied text/images is the real problem, use Ditto. | Clipboard history differs from an intentional next-action/resource bundle, but a text checkpoint in Ditto is a legitimate low-effort baseline. Proposed capture must not silently become background clipboard collection. |
| **Flow Launcher** | Configurable global hotkey (default Alt+Space), apps/files/file-content/bookmark search, system/shell commands, plugins, portable distribution.[8] | Optional capture/search entry point through a plugin if API fit is verified. | Persistence, reminders and a beside-work strip were not established. APIs and existing plugins remain unverified. Do not duplicate its indexing or shell-command feature set. |
| **Workona / browser sessions** | Project spaces group tabs and show current context; session autosave, crash restoration, cloud backups, cross-computer sync and quick context switching; Chrome, Firefox and Edge listed.[9] | For browser-dominated work, configuring spaces may solve more than a new Windows utility. | Retrieved evidence concerns tabs, not native reference-only desktop checkpoints. Session restoration and next-action recall differ but are complementary. This is direct evidence work-context recovery is not new. |
| **PowerToys Workspaces** | Official page inaccessible under network policy.[13] | Must be evaluated later if app/window working-set restoration dominates the need. | No capability or limitation is asserted from the inaccessible document. Its exclusion limits confidence and cannot justify uniqueness. |
| **Joplin + configuration/plugin** | Local SQLite; Electron/React desktop; plugins installed/configured inside Joplin with restarts; local links via Alt+drag; attachment opening in default apps with unsafe-type warnings.[10][11][12] | Strong for existing Joplin users needing mature notes/search and a broader ecosystem. Try a checkpoint template before writing a plugin. | Native independent capture/strip and global-hotkey API fit were not verified. A plugin depends on the Joplin host being present/runnable, configuration and host lifecycle. That is an adoption tradeoff, not an impossibility. |

## Why keep a new tool rather than configure existing software?

### Configure first when existing tools fit

Recommend Task Coach for task/reminder/resource bundles; QOwnNotes or CherryTree for checkpoint notes; Ditto for clipboard recovery; Workona for tab sessions. Existing users should not migrate a knowledge base to obtain a next-action template. A template, pinned note or launch shortcut may fully solve the need. Scheduled reminders and file references alone do not justify building.

Use a competent baseline configuration, not an empty/default app: create the interruption template, arrange a recovery list, set available shortcuts/reminders, and practice before measuring. Give every baseline the same references/context. If configuration wins, recommend it openly.

### Extend Joplin (or optionally Flow) for existing ecosystem users

A Joplin plugin avoids rebuilding its persistence/search/editor ecosystem, but users need the Joplin host available and must install/configure the plugin. Official docs substantiate the host dependency and restart lifecycle, not whether all desired Windows integrations fit its API.[10][11] “Installed dependency” means the host must be present; it does not mean Joplin necessarily requires an installer.

Flow could be an optional integration later; making it mandatory adds another host dependency. Neither extension path should be dismissed just because it is not a standalone executable. Prove API and UX fit before selecting a base.

### Keep the native companion only for the lower-friction loop

The standalone case is **no browser launch, no editor ecosystem prerequisite, no taxonomy to maintain and no knowledge-base migration**. Intentionally checkpoint from the current desktop app and return to the action without entering a general notes workspace. QOwnNotes and CherryTree already offer portable Windows software; native/portable is a delivery constraint and potential adoption advantage, not exclusivity.

A separate tool creates obligations: hotkey conflicts, focus/accessibility, atomic persistence, migrations/backups/export, target validation, opening failures, scheduling across sleep/reboot and Windows packaging/signing. These costs require measured value. Do not fork an entire notes app for a small strip. Do not claim speed, reduced abandonment, retention or cognitive benefit until tested.

## Recommended must-have behavior (proposal, not implementation)

**One semantic object:** required nextAction, optional short context, explicitly added typed references (file/folder/HTTP(S) URL), timestamps and optional resurfacing time. No forced project, tag, due date, priority, streak or recurring-task setup.

1. **Capture:** configurable global hotkey opens a real Windows capture card focused on next action; Ctrl+Enter commits. Add references by picker, drag/drop or deliberate paste; preview exact targets. No working-set inference by file crawling or window/clipboard monitoring.
2. **Durability:** “Saved” appears only after a completed disk transaction; recover after termination/restart; expose data location and export/backup. Browser localStorage alone is not this durability contract. On failure retain the editable card and show an actionable error, never a false success.
3. **Resume:** one click on a saved card displays action/context and opens its explicitly selected validated safe references. Report each success/failure and missing target. Never open references merely because a reminder fires. Do not promise unsaved-edit, application-state or window-layout restoration.
4. **Continuation strip:** compact, optional always-on-top native window holds the current action and minimal controls beside real work. Keyboard accessible, dismissible, no unsolicited focus stealing; pause/update/finish without gamification. Not another dashboard.
5. **Resurface:** chosen time offers Resume / Snooze / Dismiss, respects quiet hours, and has no streaks, guilt copy, escalating badges or overdue nagging. Coalesce missed reminders after sleep/restart. Scheduling while the executable is closed needs an explicitly approved mechanism; a portable app must not secretly install services/startup tasks.

**Safe opening boundary:** never construct shell commands from references. Default permit only deliberately selected ordinary documents/folders and HTTP(S) URLs. Block or separately confirm executables/scripts/shortcuts, custom protocols and network/UNC locations. Revalidate on resume: targets and associations can change. Document opening is not guaranteed harmless; this is risk reduction, not sandboxing. Preserve missing references for repair; do not silently copy/sync referenced contents.

## Five testable acceptance metrics / release gates

Targets below are **proposed, not measured**. Record Windows version, CPU/storage, data size, build, reference types and warm/cold state. Compare within participants against the best configured relevant baseline. Start with at least 5 representative users × 10 counterbalanced interruption/resume trials per condition; this is a pilot, not population-level efficacy evidence.

| # | Metric and test | Acceptance target |
| --- | --- | --- |
| **1** | **Invocation/capture effort.** 100 warm hotkey invocations across foreground apps, 20 cold launches. Time minimal cards and cards with one file, folder and URL; count non-text interactions. | Warm hotkey-to-editable-card p95 ≤300 ms; cold launch p95 ≤2 s on declared PC. Minimal capture is one hotkey + next-action text + Ctrl+Enter, no other required field. Median three-reference capture ≤20 s and ≥25% faster than best configured baseline, without more incorrect references. |
| **2** | **Recovery usefulness.** After unrelated interruption and restart, measure card selection to first correct productive action including resource opening. Record wrong-context resumes and strip focus. | ≥90% identify saved next action without external searching; median first-productive-action time ≤15 s and ≥25% better than baseline. One Resume activation for previously approved safe set. Zero unsolicited strip focus steals in 100 foreground-app switches. Report distributions and failures. |
| **3** | **Acknowledged-save survival.** Inject faults around commit/ack, force-kill/relaunch 100 times, perform 20 OS-restart trials; verify content, references, schedules and export/reimport. | Zero lost/corrupt acknowledged cards, false “Saved” acknowledgments or recovery duplicates. Document filesystem/flush assumptions; process-kill tests alone do not prove power-loss durability. |
| **4** | **Explicit capture/safe resume.** At least 50 adversarial cases: missing/moved files, Unicode/spaces, changed target types, .exe/.bat/.ps1/.lnk, custom protocols, UNC, credential-bearing URLs, shell metacharacters. Inspect persistence during an idle privacy test. | Zero unapproved launches/commands/copied contents. All blocked/missing references get per-item results while safe items remain resumable. No passive clipboard/window-title/screenshot history in storage. No reminder launches references without user action. |
| **5** | **Non-nagging resurfacing/standalone operation.** 30 schedules spanning awake, sleep/wake, restart, quiet hours, snooze/dismiss; clean supported Windows accounts offline without Joplin, Flow or browser processes. | While running/awake, due surface within 60 s; after wake/start, at most one coalesced pending surface within 60 s outside quiet hours. Zero repeat alerts after dismiss until explicitly rescheduled; zero guilt/streak escalation or unsolicited focus grabs. Capture/save/resume/strip work in all clean-account trials without admin rights, network or another productivity app. |

**Go/no-go:** safety/durability gates are mandatory. If capture/resume is no better than configured Task Coach, QOwnNotes/CherryTree or an existing-user Joplin setup, stop or narrow further. A pilot pass permits a limited native prototype decision, not a novelty or retention claim. Mobile sync, general task management, automatic working-set restoration and passive activity logging are outside initial scope.

## Sources actually retrieved

1. [Task Coach — Features](https://taskcoach.org/features.html): descriptions, reminders, drag/drop references, XML and removable media.
2. [Task Coach — SourceForge](https://sourceforge.net/projects/taskcoach/): task/note/category hierarchy, reminders, Windows; activity labels not maintenance proof.
3. [QOwnNotes — Overview](https://www.qownnotes.org/getting-started/overview.html): C++/Qt, Markdown, scripts, floating panels, portable mode.
4. [QOwnNotes — Windows installation](https://www.qownnotes.org/installation/windows.html): unzip/run, no installation, portable data/config.
5. [QOwnNotes — Shortcuts](https://www.qownnotes.org/getting-started/shortcuts.html): links, notes, attachment-copy semantics; main-window shortcut scope.
6. [CherryTree — Official features/downloads](https://www.giuspen.net/cherrytree/): reference links, pasted file lists, storage and portable archive.
7. [Ditto — SourceForge](https://sourceforge.net/projects/ditto-cp/): clipboard history, hotkey, SQLite, network sync.
8. [Flow Launcher — Official overview](https://www.flowlauncher.com/): global activation, resource search, plugins, portable distribution.
9. [Workona — Official overview](https://workona.com/): tab spaces, context switching, session autosave/restoration/cloud backup.
10. [Joplin — Plugins](https://joplinapp.org/help/apps/plugins/): host configuration, package installation, restart lifecycle.
11. [Joplin — Architecture](https://joplinapp.org/help/dev/spec/architecture/): Electron/React desktop, local SQLite.
12. [Joplin — Attachments](https://joplinapp.org/help/apps/attachments/): Alt+drag local links, unsafe-file warnings.
13. [PowerToys Workspaces — intended comparator, NOT retrieved](https://learn.microsoft.com/en-us/windows/powertoys/workspaces): follow-up only; no claims derive from this URL.
