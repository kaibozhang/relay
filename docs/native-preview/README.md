# Native image evidence

These images come from the running native WPF application, not mockups. The files are copied from the successful 19-scenario UIAutomation run at 2026-09-30 16:02 local time.

- [workspace.png](<workspace.png>): initial empty application.
- [capture.png](<capture.png>): real capture form using synthetic test data.
- [strip.png](<strip.png>): real continuation strip.
- [progress.png](<progress.png>): progress dialog opened from the strip while dashboard remained minimized.

Method: opt-in app-owned RenderTargetBitmap of each window's actual content visual tree, composited with the actual window background. It excludes OS title chrome and does not capture other apps. Related raw run also contains independently nonblank PrintWindow captures and UIAutomation rectangles.

[Raw run report](<../../artifacts/native-tests/20260930-080200-ffa83880/report.json>) records 32 nonblank captures and the verification method. Pixel statistics establish nonblank output, not human aesthetic review. The session model cannot inspect image inputs; no manual pixel-level review is claimed.
