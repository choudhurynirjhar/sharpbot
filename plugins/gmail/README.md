# Gmail Plugin

Gmail integration for Sharpbot. This plugin adds tools to search, read, send, reply to, and manage Gmail messages using OAuth2.

## What It Provides

- `gmail_search`: Search emails with Gmail query syntax (`in:inbox`, `from:...`, `is:unread`, etc.)
- `gmail_read`: Read one message by `message_id` or a full conversation by `thread_id`
- `gmail_send`: Send a new email
- `gmail_reply`: Reply to an existing thread
- `gmail_manage`: Archive, trash, star/unstar, mark read/unread, add/remove labels

## Required Environment Variables

- `GMAIL_CLIENT_ID`: OAuth2 client ID from Google Cloud
- `GMAIL_CLIENT_SECRET`: OAuth2 client secret from Google Cloud
- `GMAIL_TOKEN_PATH` (optional): where refresh/access tokens are stored

If `GMAIL_TOKEN_PATH` is not set, Sharpbot stores tokens at:

`{app-base}/plugins/gmail/data/gmail-token.json`

## One-Time OAuth Setup (How to Get the Gmail Token)

The plugin uses OAuth2 and stores a refresh token locally. Do this once:

1. Open Google Cloud Console: `https://console.cloud.google.com`
2. Create/select a project
3. Enable **Gmail API**
4. Go to **APIs & Services -> Credentials**
5. Create **OAuth client ID**
   - Application type: **Desktop app**
6. Copy the generated client ID and client secret
7. Set environment variables:

```powershell
$env:GMAIL_CLIENT_ID = "your-client-id.apps.googleusercontent.com"
$env:GMAIL_CLIENT_SECRET = "your-client-secret"
# Optional:
$env:GMAIL_TOKEN_PATH = "C:\path\to\gmail-token.json"
```

8. Run authorization command:

```bash
sharpbot gmail-auth
```

9. Your browser opens Google login/consent
10. After approval, Sharpbot saves tokens to `GMAIL_TOKEN_PATH` (or default path)

Notes:
- Scope used is `https://www.googleapis.com/auth/gmail.modify`
- Callback uses `http://localhost:8765/callback`
- Access token refresh is automatic after initial authorization

## Quick Usage Examples

Search inbox:

```json
{
  "tool": "gmail_search",
  "arguments": {
    "query": "in:inbox newer_than:2d",
    "max_results": 5
  }
}
```

Read a message:

```json
{
  "tool": "gmail_read",
  "arguments": {
    "message_id": "18c123abc456def"
  }
}
```

Send an email:

```json
{
  "tool": "gmail_send",
  "arguments": {
    "to": "user@example.com",
    "subject": "Status update",
    "body": "Everything is on track."
  }
}
```

Reply in a thread:

```json
{
  "tool": "gmail_reply",
  "arguments": {
    "thread_id": "18c111aaa222bbb",
    "message_id": "18c123abc456def",
    "body": "Thanks, I have reviewed this."
  }
}
```

Archive a message:

```json
{
  "tool": "gmail_manage",
  "arguments": {
    "message_id": "18c123abc456def",
    "action": "archive"
  }
}
```

## Troubleshooting

- "not configured": set `GMAIL_CLIENT_ID` and `GMAIL_CLIENT_SECRET`
- "not authorized": run `sharpbot gmail-auth`
- Browser did not open: copy the OAuth URL from logs and open manually
- Token expired/revoked: run `sharpbot gmail-auth` again
