# Processing in its own window — design

Owner request, 2026-09-26: "pressing Start processing should open in a window, not make the main screen disappear", and "when the window opens, it auto fits to the actual pdf size".

## Today

OrdoSort is one window, `MainWindow`, that changes shape:

- Ready: a compact dashboard parked in the screen's corner (470 px wide).
- Start processing: the same window grows into the filing screen (PDF viewer, routes, naming box) and the dashboard content disappears.
- Done, then back: it shrinks back to the dashboard.

One view model, `ShellViewModel`, drives all three screens (`Screen.Ready`, `Processing`, `Done`).

## Decisions

| Question | Owner's answer |
|---|---|
| Structure | Two windows, one brain: the dashboard window and a new Processing window, both bound to the same `ShellViewModel` |
| Dashboard while filing | Stays live (counts, alerts, monitored folders keep updating); its Start button reads "Processing… (show)" and brings the Processing window to the front; no second session can start |
| X on the Processing window mid-session | Stops the session, exactly as Stop / Esc does today |
| Last document filed | The Processing window shows the Done summary (with Undo); Back to dashboard or X closes it |
| Size when it opens | The whole first page fits: the viewer takes the page's shape at the tallest height the screen allows |

## Design

### Windows

| Window | Holds | Shape |
|---|---|---|
| `MainWindow` (dashboard) | Header (menus, Tools, folder tiles), Ready screen | Always the compact dashboard; never reshapes |
| `ProcessingWindow` (new) | PDF viewer, splitter, Processing screen, Done screen | Opens per session, fitted to the first page |

- The Processing window is created once, at start-up, and hidden between sessions. It owns the one WebView2 viewer that `ShellViewModel` is given, so the view model's constructor does not change.
- The viewer starts up when the Processing window is first shown. Starting a session waits for it before loading the first document.
- `MainWindow` loses the compact/normal mode switch (`EnterCompact`, `EnterNormal`, the viewer columns); the dashboard is always compact.

### Session flow

| Step | Dashboard | Processing window |
|---|---|---|
| Start processing | Stays; button becomes "Processing… (show)" | Opens, fitted to the first page, and takes focus |
| Filing | Live: counts, alerts, tiles keep updating | Viewer, routes, naming box as today |
| Press "Processing… (show)" | — | Comes to the front |
| Stop / Esc / X mid-session | Back to Ready | Hides; filed documents stay filed, the rest stay in the inbox, Undo stays available |
| Last document filed | — | Shows Done with Undo |
| Back to dashboard / X on Done | Ready | Hides |
| X on the dashboard mid-session | Stops the session first (as today) and stays open | Hides |

### Fitting the window to the page

When a session starts, the Processing window is sized once from the first page's shape:

- Height: the full height of the work area of the monitor the dashboard is on.
- Width: the viewer's width is that height (less the window's own chrome) times the page's width-to-height ratio, plus the side panel and splitter.
- If that is wider than the work area, the height shrinks until it fits.
- It opens centred on that monitor. The user can resize it; the next session fits again.
- A first file that won't open, or has no page size, leaves the window at its last size (as today, "a non-event").

### Keyboard and input

| Input | Moves to |
|---|---|
| Route hotkeys (`RebindRouteHotkeys`) | Processing window |
| Esc (Stop), Enter, naming-box suggestions | Processing window (they already live in `ProcessingView`) |
| Viewer zoom / pan (`ViewerInputEnhancer` pan zone) | Processing window |
| Ctrl+, (Settings), menus, Tools | Stay on the dashboard |

### Unchanged

- The filing rules: renaming, moving, the audit log, Undo, the naming completer.
- The dashboard's alerts, taskbar flash and badge.
- Settings, Tools and every tool window.
- The app's exit path (`OnExit`, `FinishClosingWhenIdle`).

## Testing

| Behaviour | Test |
|---|---|
| Start opens the Processing window; the dashboard stays visible and shows Ready content | new window test |
| The dashboard's button reads "Processing… (show)" during a session and brings the Processing window forward | new |
| No second session starts while one runs | new |
| X on the Processing window mid-session stops the session | new |
| Done, then X / Back to dashboard, returns to Ready and hides the window | new |
| The fit: page shape × tallest height, shrunk to the work area width, centred on the dashboard's monitor | new pure test on the fit maths (`FitMath`) |
| Route hotkeys reach the Processing window | new |
| Existing session suites (processing, done, header layout, E2E scenarios, screenshots) | re-pointed at the Processing window; setup may change, assertions may not weaken |

- [ ] Live check on a scratch copy with the dev inbox: Start, file two documents, Stop; Start, finish, Done, close.
