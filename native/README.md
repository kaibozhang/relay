# Native Relay solution map

The desktop application is pure WPF/.NET10, without WebView2 or a browser engine. Runtime UI data is not browser storage. No third-party NuGet dependencies are required.

## Production

- [Relay.Desktop](<Relay.Desktop/Relay.Desktop.csproj>): application, native windows, tray, shortcuts, taskbar, explicit resource opening.
- [Relay.Core](<Relay.Core/Relay.Core.csproj>): validated models, atomic durable vault, recovery, imports/exports.

## Tests

- [Relay.Tests](<Relay.Tests/Program.cs>): core disk and schema.
- [Relay.FocusTests](<Relay.FocusTests/Program.cs>): separate persisted focus intent and acknowledgement.
- [Relay.PolicyTests](<Relay.PolicyTests/Program.cs>): quiet hours and acknowledged reminders.
- [Relay.SafetyTests](<Relay.SafetyTests/Program.cs>): local path/reparse/export and lock primitives.
- [Relay.LauncherTests](<Relay.LauncherTests/Program.cs>): launch plans, never executes references.
- [Relay.MigrationTests](<Relay.MigrationTests/Program.cs>): explicit legacy JSON migration.
- [Relay.DialogTests](<Relay.DialogTests/Program.cs>): actual WPF retained drafts and modal exits.
- [Relay.AccessibilityTests](<Relay.AccessibilityTests/Program.cs>): targeted native keyboard behaviors.
- [Relay.Automation](<Relay.Automation/README.md>): real process/UIA and controlled keyboard input.
- [Relay.PackageTests](<Relay.PackageTests/Program.cs>): unpacked release portable data and CLI.
- [Relay.PerformanceTests](<Relay.PerformanceTests/Program.cs>): core/query benchmarks, not UI latency.
- [Relay.Soak](<Relay.Soak/Program.cs>): sustained randomized data roundtrips and fault injection.

Each test owns workspace-local temporary data. Never point these runners at real user vaults. UI tests are intentionally serial because global hotkeys/focus are real system resources. Tests report unavailable environment fixtures rather than pretending they passed.

## Build and publish


dotnet build native/Relay.Desktop/Relay.Desktop.csproj -c Release

Run the [publishing script](<../scripts/Publish-Relay.ps1>) from PowerShell7.2+ and the [release check](<../scripts/Test-Release.ps1>) under PowerShell7.6+. Application end users need neither SDK nor PowerShell when using the self-contained ZIP.

## Ownership boundaries

The UI validates a candidate before acknowledging disk success. Core save does not accept concurrent external writers; App holds both a user-scoped named mutex and an exclusive physical lock file. Resources are references only, not copied files or executable commands. Diagnostics render only this app's own WPF visual trees and are disabled unless an explicit diagnostic command argument is supplied.
