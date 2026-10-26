# Team Credential Vault (event API)

## What it is
The organizers give out service keys (cloud, AI model APIs, etc.) through a vault at
`https://cdao.hackathon.maximus.com`. Team **Data Crucible** gets one **team token** that opens our section.
The vault doesn't build anything. It's just how we get the keys for the services we build with.

**Status:** team token not issued yet.

Team ID: `7e3a3e89-0e20-42b5-b646-8a857cd51012`
Base URL: `https://cdao.hackathon.maximus.com/api/teams/<team ID>/credentials/`
Auth: header `Authorization: Bearer <team token>`

## Security rules (from the event, mandatory)
- **Never put the token in source code.** Keep it in the API console (DPAPI-encrypted), an env var, or CI secrets.
- **Every reveal is audit-logged** with IP, user agent and timestamp. Reveal only what you need.
- The token is scoped to our team only and **can be revoked at any time**.
- Values are encrypted at rest, isolated per event.
- In GitHub Actions, run `echo "::add-mask::$SECRET"` so secrets don't leak into logs.

## Endpoints
| Method | Path (after `/credentials`) | Does | Secret returned? |
|---|---|---|---|
| GET | `/` | List all keys (name, env var name, expiry, access count) | No |
| GET | `/{uuid}/` | One key's details | No |
| GET | `/by-name/{name}/` | One key's details, looked up by display name (409 if the name isn't unique) | No |
| POST | `/{uuid}/reveal/` | The secret value | **Yes, logged** |
| POST | `/by-name/{name}/reveal/` | The secret value, looked up by display name | **Yes, logged** |

Names with spaces are URL-encoded: `AWS Sandbox Key` → `AWS%20Sandbox%20Key`.

## How we use it
1. **Save the token** in the API console (`tools/api-console/`). It's stored encrypted and reused by the script below.
2. **See what we have:** `powershell -File tools/creds/pull-creds.ps1 -List` (reveals nothing).
3. **Pull keys** into the repo-root `.env` (gitignored): `powershell -File tools/creds/pull-creds.ps1`
   or one key: `... -Name 'AWS Sandbox Key'`.
4. **Code reads from env vars / `.env`.** Never hardcode a key, print it, or paste it into chat.

## Raw curl (reference)
```bash
curl -s -H "Authorization: Bearer $TOKEN" https://cdao.hackathon.maximus.com/api/teams/7e3a3e89-0e20-42b5-b646-8a857cd51012/credentials/
curl -s -X POST -H "Authorization: Bearer $TOKEN" https://cdao.hackathon.maximus.com/api/teams/7e3a3e89-0e20-42b5-b646-8a857cd51012/credentials/by-name/AWS%20Sandbox%20Key/reveal/
```
