# Native desktop and Android build feasibility

Probe date: 2026-09-30, approximately 14:53–14:58 +08:00. Workspace confirmed by `pwd`: `D:\DPH`. **Paused at user request; no implementation or further probes should start until authorized.**

## Decision / ADR

**Recommend C# WPF + WebView2 for the two-hour Windows prototype.** It creates an independent Windows EXE, not a user-browser tab, and uses installed/cached toolchains. Use WPF for taskbar thumbnail buttons, Windows Forms NotifyIcon for tray, Win32 RegisterHotKey for global hotkey, and native file services for actual disk data. Electron is technically suitable but its 158 MB runtime is not installed/proven downloadable end-to-end; registry access alone is not proof. Native Android APK is feasible in principle but its build prerequisites are missing.

Constraints: actual disk-backed data; own EXE/APK; no DSH executable or checkout reuse; no registry/settings/firewall changes; no private document/profile inspection; no phone access. Read-only host/SDK inspection and public metadata/HEAD requests only. No packages installed, no binaries downloaded, no compile/launch proof performed. Only this report was written; application source was untouched.

## Exact local evidence

Commands used: `pwd`, `Get-Command dotnet,csc,msbuild,node,npm,npx,java,javac,gradle,adb,sdkmanager,curl.exe`, `dotnet --info`, `dotnet --list-sdks`, `dotnet --list-runtimes`, `node --version`, `npm --version`, `java -version`, compiler `/help`, MSBuild `-version`, and targeted expected SDK directory listings.

| Item | Observed result |
|---|---|
| OS | Windows 10.0.29648, win-x64 |
| dotnet command | `C:/Program Files/dotnet/dotnet.exe` |
| SDKs | 9.0.311, 10.0.103, 10.0.401 |
| Selected SDK | 10.0.401, SDK MSBuild 18.9.11 |
| Desktop runtimes | 8.0.24, 8.0.25, 9.0.13, 10.0.3, 10.0.12 |
| Desktop targeting packs | 8.0.24, 9.0.13, 10.0.3, 10.0.12 |
| Workloads | None; no Android packs in dotnet packs directory |
| MSBuild command | `C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/amd64/MSBuild.exe`, version 18.10.1.42706 |
| Framework csc | Both Framework and Framework64 `v4.0.30319/csc.exe` exist; x64 reports compiler 14.8.10000.0, **C# 5 only** |
| Framework desktop assemblies | System.Windows.Forms, WPF PresentationFramework and PresentationCore present |
| Node / npm | Node v24.16.0, npm 11.13.0; `C:/Program Files/nodejs` |
| WebView2 runtime | `C:/Program Files (x86)/Microsoft/EdgeWebView/Application/154.0.4258.37/msedgewebview2.exe`, file version 154.0.4258.37, 4,862,792 bytes |
| WebView2 SDK | Cached NuGet Microsoft.Web.WebView2 1.0.2903.40, metadata and build targets read |
| Cached self-contained runtime packages | Both microsoft.netcore.app.runtime.win-x64 and microsoft.windowsdesktop.app.runtime.win-x64: 8.0.13, 9.0.2, 9.0.9, 9.0.11. No net10 runtime package observed. |

WebView2 cached root: `C:/Users/user/.nuget/packages/microsoft.web.webview2/1.0.2903.40`. Confirmed assets relative to this root:

```text
lib/net462/Microsoft.Web.WebView2.Core.dll
lib/net462/Microsoft.Web.WebView2.WinForms.dll
lib/net462/Microsoft.Web.WebView2.Wpf.dll
lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.Core.dll
lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.WinForms.dll
lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.Wpf.dll
runtimes/win-x64/native/WebView2Loader.dll
build/Microsoft.Web.WebView2.targets
build/Common.targets
```

Use PackageReference rather than manually selecting legacy assemblies: the package build targets select appropriate managed assemblies and native loader. A direct-reference fallback can use the modern managed paths above plus explicitly copied x64 loader, but that was not compiled.

## Desktop design and packaging

```text
Own EXE -> WPF native window -> embedded WebView2 local UI
         -> native file service -> user-selected filesystem locations
         -> tray / hotkey / taskbar / native menus
```

Suggested project settings (not executed):

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows</TargetFramework>
  <UseWPF>true</UseWPF>
  <UseWindowsForms>true</UseWindowsForms>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.2903.40" />
</ItemGroup>
```

- Tray: `System.Windows.Forms.NotifyIcon` + `ContextMenuStrip`; explicit Quit, safe restore/minimize behavior.
- Global hotkey: Win32 `RegisterHotKey` / `UnregisterHotKey`, receive `WM_HOTKEY`; report conflicts instead of claiming success.
- Thumbnail toolbar: WPF `TaskbarItemInfo` + `ThumbButtonInfo`; actual taskbar buttons, not an imitation in the web page.
- Context menu: native in-app/tray menu feasible. Explorer shell integration is a separate feature typically requiring registration and must **not** be implemented under the no-registry-change constraint.
- Disk data: native `System.IO` services; pick explicit directories, validate/canonicalize paths, handle permission/locked-file errors, keep writes atomic. No fake sample listing represented as live files.
- Security: local-only WebView navigation, narrow validated message bridge, no arbitrary JS-driven filesystem/command execution, dedicated writable WebView user-data directory; no remote untrusted content granted file privileges.

Packaging command after authorization:

```powershell
dotnet publish <project> -c Release -r win-x64 --self-contained true -o dist/windows
```

Deliver a ZIP of the **entire publish folder**, not just its EXE. Keep WebView loader, managed dependencies, UI assets and licenses. This is an independent desktop app even though it embeds Chromium. Self-contained includes .NET but **not Evergreen WebView2**. Detect WebView2 absence at startup and provide clear remediation; do not silently install it. For other machines either require Evergreen installation or intentionally bundle a licensed Fixed Version runtime (large extra download).

Current net10 self-contained publish may need runtime-pack downloads despite installed shared runtimes. NuGet index is reachable but package retrieval not proven. Offline prototype fallback: target net9.0-windows and explicitly pin `RuntimeFrameworkVersion=9.0.11`, `TargetLatestRuntimePatch=false`, using cached runtime packages; this is an older patch and needs security updates before distribution, and publish remains untested. Alternatively framework-dependent net10 builds can run on this machine, but are **not** self-contained for arbitrary PCs. Last-resort Framework csc works with C#5 constraints and cached net462 WebView2 assemblies; not recommended over modern SDK.

For a two-hour scope, prioritize launch + live file listing first, then native integrations, then publish folder and clean-machine dependency/error tests. No guarantee all acceptance tests fit two hours; runtime availability and real UI interaction must be verified during implementation.

## Electron feasibility and network evidence

Electron satisfies independent desktop EXE, Node filesystem access, Tray/Menu/globalShortcut, and Windows `BrowserWindow.setThumbarButtons`. It has simpler all-JS integration but a larger download/distribution. Use sandboxed renderer, context isolation, disabled nodeIntegration, narrow preload IPC. Package a full Windows runtime folder via Electron tooling; do not relabel or reuse DSH. Installer/signing/SmartScreen are separate packaging concerns.

| Endpoint / probe | Exact outcome |
|---|---|
| [npm metadata](https://registry.npmjs.org/electron/latest), Invoke-RestMethod GET | Success; electron 44.5.0, wrapper unpackedSize 1,165,665 bytes |
| [npm tarball](https://registry.npmjs.org/electron/-/electron-44.5.0.tgz), curl HEAD | Failed TLS revocation check: curl35, CRYPT_E_REVOCATION_OFFLINE 0x80092013 |
| [official x64 binary](https://github.com/electron/electron/releases/download/v44.5.0/electron-v44.5.0-win32-x64.zip), curl HEAD + redirects | GitHub302 to release-assets.githubusercontent.com; final TLS failed CRYPT_E_REVOCATION_OFFLINE; binary download not established |
| [mirror x64 binary](https://npmmirror.com/mirrors/electron/44.5.0/electron-v44.5.0-win32-x64.zip), curl HEAD + redirects | 302 to cdn.npmmirror.com, then200; Content-Length **157,966,405** (~150.65MiB); application/zip |
| [NuGet index](https://api.nuget.org/v3/index.json), curl HEAD + redirects | 302 to nuget.azure.cn/v3/index.json, then200; Content-Length9251 |

The mirror HEAD establishes advertised availability only, not sustained download speed, completeness, provenance or successful execution. Third-party mirror use should be approved and archive SHA256 compared with trusted official checksums before execution. No TLS checks disabled; no proxy, DNS, registry or network settings changed. Final shell exit0 does not negate intermediate curl failures: those were explicitly inspected and recorded.

## Native Android APK feasibility

Observed Java: Oracle **JRE** 1.8.0_481 at `C:/Program Files (x86)/Common Files/Oracle/Java/java8path/java.exe`. `C:/Program Files/Java` contains jre1.8.0_481 and latest. No JDK found. `adb.exe` exists at `D:/tool/platform-tools/adb.exe` but was **not run**; no device or phone content accessed.

Not on PATH: javac, gradle, sdkmanager, aapt2, apksigner, zipalign, d8. JAVA_HOME, ANDROID_HOME, ANDROID_SDK_ROOT, GRADLE_HOME unset. No .NET Android workloads.

Expected paths absent: local AppData Android/Sdk; Program Files Android/Android Studio/jbr; Program Files (x86)/Android; C:/Android, C:/AndroidSDK, C:/android-sdk; D:/Android, D:/Android/Sdk, D:/AndroidSDK, D:/android-sdk, D:/SDK; D:/tool/Android, android-sdk, AndroidSDK, gradle, jdk, jdk-17, Java, sdk; C:/Gradle and D:/Gradle. JDK vendor roots absent: Program Files Microsoft, OpenJDK, Eclipse Adoptium, JetBrains, Amazon Corretto, BellSoft, Zulu. Immediate directory-name filtering of Program Files, Program Files (x86), D:/tool, D:/ found no further Android/JDK/Gradle candidates. This is a targeted search, not proof no SDK exists anywhere on disk.

Official service probes via Invoke-WebRequest HEAD:

| Service | Outcome |
|---|---|
| [Android repository](https://dl.google.com/android/repository/repository2-1.xml) | 200, ~5915ms |
| [Gradle metadata](https://services.gradle.org/versions/current) | 200, ~792ms |
| [Adoptium metadata](https://api.adoptium.net/v3/info/available_releases) | 200, ~1070ms |
| [Android Gradle plugin metadata](https://dl.google.com/dl/android/maven2/com/android/tools/build/gradle/maven-metadata.xml) | 200, ~4291ms |

A genuine native local-file APK would require JDK17+, Android command-line SDK/platform/build-tools and Gradle/Android Gradle Plugin dependencies (exact compatible versions pinned together). These are substantial downloads, not a tiny workspace dependency proof. Metadata success does not establish binary download feasibility. No SDK licenses accepted or packages downloaded. Under the no-install/no-download probe phase, APK compilation cannot currently be demonstrated.

Recommended Android implementation after authorization: small Java/Kotlin Activity, native file list and Storage Access Framework directory selection using persisted URI permission; use ContentResolver/DocumentFile to access selected actual files. Avoid broad all-files permission and promises of unrestricted filesystem access under Android scoped storage. A native WebView shell can reuse UI but must bridge SAF securely and is still an APK, not a browser shortcut. Windows tray/hotkey/taskbar concepts do not transfer directly to Android. Build `assembleDebug` for test APK; deliver signed APK + SHA256 + install instructions, distinguishing debug and release signing. No device install/test without explicit authorization.

## Probe accounting and limitations

Background jobs started: `pwsh-17` (Electron/NuGet), `pwsh-19` (Android service HEAD). Both completed and all outputs were collected; no remaining jobs require killing. Two escaped-path run_code attempts and one metadata command construction failed before execution; they produced no filesystem/network effects. No separate build, dependency restore, runtime startup, disk write/read application behavior, full binary download, signing or device test has been verified. User pause took precedence over further investigation.
