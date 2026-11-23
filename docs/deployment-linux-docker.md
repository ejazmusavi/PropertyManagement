# Deploy to a Linux server with Docker

Result: HTTPS site at `https://<WEB_DOMAIN>` (MVC) and `https://<API_DOMAIN>` (API), with MySQL, uploaded documents and login keys on persistent Docker volumes. Only ports 80/443 are open; MySQL is never exposed.

Files used: `docker-compose.prod.yml`, `Caddyfile`, `.env.example`, `Dockerfile.web`, `Dockerfile.api`.
(`docker-compose.yml` is for local development only: Development mode, open MySQL port, Swagger on.)

## 1. Prerequisites
- A Linux server (Ubuntu 22.04/24.04 assumed), 2 GB RAM or more, with SSH access.
- Two DNS **A records** (e.g. `erp.example.com`, `api.example.com`) pointing to the server's public IP. Caddy needs them to issue HTTPS certificates.
  No domain yet? Use free wildcard DNS: for IP `1.2.3.4` set `WEB_DOMAIN=1-2-3-4.nip.io` and `API_DOMAIN=api.1-2-3-4.nip.io`. Real certificates are issued the same way.
- **Oracle Cloud**: in the subnet's Security List add ingress rules from `0.0.0.0/0` for TCP 22, 80 and 443 (the default 80/443 rules only allow `10.0.0.0/16`). The VCN also needs an Internet Gateway and a `0.0.0.0/0` route to it, and the instance a public IP. Default login user is `ubuntu`.

## 2. Prepare the server
```bash
# install Docker + compose plugin
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER      # log out and back in afterwards

# firewall: SSH + web only
sudo ufw allow OpenSSH && sudo ufw allow 80 && sudo ufw allow 443 && sudo ufw enable
```
Oracle Cloud Ubuntu images have no `ufw`; they use `iptables` rules that reject everything except SSH. Open the web ports and save them instead:
```bash
sudo iptables -I INPUT 5 -p tcp -m multiport --dports 80,443 -m state --state NEW -j ACCEPT
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y iptables-persistent
sudo netfilter-persistent save
```

## 3. Get the code
```bash
git clone <your-repo-url> realestate
cd realestate
git checkout main
```

## 4. Set the secrets (on the server only)
Secrets live in a `.env` file on the server. It is git-ignored; never commit it or copy it into chat/tickets.
```bash
cp .env.example .env
chmod 600 .env                      # only your user can read it

# generate strong values and write them into .env
sed -i "s|^MYSQL_ROOT_PASSWORD=.*|MYSQL_ROOT_PASSWORD=$(openssl rand -hex 24)|" .env
sed -i "s|^MYSQL_APP_PASSWORD=.*|MYSQL_APP_PASSWORD=$(openssl rand -hex 24)|"   .env
sed -i "s|^JWT_SIGNING_KEY=.*|JWT_SIGNING_KEY=$(openssl rand -base64 48 | tr -d '\n/+=')|" .env

nano .env                           # set WEB_DOMAIN and API_DOMAIN
```
Notes:
- Passwords are hex so they are safe inside the MySQL connection string. If you choose your own, avoid `;` `"` `$` and spaces.
- **Back up `.env` in a password manager.** Losing `MYSQL_*` blocks DB access; changing `JWT_SIGNING_KEY` logs everyone out (mobile app tokens become invalid).
- The MySQL passwords are only applied when the `mysql-data` volume is first created. Changing them later in `.env` does not change the existing DB user; change the password inside MySQL first (`ALTER USER`), then update `.env`.
- Optional features (e-mail, payment gateway, background jobs) are also secrets/config: add them to the `environment:` of the `api`/`web` services in `docker-compose.prod.yml` using `__` for nesting, with the values in `.env`. Example: `Notifications__Email__Password: ${SMTP_PASSWORD}` plus `SMTP_PASSWORD=...` in `.env`. Keys are listed in `docs/development-setup.md`.

## 5. Build and start
```bash
docker compose -f docker-compose.prod.yml up -d --build
docker compose -f docker-compose.prod.yml ps          # all services "running"/"healthy"
docker compose -f docker-compose.prod.yml logs -f api web caddy
```
Database migrations are applied automatically on startup (`Database__MigrateOnStartup`). The first start takes a few minutes (image build + certificate issuance).

Check: `curl https://<API_DOMAIN>/api/v1/health` returns 200.

## 6. Create the first administrator (once)
The password is read from your shell, not stored in any file:
```bash
read -s -p "Admin password: " REALESTATE_ADMIN_PASSWORD; echo
export REALESTATE_ADMIN_PASSWORD
# no sudo: Ubuntu 26.04's sudo drops environment variables; your user is in the docker group
docker compose -f docker-compose.prod.yml run --rm -e REALESTATE_ADMIN_PASSWORD web \
  create-admin --email you@example.com --name "Your Name" --company "Your Company"
unset REALESTATE_ADMIN_PASSWORD
```
Then sign in at `https://<WEB_DOMAIN>`.

## 7. Update to a new version (automatic: GitHub Actions)
`.github/workflows/deploy.yaml` deploys every push to `main` (or run it manually from the Actions tab). It uploads the code, then runs `deploy/deploy.sh` on the server, which:
1. takes a **pre-deploy backup** of the database and documents,
2. rebuilds and restarts the containers (volumes are kept, `.env` is never overwritten, migrations apply automatically),
3. waits for the API to be healthy and smoke-tests the web login page and `/api/v1/health` over HTTPS.

One-time setup: GitHub repo > Settings > Secrets and variables > Actions > New repository secret:

| Secret | Value |
|---|---|
| `SSH_HOST` | server IP or host name |
| `SSH_USER` | `ubuntu` |
| `SSH_PRIVATE_KEY` | full contents of the private key file |
| `SSH_KNOWN_HOSTS` | output of `ssh-keyscan -t ed25519 <server-ip>` (pins the server identity) |

Prefer a dedicated deploy key: create it with `ssh-keygen -t ed25519 -f deploy_key -N ""` and append `deploy_key.pub` to `~/.ssh/authorized_keys` on the server.

Manual deploy from your PC is also possible: copy the code to `~/realestate` (never overwrite `.env`) and run `~/realestate/deploy/deploy.sh`.

## 8. Backups
Daily backups run from `cron` at 02:00 server time (UTC) via `deploy/backup.sh`; the deploy script also runs it before every deploy. Files go to `~/backups` (`db-*.sql.gz`, `docs-*.tar.gz`, mode 600) and are deleted after 14 days. Log: `~/backups/backup.log`.

Install the cron job (once; already done on the current server):
```bash
chmod +x ~/realestate/deploy/*.sh
( crontab -l 2>/dev/null | grep -v deploy/backup.sh; echo "0 2 * * * $HOME/realestate/deploy/backup.sh daily >> $HOME/backups/backup.log 2>&1" ) | crontab -
```
Run one now: `~/realestate/deploy/backup.sh`. Backups do **not** contain `.env` (secrets): keep that in a password manager. Also copy `~/backups` off the server regularly (e.g. `scp` or object storage); backups on the same disk do not survive losing the server.

### Restore
```bash
cd ~/realestate
# database (replaces current data!)
gzip -dc ~/backups/db-daily-<time>.sql.gz | docker compose -f docker-compose.prod.yml exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot realestate'
# documents
docker run --rm --volumes-from "$(docker compose -f docker-compose.prod.yml ps -q api)" -v ~/backups:/backup:ro alpine \
  sh -c 'cd /var/realestate/storage && tar xzf /backup/docs-daily-<time>.tar.gz'
```

## Troubleshooting
| Symptom | Check |
|---|---|
| Caddy can't get a certificate | DNS A records point to this server; ports 80/443 open; `docker compose ... logs caddy` |
| `set in .env` error on startup | A required variable in `.env` is empty |
| `api` unhealthy | `docker compose ... logs api` (usually a wrong DB password or an invalid/short JWT key) |
| Login loops / redirects | Access the site via `https://` only; keep `ASPNETCORE_FORWARDEDHEADERS_ENABLED` set |
| Reset everything (DESTROYS DATA) | `docker compose -f docker-compose.prod.yml down -v` |
