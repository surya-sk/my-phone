# My Phone (MessagesClient) handoff

The app is shown to users as **My Phone**. Internal names (Messages.exe, the `NativeMessages.App` package identity, `%LOCALAPPDATA%\NativeMessages`, `%LOCALAPPDATA%\Programs\Messages`) are unchanged so existing installs and pairings keep working.

This is a native WinUI 3 Windows client for Google Messages. The user wants a proper messaging app for **RCS and regular SMS**, paired to their Android phone. The Windows PC has no SIM. The UI must remain native XAML: **do not replace it with a WebView**. The app should start and stop its local backend with the app; the user does not want to operate a separate always-on server. The user plans to have Claude improve the current UI.

## Where things are

| Path | Purpose |
| --- | --- |
| `MessagesClient.sln` | Visual Studio solution; x64, unpackaged WinUI app. |
| `Messages/MainWindow.xaml` | Entire native messaging UI: pairing, inbox, transcript templates, composer, media viewer. |
| `Messages/MainWindow.xaml.cs` | Pairing, inbox filters/search, thread loading and paging, optimistic sends, reactions, replies, attachments, notification detection, unread badge. |
| `Messages/Themes/Metro.xaml` | Windows 8.1 "Metro" styling over Mica: square corners, light display type, circle app-bar buttons, theme brushes. |
| `Messages/Models/ViewModels.cs` | Bindable rows for conversations, messages (media, reply quote, reactions, link preview), attachments. |
| `Messages/Controls/Helpers.cs` | Template selectors and the link-detecting `Linkify.Text` attached property. |
| `Messages/App.xaml.cs` | Windows App SDK notification registration, toasts with inline reply / mark-as-read, activation handling. |
| `Messages/Program.cs` | Custom `Main`: single instance (second launches and toast activations go to the running window). |
| `Messages/Services/OpenMessageClient.cs` | Local HTTP API client, SSE listener, JSON models, delivery/read labels. |
| `Messages/Services/MediaService.cs` | Authenticated avatar, attachment and link-preview loading with an in-memory cache. |
| `Messages/Services/BackendHost.cs` | Starts/stops the Go backend and holds local paths/port. `ChildProcessJob` kills it if the app crashes. |
| `Messages/Services/TaskbarBadge.cs` | Unread count as a taskbar overlay icon (badge notifications need package identity). |
| `Messages/Services/Log.cs` | Diagnostic log at `%LOCALAPPDATA%\NativeMessages\app.log`; never log message text, numbers or cookies. |
| `Messages/README.md` | End-user build, pairing, and usage instructions. |
| `Messages.Backend/` | Go implementation of the unofficial Google Messages linked-device protocol; this directory is its **own Git repository**. |
| `Messages.Tools/go/bin/go.exe` | Local Go toolchain. |

`MessagesClient` itself is not a Git repository. Do not assume a clean backend worktree or overwrite its changes. At handoff, `Messages.Backend` has local edits in `.gitignore`, `cmd/backup.go`, `cmd/serve.go`, `internal/db/tapback.go`, `internal/db/tapback_test.go`, and `internal/web/api.go`, plus new Windows/Unix backup and umask files. Check its status before modifying it. Under the Codex sandbox, Git may require `git -c safe.directory=C:/Users/surya/repo/MessagesClient/Messages.Backend ...` for read-only status/diff commands.

## Runtime and build

- The WinUI app is .NET 8, Windows App SDK 2.2.0, `WindowsPackageType=None`, x64. Use Visual Studio's **Unpackaged** launch profile.
- The Go backend is built from `Messages.Backend` (Go 1.25) as `openmessage.exe`. The WinUI project copies that executable into `Messages/bin/x64/Release/net8.0-windows10.0.26100.0/Backend/`.
- From the project root, build with `dotnet build MessagesClient.sln --no-restore -c Release -p:Platform=x64`. If dependencies are not restored yet, restore the solution first.
- To rebuild the backend from `Messages.Backend`, run `../Messages.Tools/go/bin/go.exe build -buildvcs=false -o openmessage.exe .` (adjust path separators for the shell). Then rebuild the WinUI solution so the new executable is copied.
- Run `Messages/bin/x64/Release/net8.0-windows10.0.26100.0/Messages.exe` or F5 in Visual Studio. The last Release build on 2026-10-12 succeeded with zero warnings and zero errors. This confirms compilation, **not** live phone behavior.
- `BackendHost` launches `openmessage.exe serve` on `127.0.0.1:17077` when the app opens a paired session and kills its child process when the window closes. It launches `openmessage.exe pair --google-stdin` for pairing. Session and bearer token live in `%LOCALAPPDATA%\NativeMessages` (`session.json` and `control.token`). Do not commit or print those files.

## Pairing and data flow

The app uses **Google account device pairing**, with the same account selected in Google Messages on the phone. It does not use QR pairing. The user chooses Firefox, Chrome, or Edge in the pairing screen. The app opens a dedicated persistent browser profile; the user copies the `messages.google.com/web/config` request as cURL from that browser's Network panel and pastes the entire command into the app. The local Go pairing process extracts the cookies, displays the emoji challenge, and the user taps the matching emoji on the phone. The backend can refresh expired cookies from the selected profile on Windows while reconnecting. Pairings made through the old private window need one new pairing. `Messages/README.md` has the exact user-facing steps. Treat the pasted cURL command as sensitive credentials; never put it in logs, tests, or issue text.

Once paired, the phone remains the RCS/SMS endpoint. The desktop calls the loopback API with a bearer token:

- `GET /api/status`, `GET /api/conversations?limit=500`, and `GET /api/conversations/{id}/messages?limit=100[&before=&before_id=]` provide the inbox and paged transcript.
- `GET /api/events` streams message, conversation, status, and typing events. `MainWindow` also refreshes every 15 seconds.
- Sending uses `POST /api/v1/outbox/messages` and `/api/v1/outbox/media` (multipart `file`), falling back to `/api/send` and `/api/send-media` only on 503. Each send has its own idempotency key. Replies and reactions target `source_id` when it is present (the Google ID in v2 mode), otherwise `MessageID`.
- Reactions use `POST /api/react` with `add`/`remove`/`switch`. Reaction `actors` are Google participant IDs; the participant with `is_me` is the user. Other endpoints used: `/api/mark-read`, `/api/conversations/{id}/tab|favorite|notification-mode`, `/api/avatar`, `/api/media/{id}`, `/api/link-preview(-image)`, `/api/search/messages`, `/api/contacts`, and `/api/new-conversation`.
- The backend persists Google message status strings. `ChatMessage.StatusDisplay` maps reported outgoing status to `✓ Sent`, `✓✓ Delivered`, or `✓✓ Read`; SMS cannot provide RCS read receipts. The client does not invent a read state.

## Current implementation (2026-10-13 redesign)

The UI follows the Windows 8.1 Metro language adapted to Windows 11: Mica backdrop and extended title bar, lowercase light headers ("messages", "all · unread · archived" pivot), square tiles in the Win8 start-screen palette, circle app-bar buttons, Windows Phone-style square bubbles with corner tails (outgoing = accent), and `ControlCornerRadius`/`OverlayCornerRadius` set to 0 globally.

- **Inbox:** contact photos (`/api/avatar`, with initials or glyph tiles as fallback), unread badges with bold rows, pinned threads first, mute and pin icons, an "unread N" pivot count, and an archived pivot. The right-click menu offers mark read, pin, mute, archive/unarchive, and copy number. Archive uses the backend's local `tab` (`/api/conversations/{id}/tab`); it is not synced to the phone. A new incoming message unarchives a thread, as in Google Messages. Archive actions show an undo toast.
- **Search:** filters people as you type, then searches full history through `/api/search/messages`. Selecting a hit pages back through history and highlights the message.
- **Transcript:** day headers, sender grouping, group-chat sender names and photos, reply quotes (click to jump), reaction chips (the user's own is outlined; click toggles), link detection with previews (`/api/link-preview`), inline images and GIFs, video and voice-message tiles, file cards, delivery status, failed-send state with retry, and infinite scroll back through history (`before`/`before_id`).
- **Message menu** (right-click, Shift+F10, or press-and-hold): a row of six reactions (add, remove, or switch), Reply, Copy text, Save attachment, Try again/Delete for failed sends, and Details. There is still no visible per-message reaction button.
- **Composer:** Enter sends and Shift+Enter adds a line. RCS and SMS placeholders differ, and SMS shows a segment counter. Attach files through the picker, drag and drop, or paste an image. Sends are optimistic: a pending bubble is handed to the phone's echo without flicker, and replies send `reply_to_id`. Drafts are kept per thread for the session.
- **Media viewer:** full-window overlay for photos and video, with save. Voice messages play inline; AMR may not play on Windows, and the app says so. HEIC needs the Windows HEIF extension.
- **Notifications:** toasts show the contact photo, an inline photo when there is one, an inline **reply** box, and **Mark as read**. They are grouped per thread and cleared when the thread is opened. Muted threads stay silent, and there is a global toggle in the ••• menu. The taskbar flashes while the window is in the background. The unread total appears in the window title and as a taskbar overlay icon.
- **Connection state:** the title bar and an inbox banner show the backend's `google.auth_expired`/`needs_repair`/`needs_pairing` as "Google sign-in expired", with a pair-again link.
- **Layout:** two panes, collapsing to a single pane with a back button below 760 px.

### Verified on 2026-10-13 against the user's real local data (not sending anything)

These were tested with the app running: inbox, avatars, unread badges, pivots, archive → archived pivot → unarchive (state restored afterwards), full-history search and jump, a group chat, an RCS thread with read receipts, reaction chips and the menu, the reply bar, link previews, light and dark theme, narrow layout, a real toast (photo, reply box, mark-as-read), and clicking a toast to open its thread. **The Google session was expired at the time** (`auth_expired`), so media downloads failed and nothing was sent; **sending, inline toast reply, live incoming events, typing, and image/video rendering from the phone still need a check after re-pairing.** The taskbar overlay call succeeds, but it was not seen on screen because the taskbar was hidden.

### Package identity (native taskbar badge)

Windows only shows its taskbar badge for apps with package identity. The app stays unpackaged (`WindowsPackageType=None`) but carries a **sparse identity package**:
- `Messages/Identity/AppxManifest.xml` declares `NativeMessages.App`, the Windows App Runtime 2 dependency, and toast activation (COM CLSID `7E4BA66E-…`).
- `Messages/app.manifest` has the matching `<msix>` element.
- On first start, `Services/PackageIdentity.cs` registers the manifest with `ExternalLocationUri` set to the exe folder, then relaunches once. This needs **Developer Mode**. Without it the app runs normally, with no badge.
- `WindowsAppSDKBootstrapAutoInitializeOptions_OnPackageIdentity_NoOp=true` is required, because the bootstrapper fails with `0x80070032` in a process that has identity.
- To change identity details, bump the manifest `Version`; a newer version re-registers on the next start. To change logos, regenerate `Identity/resources.pri` with `makepri new /pr Identity /mn Identity\AppxManifest.xml /cf <priconfig> /of Identity\resources.pri`. The build copies it to the exe folder, where Explorer reads it; without it the taskbar icon is blank.
- Registration points at one output folder. Switching between Debug and Release re-registers to the folder you launched.
- Messages also appears in Start search as an installed app. Remove it with `Get-AppxPackage NativeMessages.App | Remove-AppxPackage`.

### RCS/SMS label

Google's conversation `type` (SMS=1, RCS=2) is now stored as `display_protocol` in the backend: `internal/client/events.go` and `internal/app/backfill.go` via `db.DisplayProtocolFromConversationType`. Message tombstones may only relabel a thread when they are at least as new as its last message, in `internal/db/messages.go`. Before this, history backfill replayed an old "created as SMS" event after a newer "switched to RCS" one. The client also resolves the protocol from loaded history (`Services/Protocol.cs`) for threads Google hasn't typed yet, and shows tombstones as centered system lines.

### One active device at a time

Google Messages delivers live messages to **one** linked session at a time, even when each PC was paired separately. When another session takes over, Google sends a `BROWSER_INACTIVE` user alert; the long-poll stays up, so the backend still reports `connected`.
- The backend handles the alert (`internal/client/events.go` → `App.BrowserInactive` in `internal/app/google_active_session.go`) and reports `google.session_inactive` in `/api/status`.
- Timeout and inactivity deactivations are reclaimed automatically. A takeover by another device is not, so two PCs don't keep stealing from each other.
- `POST /api/google/activate` calls libgm `SetActiveSession` (the web client's "Use here"), paced to once a minute, and then runs a recent reconcile.
- The app shows "active on another device" with a **use here** banner, activates when its window is focused (at most every 2 minutes), and Refresh also activates before requesting a phone sync (`POST /api/backfill {since}`).

### Calls (2026-10-16)

Calls use Bluetooth only. There is no Android companion app.
- **Hands-free role** (`Services/PhoneCalls.cs`). The app takes the phone's role as a Bluetooth hands-free device. It calls `PhoneLineTransportDevice.RequestAccessAsync`, then `RegisterApp`, then `ConnectAsync`.
  - Windows then sends call audio through the PC's speakers and microphone.
  - The phone line comes from a `PhoneLineWatcher`, matched by `TransportDeviceId`. Calls in progress are `PhoneCall` objects: answer, decline, end, mute, hold, keypad tones, and moving audio between PC and phone.
- **Limited Access Feature.** `phonelinetransportdevice_v1` is unlocked with the community feature key that Sefirah also uses (`LimitedAccess` in the same file). The token is derived at run time from the package family name.
- **Manifest** (`Identity/AppxManifest.xml`). It declares `phoneCall`, `phoneLineTransportManagement`, `phoneCallSystem`, and `uap13:windows.phoneCallActivation`.
- **`DeniedBySystem` means another app holds the role for that phone.** Phone Link holding it is what happened here. Remove the phone in Bluetooth settings, pair it again, then pick it in the app. It was not a missing token or capability.
- **Recent calls and contacts** (`Services/Phonebook.cs`). They come over Bluetooth PBAP: a minimal OBEX client on RFCOMM that pulls `telecom/cch.vcf` and `telecom/pb.vcf` (vCard 2.1, with a property mask that includes PHOTO).
  - Android asks on the phone once per PC and gives up after about 30 seconds.
  - Results are cached in `%LOCALAPPDATA%\NativeMessages\phonebook.json`.
- **Notifications.** Incoming calls get a ringing toast (`AppNotificationScenario.IncomingCall`) with Answer, Decline and Message, removed when ringing stops. Missed calls get a toast with Call back and Message.
- **Screen.** `Views/CallsView.xaml(.cs)` holds recent, missed and contacts, the dial pad, the in-call screen, and setup.

### UI conventions

- **Sections.** Messages and calls are Metro pivot headers. Their filters are a second, smaller pivot row.
- **Commands.** Each list's commands are in the Metro app bar at the bottom of the pane (`MetroAppBarButtonStyle`), not in the header.
- **Swipes** (`Controls/SwipeGesture.cs`):
  - On the headers and filters, a touch or pen swipe moves through filters and on into the other section. A two-finger touchpad swipe works anywhere on the list pane.
  - Conversation rows: swipe right marks read, swipe left archives. Call rows: swipe right calls, swipe left texts.
  - In the one-column layout, swiping right on a conversation or contact goes back.
- **Icon.** The icon set is generated, phone-shaped. The generator script is not in the repo.

### Distribution

- Run `powershell -ExecutionPolicy Bypass -File Messages\Packaging\build-installer.ps1`. It writes `dist\MyPhone.zip`, versioned from `Identity\AppxManifest.xml`.
- The zip has `Install.ps1`, `Uninstall.ps1`, `README.txt` for end users, the signed identity package, and the Windows App Runtime.
- The signing certificate is self-signed and stays in the builder's CurrentUser\My store. Only the `.cer` ships.

### Known gaps

- There is no Windows SIM path and no independent cloud push service. The phone must be online, and SMS also needs its cellular service. Notifications only arrive while the app is running.
- Archive, pin and mute are local to this PC's backend store. Mark-read clears the local unread count.
- The backend exposes no endpoint for sending typing indicators or for deleting messages.

## Suggested next verification pass

1. Pair again (the session was expired on 2026-10-13), then confirm the title bar shows "phone connected" and the banner disappears.
2. Receive one RCS and one SMS while another thread is open. Confirm each appears once, gets one toast with the right photo, and that **replying from the toast** sends.
3. Send text, a photo, and a reply. Watch the pending bubble turn into the phone's copy without duplicating, and watch the status move from sent to delivered to read.
4. Open threads with JPEG, GIF, HEIC, video, and AMR attachments, and check that inline rendering and the viewer work now that media can download.
5. Add, switch, and remove a reaction, then restart and confirm it persists.

Debug builds share `%LOCALAPPDATA%\NativeMessages` with Release. The app is single-instance per user, so close one before starting the other.
