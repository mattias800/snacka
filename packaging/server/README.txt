Snacka server
=============

Host a Snacka server for your friends: chat, voice, video and screen sharing.

1. Start it
   - Windows: double-click start.cmd
   - macOS:   double-click start.command (the first time: right-click > Open, since the
              download isn't signed yet)
   - Linux:   ./start.sh

   The window that opens is the server; closing it stops the server. The first time, it
   prints an invite code for your own account.

2. Create your account
   Open http://localhost:5117 in a browser, choose "Create an account", and use the invite
   code from the server window. The first account is the server's admin.

3. Let your friends in
   Open ports on your router to this computer:
     TCP 5117   the server (chat, and the web app your friends open)
     TCP 7881   voice and video
     UDP 7882   voice and video (better than TCP; forward both)

   Then open Snacka at your public address (http://<your-public-ip>:5117), go to
   Invite, and send the link to a friend. On the same network (LAN) the computer's local
   address works without forwarding.

Everything the server stores is in the "data" folder next to this file. To move or back up
the server, stop it and copy that folder.

Settings (optional, as environment variables):
  SNACKA_URLS   where to listen, default http://0.0.0.0:5117
  SNACKA_DATA   where to keep data, default ./data
