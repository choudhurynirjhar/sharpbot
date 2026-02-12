# Jira Plugin

Jira integration for Sharpbot. This plugin adds tools to search issues, inspect tickets, list projects/boards/sprints, and read comments.

## What It Provides

- `jira_search`: Search issues with filters or raw JQL
- `jira_ticket`: Get full details for one issue key (for example `PROJ-123`)
- `jira_projects`: List projects or inspect one project
- `jira_boards`: List Jira Software boards
- `jira_sprints`: List sprints for a board
- `jira_comments`: Get comments on an issue

## Required Environment Variables

- `JIRA_BASE_URL`: Jira URL (Cloud or Data Center)
  - Example Cloud: `https://yourcompany.atlassian.net`
  - Example Data Center: `https://jira.company.com`
- `JIRA_API_TOKEN`: API token or personal access token
- `JIRA_EMAIL` (recommended for Cloud): Atlassian account email
- `JIRA_API_VERSION` (optional): `2` or `3` (auto-detected if omitted)

Authentication mode:
- If `JIRA_EMAIL` is set, plugin uses Basic auth (`email:token`)
- If `JIRA_EMAIL` is empty, plugin uses Bearer token

## How to Get Jira API Token

### Jira Cloud

1. Open `https://id.atlassian.com/manage-profile/security/api-tokens`
2. Create API token
3. Copy token and set:

```powershell
$env:JIRA_BASE_URL = "https://yourcompany.atlassian.net"
$env:JIRA_EMAIL = "you@company.com"
$env:JIRA_API_TOKEN = "your-api-token"
```

### Jira Data Center / Server

Create a personal access token in your Jira instance, then set:

```powershell
$env:JIRA_BASE_URL = "https://jira.company.com"
$env:JIRA_API_TOKEN = "your-personal-access-token"
# Optional for Basic auth mode:
# $env:JIRA_EMAIL = "your-username-or-email"
```

## Quick Usage Examples

Search open bugs in a project:

```json
{
  "tool": "jira_search",
  "arguments": {
    "project": "PROJ",
    "type": "Bug",
    "status": "In Progress",
    "max_results": 20
  }
}
```

Search by sprint ID:

```json
{
  "tool": "jira_search",
  "arguments": {
    "sprint_id": 143,
    "max_results": 30
  }
}
```

Get one ticket:

```json
{
  "tool": "jira_ticket",
  "arguments": {
    "key": "PROJ-123"
  }
}
```

List boards for a project:

```json
{
  "tool": "jira_boards",
  "arguments": {
    "project": "PROJ"
  }
}
```

List active sprints for a board:

```json
{
  "tool": "jira_sprints",
  "arguments": {
    "board_id": 27,
    "state": "active"
  }
}
```

Get issue comments:

```json
{
  "tool": "jira_comments",
  "arguments": {
    "key": "PROJ-123"
  }
}
```

## Notes

- `jira_search` supports either:
  - structured filters (`project`, `status`, `assignee`, date filters, etc.), or
  - raw `jql` (if `jql` is provided, other filters are ignored)
- Cloud usually uses REST API v3; Data Center/Server usually uses v2
