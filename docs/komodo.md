# Running the Inbound server with Komodo

This guide sets up the Inbound server (the web UI and the download engine, in Docker) as a Komodo Stack. It
takes about five minutes. `docker.md` explains every setting in more depth.

The button and field names below are Komodo 2's.

## Before you start

You need:

- Komodo with at least one Server connected (Komodo's Periphery agent running on the machine that will download).
- A folder on that machine for downloads, for example `/srv/downloads`, or a mounted disk such as
  `/mnt/disk1`. It must exist already.
- The numeric user and group that own that folder. On the machine, run:

  ```sh
  stat -c '%u %g' /srv/downloads
  ```

  The answer is usually `1000 1000`. The server runs as this user, so the files it saves belong to you.
- A free port on the machine for the web UI. These steps use `8080`; pick another if it is taken
  (`ss -ltn | grep :8080` prints nothing when the port is free).

## 1. Create the Stack

1. In Komodo, open **Stacks** and choose **New**.
2. Name it `inbound` and create it. Komodo opens the new Stack on its **Config** tab.
3. Under **Server**, pick the machine that will download.
4. Under **Choose Mode**, pick **UI Defined**. You write the compose file in Komodo, and Komodo writes it to the
   machine when you deploy.

## 2. Paste the compose file

Paste this into the compose file editor, then change the three marked lines:

```yaml
services:
  inbound:
    image: ghcr.io/kaan88/inbound:0.6.1
    container_name: inbound
    restart: unless-stopped
    init: true
    stop_grace_period: 30s
    ports:
      - "8080:8080"          # CHANGE the first number if port 8080 is taken: "8095:8080"
    environment:
      PUID: "1000"           # CHANGE to the user from "Before you start"
      PGID: "1000"           #        and its group
      TZ: "Europe/Istanbul"  # your time zone, for the scheduler and the log
    volumes:
      - ./data:/data
      - /srv/downloads:/downloads   # CHANGE the left side to your download folder
```

- `./data` is the server's state: the download list, settings, logs, the sign-in password hash and the key that
  opens saved logins. Komodo keeps a UI Defined stack's files in a folder named after the stack (`stacks/inbound`)
  under its Periphery root folder, so `./data` lands there. Back up that folder.
- The web UI can only save into folders under `/downloads`. The left side of that line decides which folder
  on the machine that is.

**More than one disk.** Mount each one under `/downloads`, and list them in `DOWNLOADMANAGER_ROOTS`, separated by
`:`. The first one listed is the default download folder. Change the `environment:` and `volumes:` blocks of the
file above to look like this (don't add second copies of them):

```yaml
    environment:
      # ...as above, plus:
      DOWNLOADMANAGER_ROOTS: "/downloads/disk1:/downloads/disk2"
    volumes:
      - ./data:/data
      - /mnt/disk1:/downloads/disk1
      - /mnt/disk2:/downloads/disk2
```

The server never changes the owner of a download folder that already holds files. If `PUID`/`PGID` can't write
to one, the **Log** tab says so. An empty folder it can't write is given to `PUID`/`PGID` (that folder only, not
recursively).

## 3. Save and deploy

1. Choose **Save**.
2. Choose **Deploy** and confirm. Komodo pulls the image and starts the container.
3. Open the **Services** tab. The `inbound` service's state is `running`. The image has its own health check:
   on the machine, `docker ps` shows `inbound` as `(healthy)` after about half a minute.

## 4. Set the password

1. Open `http://<the machine's address>:8080` (your port) in a browser.
2. Sign-in is on, and the first visit sets the password. Because your browser is not on the server itself, the
   page also asks for a **setup code**. The server writes it to its log once. In Komodo, open the Stack's
   **Log** tab and look for the line with `setup code`. Each start writes a new code, so use the newest one.
3. Enter the code and choose a long password.

If the page says "This server isn't known by the name ...", you opened it by a name rather than an IP address.
Add the name to `DOWNLOADMANAGER_HOSTS` (below).

**Optional: set the password from Komodo instead.**
1. In Komodo's **Settings**, open the **Variables** tab. Add `INBOUND_PASSWORD` and mark it secret, so Komodo
   hides it in its logs and from non-admin users.
2. In the Stack's **Environment** box, add:

   ```
   INBOUND_PASSWORD=[[INBOUND_PASSWORD]]
   ```

3. Add this to the compose file's `environment:`, then Save and Deploy:

   ```yaml
         DOWNLOADMANAGER_PASSWORD: ${INBOUND_PASSWORD}
   ```

With a password set this way, the web UI can't change it: change the variable, then Deploy. Note that `docker inspect` on the machine shows the
password. A password file is safer (see "Compose" in `docker.md`), but it needs a file on the machine.

## 5. Connect the browser extension (optional)

The Inbound browser extension can send downloads to this server.

1. In the web UI, open **Settings**, then **Sign-in and browsers**, and choose **Pair a browser**. The code works
   once, for ten minutes.
2. In the extension's Settings, go to **Inbound server**. Enter the server address (`http://<address>:8080`) and
   the code, then choose **Pair**.

## Updating

Each release has its own image tag, for example `0.6.1`, listed on the project's GitHub releases page.

- **By hand (recommended):** change the tag on the `image:` line, Save, then Deploy. Your list and settings in
  `./data` are kept. Downloads in progress pick up where they stopped.
- **Automatically:** use `image: ghcr.io/kaan88/inbound:latest`. In the Stack's Config, turn on **Poll for Updates**
  so Komodo shows when a newer image is out. **Auto Update** redeploys on its own when Komodo finds one. **Pull
  Images** fetches the newest image without restarting.

## Using a name or a reverse proxy

- **A name instead of an IP** (such as `downloads.home.arpa`): add
  `DOWNLOADMANAGER_HOSTS: "downloads.home.arpa"` to `environment:`. Names that aren't listed are refused, against
  DNS rebinding.
- **HTTPS through a reverse proxy** (Nginx Proxy Manager, Caddy, Traefik): also set `DOWNLOADMANAGER_TRUST_PROXY` to
  the proxy's address. The proxy must pass WebSockets through, because the web UI needs them. "Reverse proxy and
  HTTPS" in `docker.md` has the details and examples.

## If something goes wrong

| What you see | What to do |
| --- | --- |
| Deploy fails with "port is already allocated" | Another container uses that port. Change the first number on the `ports:` line. |
| The **Log** tab warns that the user can't write to `/downloads` | Set `PUID`/`PGID` to the folder's owner (`stat -c '%u %g' <folder>`), Save, Deploy. |
| The container stops at once, and the log mentions `DOWNLOADMANAGER_AUTH` | Sign-in can only be turned off together with `DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1`. Leave sign-in on unless the network is yours alone. |
| After restoring a backup, the server won't start, and the log says the secret key file is missing or the saved passwords were sealed with another key | Restore `secrets.key` with the rest of `./data`. See "Backup" in `docker.md`. |
| Lost the setup code | Choose **Restart**. Each start writes a new code to the log until a password is set. |
