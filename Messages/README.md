# Messages for Windows

Native WinUI 3 client for Google Messages conversations. Its UI uses XAML controls and does not embed a browser. A local Go process implements the Google Messages linked-device protocol; your Android phone remains the RCS endpoint.

## Build and run

1. Build `Messages.Backend/openmessage.exe` from the included source with Go 1.25 or newer: `go build -o openmessage.exe .` from `Messages.Backend`.
2. Open `MessagesClient.sln` at the project root in Visual Studio. Select the `Unpackaged` launch profile and press F5. The solution targets x64. You can also run `dotnet build MessagesClient.sln` from the project root.
3. Launch `Messages.exe`. It starts the local backend on `127.0.0.1:17077` after pairing.

The backend executable is copied into the app output by the WinUI project. Session data is saved under `%LOCALAPPDATA%\NativeMessages`, outside the source tree.

## Pair your phone

The box in the app accepts the **entire cURL command for one browser request**. You do not type a command into a terminal, and you do not paste the web page's text or address.

1. In the Windows app, choose **Firefox**, **Chrome**, or **Edge**, then click **Open Google sign-in**. Sign in using the Google account selected under Google Messages → account menu → Device pairing on your Android phone. The app opens a dedicated, persistent profile in your chosen browser. Stay signed in there so the app can renew its connection after a restart.
2. When the browser reaches `https://messages.google.com/web/config`, press **F12** and select the **Network** tab in developer tools.
3. Press **Ctrl+R** to reload the page. In the Network request list, find the row named **config** with a URL containing `messages.google.com/web/config`. If you cannot see it, filter for `web/config` or reload once more.
4. **Right-click that one request row.** In Edge or Chrome choose **Copy → Copy as cURL (bash)**. In Firefox choose **Copy → Copy as cURL**. On Windows, do not choose the `(cmd)` option.
5. Return to the Windows app, click the **Paste the copied cURL command here** box, press **Ctrl+V**, and click **Pair phone**. The pasted text should begin with `curl` and contain a `Cookie` header.
6. When an emoji appears in the Windows app, open Google Messages on the phone → account menu → **Device pairing** and tap the matching emoji.

The cURL command contains sensitive account cookies. Paste it only into the local app, never into a terminal, chat, or issue report. The app clears the text box after submitting it to the local pairing process. The chosen browser profile stays under `%LOCALAPPDATA%\NativeMessages\BrowserAuth` and is used to renew account cookies automatically. Existing pairings made through a private window need one new pairing with this profile. If Google unlinks the device, use **Pair again**. [Account pairing background](https://github.com/mautrix/docs/blob/master/bridges/go/gmessages/authentication.md)

## Using Messages

- **Inbox:** switch between **all**, **unread**, and **archived** under the header. Right-click a conversation to pin, mute, mark as read, or archive it. Archived threads come back when someone writes.
- **Search** (Ctrl+F) finds people as you type and searches your whole message history. Select a result to jump to that message.
- **New message:** Ctrl+N or the **+** button. Start typing a name or number.
- **Reactions and replies:** right-click a message, or press and hold it, to react, reply, copy, or save its attachment. Click a reaction under a message to add or remove your own.
- **Attachments:** use the paperclip, drag files onto the conversation, or paste an image. Click a photo or video to view it full size and save it.
- **Composer:** **Enter** sends and **Shift+Enter** adds a new line. SMS threads show a character counter.
- **Notifications:** reply or mark as read straight from the notification. Turn all notifications off from the **•••** menu, or mute a single conversation. The unread total shows in the title bar and on the taskbar button.
- When Google account cookies expire, the app tries to renew them from the chosen browser profile and reconnect. If that fails, the inbox shows a banner; sign in to the chosen browser profile and choose **pair again**.

Notes:
- The phone needs internet access, and an active cellular plan for SMS. Google Messages chooses RCS or SMS for each recipient; Windows does not need a SIM.
- Delivery and read status appear only when Google Messages reports them. SMS has no read receipts or typing indicators.
- Notifications arrive only while Messages is open.
- Some phone formats (AMR voice notes, HEIC photos without the HEIF extension) may not play or display on Windows; save them instead.
- The Go protocol implementation is unofficial. Google can change or revoke its linked-device behavior.
