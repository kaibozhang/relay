# Native Relay security and reliability review

## Scope and method

Source-only independent review of [Relay.Desktop](<../../native/Relay.Desktop/MainWindow.cs>) and [Relay.Core](<../../native/Relay.Core/VaultStore.cs>), plus the existing [Core test harness](<../../native/Relay.Tests/Program.cs>). Loaded security-auditor. No application execution, registry changes, process launches, test execution, or inspection of unrelated user files. Only this report was written. Parent is actively changing source: line references describe the reviewed snapshot, not a release certification. Critical findings were sent to the parent as soon as established.

Priority: **P1 = fix before trusting real work**, **P2 = fix or explicitly constrain before release**, **P3 = follow-up**. No remote unauthenticated code-execution finding was established. Principal risks are lost drafts/progress and unexpected filesystem/network access.

**Freeze/update from parent after this review:** parent reports conditional startup for --capture and pre-Show integration initialization fixed; protected atomic export, quit cancellation, and stale-edit fingerprint rejection implemented. Parent plans an exclusive physical-vault file handle, resource safety checks, and a checkpoint save callback. These are parent-reported remediation statuses, not independently re-reviewed or executed here. Findings below preserve the original evidence and acceptance tests; do not treat them as a claim that those fixes remain absent. Review paused pending final diff as requested.

## P1: durability and explicit-action boundary

### 1. Quit ignores a canceled draft close

**Evidence:** [MainWindow Quit/OnClosing](<../../native/Relay.Desktop/MainWindow.cs#L234-L241>) sets quitting, closes capture, then closes its owner regardless of whether [CaptureWindow ConfirmDiscard](<../../native/Relay.Desktop/CaptureWindow.cs#L163-L168>) canceled. WPF owner closure also closes owned windows; the owner must not continue after the user refused discard. With a detached capture, unconditional application shutdown still discards it.

**Minimum fix:** close/ask the draft first; if it remains open, return from Quit and leave quitting false. Set quitting only after all cancellable draft decisions succeed. Handle settings/progress dialogs consistently. Avoid relying on owner shutdown to ask for draft disposal.

**Targeted test:** type unsaved text, invoke tray Exit, answer No. App and exact draft must remain, and subsequent main Close must still follow the normal tray preference. Repeat with background capture and with capture's URL child prompt open. Then answer Yes and verify explicit exit.

### 2. Markdown export bypasses protected, atomic vault export

**Evidence:** [ExportCard](<../../native/Relay.Desktop/MainWindow.cs#L221-L225>) calls File.WriteAllText directly on the chosen destination. [VaultStore.Export](<../../native/Relay.Core/VaultStore.cs#L144-L163>) protects main/backup/recovery filenames and uses atomic replacement, but does not participate here. A dialog extension filter is not a destination protection boundary. Direct write can truncate an existing export on failure, and a deliberately chosen live vault or backup path is not checked in code.

**Minimum fix:** share protected-destination validation and an atomic byte/text export helper with JSON export. Explicitly refuse main, backup, recovery evidence, and aliases to those files. Keep dialog overwrite confirmation. Do not reuse Save for Markdown.

**Targeted tests:** export to main, backup, evidence path, case/dot-normalized spelling, and a controlled alias; assert byte-for-byte unchanged. Fault before replacement must leave an existing Markdown export unchanged. Verify temp cleanup is best effort, not a false save failure.

### 3. Editing a whole-card snapshot overwrites newer progress

**Evidence:** [CaptureWindow copy](<../../native/Relay.Desktop/CaptureWindow.cs#L33-L38>) clones the complete card. [Edit SaveHandler](<../../native/Relay.Desktop/MainWindow.cs#L183-L188>) replaces the complete current card with that snapshot. WPF ShowDialog runs a nested dispatcher: [Tick](<../../native/Relay.Desktop/MainWindow.cs#L251-L255>), tray/hotkey dispatch, and the independently owned strip can mutate live state while the editor remains open. For example, focus completion appends a checkpoint, then saving an older edit silently removes it. Vault revision checking does not detect this: the same store is saving a candidate cloned from the latest document containing the stale replacement.

**Minimum fix:** edit only editable fields (title, next action, context, resources) into the latest card; preserve status, timestamps/history, counts, and progress. If other editor actions can change the same editable fields, compare an initial fingerprint and fail visibly on conflict while retaining the draft. Queue/block incompatible modal actions rather than assuming ShowDialog freezes background work.

**Targeted test:** start timer, open edit, let completion checkpoint arrive, modify next action, save; both edits and checkpoint must survive. Repeat with resume/status change through a hotkey and with import while another modal exists.

### 4. Local-looking references can cause passive network/filesystem access

**Evidence:** [RenderDetail](<../../native/Relay.Desktop/MainWindow.cs#L139-L146>) calls [ResourceLauncher.Exists](<../../native/Relay.Desktop/Services/ResourceLauncher.cs#L12>) for every resource, on the UI thread. [Core path validation](<../../native/Relay.Core/VaultStore.cs#L264-L279>) accepts drive-rooted syntax but cannot establish a physical local destination. A mapped drive or an intermediate directory symbolic link to a share can therefore be followed merely by rendering a card, including imported cards. This can trigger network authentication/access and block the UI. [Open](<../../native/Relay.Desktop/Services/ResourceLauncher.cs#L24-L32>) checks only the leaf's reparse attribute after existence probes; reparse folders are not put into unsafe-file handling. [Capture attachment classification](<../../native/Relay.Desktop/CaptureWindow.cs#L212-L237>) also probes Directory.Exists before a physical-local policy check, though attaching is an explicit user action.

**Minimum fix:** do not probe references automatically unless physical-local policy has passed. Reject network drive types and inspect ancestors root-to-leaf for reparse points before following descendants; fail closed to “not checked” rather than “missing.” Prefer an explicit check/open action for uncertain targets. Apply the same policy to reveal/open: Explorer reveal can also access a network-backed parent, so it is not an automatic safe fallback. Do not enumerate unrelated trees. No static precheck completely defeats same-user link-swap races; avoid claiming that it does.

**Targeted tests:** inject a filesystem probe abstraction and assert that rendering mapped-drive/reparse-parent references makes zero descendant existence/open calls. In a later authorized integration fixture, use only a private local alias tree; do not test against real network shares or user folders. Directory links and a normal .txt leaf underneath a link both matter.

## P2: smaller, high-value fixes

### 5. Progress text disappears when persistence fails

[Ui.Prompt](<../../native/Relay.Desktop/Ui.cs#L28-L35>) closes before [Checkpoint](<../../native/Relay.Desktop/MainWindow.cs#L202-L205>) calls Mutate. A lock/full-disk/read-only error therefore loses the typed checkpoint even though [Capture SaveHandler](<../../native/Relay.Desktop/CaptureWindow.cs#L170-L196>) correctly retains the main capture draft.

**Fix/test:** give progress prompt a save callback and close only after success. Lock the vault in a controlled fixture, enter a unique paragraph, save, and verify the prompt/text remain; unlock and retry exactly once.

### 6. Timer completion reports success even when its checkpoint was not saved

[Tick](<../../native/Relay.Desktop/MainWindow.cs#L251-L255>) clears focus state before Mutate and ignores the result before the completion notification. The event is lost on save failure. It also uses current Settings.FocusMinutes for progress and the checkpoint's claimed duration, rather than the duration with which the timer began. Changing 25 to 1 mid-session can produce a 1-minute completion record for a 25-minute timer. Resuming a different card leaves the previous focus running; the shared detail label can show that previous card's countdown on the newly selected card.

**Fix/test:** snapshot duration per session, retain a pending completion until persisted or explicitly dismissed, and distinguish “elapsed” from “checkpoint saved.” Inject a clock for tests. Cover failed save at zero, retry without duplicate checkpoint, settings changes, pause/resume near zero, sleep/time change policy, and resuming card B while A is timing. A monotonic clock is preferable for elapsed work; explicitly decide whether suspend counts rather than adding more scheduling features.

### 7. Hidden window must require a working tray

[MainWindow Loaded and OnClosing](<../../native/Relay.Desktop/MainWindow.cs#L58-L66>) hide using settings/integration-object existence, not actual tray readiness. [WindowsIntegration.HasTray](<../../native/Relay.Desktop/Services/WindowsIntegration.cs#L65>) already exposes the needed state. Feature initialization deliberately permits tray failure without throwing the entire service away.

**Fix/test:** only close-to-tray/start-minimized-hide when HasTray is true; otherwise keep visible or close normally with a clear fallback. Stub failed tray plus failed hotkeys and verify there is no invisible, unreachable owner process.

### 8. Forwarded capture resources are silently dropped when a draft is open

[Capture](<../../native/Relay.Desktop/MainWindow.cs#L176-L182>) only activates an existing capture and returns. [SingleInstance delivery](<../../native/Relay.Desktop/Services/SingleInstance.cs#L145-L156>) acknowledges that the application queued the request, not that its resources were retained. A second right-click on another file while drafting is silently lost.

**Fix/test:** add a bounded append-resources API on the current capture, or a small explicit queue/rejection message. Do not silently attach resources to an unrelated edit without user feedback. Send two distinct resources in sequential external requests and assert both are retained or the second is visibly rejected.

### 9. Vault identity is lexical rather than physical

[App normalization](<../../native/Relay.Desktop/App.cs#L21-L28>) trims/uppercases GetFullPath; [SingleInstance](<../../native/Relay.Desktop/Services/SingleInstance.cs#L45-L55>) hashes that supplied string. Junction/short-name aliases can refer to one vault under two mutex keys. [VaultStore Save](<../../native/Relay.Core/VaultStore.cs#L112-L134>) compares then replaces; its Gate is process-local and cannot make two differently keyed processes' check/write sequence atomic. Export's protected-path comparison is lexical too, so parent-directory aliases bypass protection.

**Fix/test:** canonicalize physical vault identity safely, reject ambiguous/reparse vault roots for the first release, or hold an exclusive lock-file handle in the actual vault for its lifetime. Keep separate physical vaults independently usable. Test same path/case/trailing slash/dot and an explicitly created alias; same physical vault must never admit two writers. Also cover inaccessible vault startup and failed IPC delivery without opening a second writer. Custom-vault integration arguments are being changed by another agent and are deliberately not re-reported here.

### 10. Explorer registration has no rollback after partial failure

[InstallExplorerMenu](<../../native/Relay.Desktop/Services/WindowsIntegration.cs#L277-L307>) preflights ownership, then performs many independent writes with no rollback. A failure halfway through can leave an owner/executable marker without the exact command. [IsOwnedMenu](<../../native/Relay.Desktop/Services/WindowsIntegration.cs#L329-L338>) then fails; retry refuses the conflicting entry and removal skips it. Removal can likewise stop halfway.

**Fix/test:** narrow per-key snapshot/created-key tracking and best-effort exact rollback on failure; never delete foreign data to “repair” a conflict. Report partial installation/removal accurately. A registry adapter permits fault injection after every CreateSubKey/SetValue without touching the user's registry. Verify existing exact owned entries restored and unexpected foreign values/subkeys untouched.

**Quoting/scope assessment:** current keys are HKCU file/directory/background verbs only, not default file associations or startup entries. Command is a quoted executable with known flags; no cmd.exe/PowerShell intermediary. QuoteArgument correctly doubles trailing backslashes for executable/vault values. Explorer's substituted quoted %V/%1 is not passed through that helper: a drive-root/background value ending in backslash deserves an exact Windows argv test. Do not claim shell-command injection merely because a filename has spaces, ampersands, or percent characters.

### 11. Oversized import still reads its entire contents

[ReadSnapshot](<../../native/Relay.Core/VaultStore.cs#L307-L318>) computes SHA256 over the whole stream before applying the 32 MiB limit. UI-thread import/startup can therefore read a multi-gigabyte file in full before rejecting it. Recovery additionally hashes/copies corrupt files to preserve evidence.

**Fix/test:** check size first on the detached import path, without forcing evidence hashing; keep recovery policy explicit and bounded/user-confirmed for oversized corrupt main files. With an instrumented stream, a size-only rejection must not read the body. Existing byte-limit test checks rejection, not bounded I/O.

## Focus/workflow differentiation: minimum honest change

The useful differentiation is “leave one precise next action, then return to the real work,” not broad task management. The initial [Capture](<../../native/Relay.Desktop/MainWindow.cs#L176-L182>) called ShowMain unconditionally and owned the capture by the activated dashboard. That defeats interruption-minimizing hotkey capture. The parent has already started a background capture branch during this review; that is the right direction, not yet runtime verified.

Minimum acceptance criteria:

1. Warm global-hotkey/tray/right-click capture never shows/restores/activates the dashboard first. Capture alone receives input; it does not read clipboard or foreground application state.
2. Save or cancel returns to the previously active application naturally, without a dashboard activation. Keep a visible dashboard in its prior state if the user initiated capture from it.
3. Cold --capture also needs this behavior: [App.Start](<../../native/Relay.Desktop/App.cs#L28>) currently shows the main window unconditionally before argument handling. Initialize the main HWND/native affordances deliberately without a visible dashboard, or document cold capture as a limitation. Do not solve warm focus by introducing a second owner process.
4. Errors are owned by the active capture. Mutate currently owns its error MessageBox by the main window; a hidden-main background route must not hide the error behind the modal or reactivate the dashboard.
5. Reentrant hotkeys while settings, file pickers, discard prompts, or capture URL prompts are open activate/queue appropriately rather than opening competing sibling modals. Resume during edit must not overwrite that editor's metadata.
6. The resume hotkey also calls ShowMain today; optionally make it reveal only the strip for a real lightweight return. This is below draft durability in priority. Strip checkpoint/complete currently shows the dashboard too; do not describe that path as completely interruption-free until changed.

**Focus regression matrix:** main visible/hidden/minimized × capture via main/hotkey/tray/IPC × save/cancel/save-failure; add cold --capture and repeated hotkey while child URL dialog is open. Check active HWND before, during, after using the parent's approved UI harness. This review did not run it.

**Honest product gaps to retain in copy:** references are not session restoration; no unsaved editor state is recovered; timer and reminders operate only while running; drafts survive a failed save within the dialog, not an application crash; saved JSON is unencrypted; shell associations/default browser are external trust boundaries. No need to add cloud, screenshotting, application crawling, or clipboard monitoring to differentiate this workflow.

## Positive controls verified in source

- [VaultStore](<../../native/Relay.Core/VaultStore.cs>) validates strict bounded JSON, required members, duplicate/unknown fields, UTF-8/Unicode, IDs, counts, dates and local-path syntax. Save clones before touching caller metadata, writes a same-directory unique temp with durable flush, replaces atomically, keeps a prior backup, fingerprints stale writers, and preserves corrupt evidence. This is a good basis; do not replace it with a simpler direct write.
- [Import](<../../native/Relay.Core/VaultStore.cs#L165-L173>) is detached, validated and requires UI confirmation before replacement; invalid import does not execute resources. [Core tests](<../../native/Relay.Tests/Program.cs#L5-L27>) cover 20 durability/schema cases by source inspection, not a run in this review.
- [Capture save](<../../native/Relay.Desktop/CaptureWindow.cs#L170-L196>) keeps the draft if SaveHandler returns false or throws, and close asks before discarding dirty data.
- [Clipboard](<../../native/Relay.Desktop/CaptureWindow.cs#L305-L319>) is only read from the explicit paste button. No passive clipboard watcher was found in the reviewed source.
- [SingleInstance](<../../native/Relay.Desktop/Services/SingleInstance.cs>) uses a user-scoped mutex/pipe, a dedicated mutex-owning thread, argument allowlisting, bounded framed payloads, a deadline and acknowledgement; failure does not quietly start another owner.
- [ResourceLauncher](<../../native/Relay.Desktop/Services/ResourceLauncher.cs>) no longer directly opens .js/.py/.html/.svg. Unsafe extensions are revealed instead, HTTP(S) URLs and allowlisted documents ask first. This is not an association-independent guarantee: .doc/.xls/.ppt and even text extensions launch configured external handlers; arbitrary local associations cannot be made safe by an extension allowlist. Keep explicit confirmation and accurate wording. A generic “reveal only” action is a useful safer choice. Resolve explorer.exe from the Windows directory rather than relying on executable-name search as inexpensive hardening.

## Recommended order within the existing two-hour budget

1. Fix canceled quit, stale editor overwrite, and protected atomic Markdown export; add the three regression cases before spending time on polish.
2. Remove passive unsafe resource probing; guard tray hiding by HasTray. Preserve failed checkpoint drafts and pending timer completion.
3. Finish warm and cold focus routes, active-modal error ownership, and repeated-resource handling; run a small approved focus matrix.
4. Add/verify Explorer write rollback and physical vault ownership protection. Use fault-injected pure adapters rather than mutating the real shell to test.
5. If time remains: timer duration snapshot, bounded oversize import I/O, exact root-path argv test. Record unverified remainder explicitly.

No feature expansion is required for any of the first four steps. The main difference from a generic todo app can remain a trustworthy, low-friction capture-to-resume loop.

## Final narrow source re-review (parent-requested pass)

Re-read changed [App](<../../native/Relay.Desktop/App.cs>), [MainWindow](<../../native/Relay.Desktop/MainWindow.cs>), [ResourceSafety](<../../native/Relay.Desktop/Services/ResourceSafety.cs>), [AtomicExport](<../../native/Relay.Desktop/Services/AtomicExport.cs>), [ResourceLauncher](<../../native/Relay.Desktop/Services/ResourceLauncher.cs>), [FocusSessionStore](<../../native/Relay.Desktop/Services/FocusSessionStore.cs>), [ProgressWindow](<../../native/Relay.Desktop/ProgressWindow.cs>), [ResourceResumeWindow](<../../native/Relay.Desktop/ResourceResumeWindow.cs>), [WindowsIntegration](<../../native/Relay.Desktop/Services/WindowsIntegration.cs>), [ShortcutIntegration](<../../native/Relay.Desktop/Services/ShortcutIntegration.cs>), and changed Core export/oversize paths. Still source-only: parent's reported test counts and cold/warm capture evidence were not independently rerun.

### Confirmed improvements (supersede original findings where stated)

- [App startup](<../../native/Relay.Desktop/App.cs#L29-L36>) initializes native integration without showing the dashboard for initial capture/resource requests. Warm capture uses a detached dialog. The exclusive physical-vault lock handle addresses separate lexical aliases admitting two writers; aliases may fail with a clear message rather than forward, which is acceptable for durability.
- [Capture and Edit](<../../native/Relay.Desktop/MainWindow.cs#L179-L191>) append repeated resources, retain persistence-failed drafts, and fingerprint-check stale edits before replacement. [Quit](<../../native/Relay.Desktop/MainWindow.cs#L243>) now honors capture close cancellation. [OnClosing](<../../native/Relay.Desktop/MainWindow.cs#L239-L241>) requires HasTray before hiding.
- [ProgressWindow.Save](<../../native/Relay.Desktop/ProgressWindow.cs#L90-L109>) calls persistence before success/close, protects against reentrant saves, and retains input on false/exception. The next action and checkpoint are saved in one vault transaction.
- [ResourceSafety](<../../native/Relay.Desktop/Services/ResourceSafety.cs#L13-L31>) checks drive type and ancestor reparse attributes root-to-leaf before descending. [ResourceLauncher](<../../native/Relay.Desktop/Services/ResourceLauncher.cs#L12-L24>) uses it for display checks and before opening. This closes the ordinary mapped-drive/reparse-parent passive-probe path; it is not a race-proof guarantee against a hostile same-user filesystem mutator.
- [Focus settlement](<../../native/Relay.Desktop/MainWindow.cs#L269-L287>) persists pending intent, writes a checkpoint containing a session marker only if absent, then acknowledges via a durable tombstone. [FocusSessionStore](<../../native/Relay.Desktop/Services/FocusSessionStore.cs#L173-L220>) preserves memory state until commit and snapshots session duration. Restart recovery is now part of the product, superseding the original report's “timer only while running” limitation: notifications still require a running app.
- [Core size check](<../../native/Relay.Core/VaultStore.cs#L309-L343>) precedes hashing and bounds corruption preservation. [Registry installation](<../../native/Relay.Desktop/Services/WindowsIntegration.cs#L370-L478>) now journals value changes, tracks key creation disposition, restores exact prior kinds/data, and preserves concurrent foreign edits. [Shortcuts](<../../native/Relay.Desktop/Services/ShortcutIntegration.cs#L64-L115>) are explicit opt-in, stage same-directory publication, require exact owner/executable/action provenance, and do not register file associations or autostart.

### R1 — P1: complete-backup export still bypasses the new protection

[MainWindow settings export](<../../native/Relay.Desktop/MainWindow.cs#L305>) still calls [Core.Export](<../../native/Relay.Core/VaultStore.cs#L146-L164>). It protects only the lexical main-vault path and its suffixes. **Exporting a full backup directly onto the live focus state or focus backup is allowed**, without any alias, and destroys that timeline. Parent-directory junction/short-name aliases can still bypass the lexical main-vault guard. The Markdown [AtomicExport](<../../native/Relay.Desktop/Services/AtomicExport.cs#L9-L18>) blocks reparse directories but compares vault directory text: short-name/substituted-drive aliases are not inherently excluded by ResourceSafety.

**Narrow fix:** unify protected destination policy for both export formats, including focus state/evidence. A conservative reserved-basename refusal everywhere is simpler than inventing incomplete physical canonicalization during this release; otherwise use physical directory/file identity. Validate before writing. Test the *actual settings JSON-export route*, not only Markdown helper: direct focus-state/backup destinations, main-vault alias, and ordinary valid backup destination, asserting original bytes unchanged. This was sent to parent immediately.

### R2 — P1: dirty progress dialog is not included in shutdown cancellation

[Checkpoint](<../../native/Relay.Desktop/MainWindow.cs#L206-L210>) keeps its ProgressWindow only in a local variable; [Quit](<../../native/Relay.Desktop/MainWindow.cs#L243>) handles only capture. ProgressWindow is owned by the main window. Tray Exit can therefore close its owner without first honoring the progress dialog's dirty-discard cancellation, repeating the original capture loss pattern in the new dialog.

**Narrow fix:** track active progress/all cancellable draft windows; explicitly request close and abort exit if any remains visible, before setting quitting. Route persistence error ownership to the active progress dialog too: Mutate still chooses only capture or main. Test dirty progress → tray Exit → No retains exact progress and next-action text; then Yes exits. Also test write failure leaves an accessible progress-owned error and retained input. Sent to parent immediately.

### Small residuals to fix or document; not grounds for scope expansion

- [Tick](<../../native/Relay.Desktop/MainWindow.cs#L289-L295>) still updates the current detail label using the running session's card ID, even if another card is displayed. Bind the label to the displayed card ID. This is a misleading display, not timer persistence loss.
- Idempotence depends on marker text in a user-visible/editable checkpoint. If a user removes/replaces the marker via external editing or import while a completion remains pending, restart can write it again. This is not a typical crash-only duplicate; document that exactly-once applies while recorded markers remain intact. Do not add another persistence subsystem solely for this edge case.
- [Capture resource classification](<../../native/Relay.Desktop/CaptureWindow.cs#L215-L226>) still calls Directory.Exists after lexical validation, not ResourceSafety. Unlike list rendering this follows explicit attachment, but it can follow a mapped/reparse parent while saying local-only. Reuse the guard before classification, or accept it as an explicitly disclosed attachment-time check. No need to scan contents.
- [Explorer rollback](<../../native/Relay.Desktop/Services/WindowsIntegration.cs#L410-L424>) records failed undos in exception Data, but settings displays only error.Message. Report “may be partially installed; rollback also failed” when that data exists; avoid telling the user simply “not enabled.” Removal remains nontransactional and safely skips conflicts.
- [ResourceLauncher reveal](<../../native/Relay.Desktop/Services/ResourceLauncher.cs#L31>) still uses a bare explorer.exe name. A fully qualified Windows-directory path removes executable-search ambiguity with a one-line change; external document associations remain a user/system trust boundary, not an executable whitelist guarantee.

**Final requested regression set:** full-backup protected destinations/alias; dirty-progress canceled exit; failed progress save ownership; pending-focus crash boundaries (before checkpoint / after checkpoint before acknowledgement); A-timer/B-detail labeling. Keep the existing tests and ongoing endurance run; no new feature work is required. The two P1 items above are the only new release-gating findings in this pass, not a claim that earlier parent fixes failed.

### Targeted remediation confirmation after final pass

Source checked, no execution: [settings JSON export](<../../native/Relay.Desktop/MainWindow.cs#L306>) now validates by cloning through Core and serializes through AtomicExport; [reserved basename policy](<../../native/Relay.Desktop/Services/AtomicExport.cs#L14-L17>) blocks backing-data names regardless of destination directory. [Quit](<../../native/Relay.Desktop/MainWindow.cs#L244>) tracks progress and honors its canceled close before owner shutdown, and Mutate chooses progress as error owner. The displayed focus label now uses the selected card ID, and Explorer is resolved from the Windows directory. These changes address the ordinary R1/R2 reproductions; runtime regression results remain the parent’s responsibility.

One exact alias-coverage nuance sent to parent: rejecting long reserved basenames protects short-name/substituted-drive *parent directory* aliases, but not an existing backing file’s own generated 8.3 leaf name when short names are enabled. Test the actual short leaf returned for a fixture backing file, skipping explicitly when unavailable. Reject short-name leaf syntax conservatively or compare a resolved long name/physical identity before claiming all 8.3 alias cases covered. This is a narrow R1 edge, not a request for another broad audit.

