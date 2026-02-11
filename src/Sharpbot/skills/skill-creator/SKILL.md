---
name: skill-creator
description: Create or update skills for the agent. Use when designing, structuring, or packaging skills with scripts, references, and assets.
---

# Skill Creator

This skill provides guidance for creating effective skills.

## About Skills

Skills are modular, self-contained packages that extend the agent's capabilities by providing
specialized knowledge, workflows, and tools. They transform a general-purpose agent into a
specialized agent equipped with procedural knowledge.

### What Skills Provide

1. Specialized workflows - Multi-step procedures for specific domains
2. Tool integrations - Instructions for working with specific file formats or APIs
3. Domain expertise - Company-specific knowledge, schemas, business logic
4. Bundled resources - Scripts, references, and assets for complex and repetitive tasks

## Core Principles

### Concise is Key

The context window is a public good. Skills share the context window with everything else.
Only add context the agent doesn't already have. Challenge each piece of information:
"Does the agent really need this?" and "Does this justify its token cost?"

Prefer concise examples over verbose explanations.

### Anatomy of a Skill

Every skill consists of a required SKILL.md file and optional bundled resources:

```
skill-name/
├── SKILL.md (required)
│   ├── YAML frontmatter metadata (required)
│   │   ├── name: (required)
│   │   └── description: (required)
│   └── Markdown instructions (required)
└── Bundled Resources (optional)
    ├── scripts/          - Executable code (Python/Bash/etc.)
    ├── references/       - Documentation to load into context as needed
    └── assets/           - Files used in output (templates, icons, etc.)
```

### SKILL.md (required)

Every SKILL.md consists of:

- **Frontmatter** (YAML): Contains `name` and `description` fields. The description is the primary
  mechanism that determines when the skill gets used — be clear and comprehensive.
- **Body** (Markdown): Instructions and guidance for using the skill.

### Optional metadata field

The frontmatter can include an optional `metadata` field with a JSON string for requirements gating:

```yaml
metadata: {"sharpbot":{"requires":{"bins":["gh"],"env":["GITHUB_TOKEN"]},"os":["darwin","linux"]}}
```

Supported gating:
- `bins` - Required CLI binaries (all must be on PATH)
- `anyBins` - At least one of these binaries must be on PATH
- `env` - Required environment variables
- `config` - Required config keys (dot-separated)
- `os` - Restrict to specific operating systems (`darwin`, `linux`, `win32`)
- `always` - If true, skill is always loaded into context (set at top level, not under requires)

### Bundled Resources

**Scripts (`scripts/`)**: Executable code for deterministic tasks. Token-efficient, may be executed
without loading into context.

**References (`references/`)**: Documentation loaded as needed into context.

**Assets (`assets/`)**: Files used in output (templates, images, etc.), not loaded into context.

## Skill Locations

Skills are loaded from three locations (highest to lowest priority):

1. **Workspace skills**: `{workspace}/skills/` — User's custom skills
2. **Managed skills**: `data/skills/` — Installed/managed skills (relative to app directory)
3. **Built-in skills**: Bundled with sharpbot — Default skills

Workspace skills override managed/built-in skills of the same name.

## Creating a Skill

### Step 1: Create the directory

```bash
mkdir -p skills/my-skill
```

### Step 2: Write SKILL.md

```markdown
---
name: my-skill
description: Describe what the skill does and when to use it. Be specific about trigger conditions.
---

# My Skill

Instructions for the agent...
```

### Step 3: Add resources (optional)

```bash
mkdir -p skills/my-skill/scripts
mkdir -p skills/my-skill/references
mkdir -p skills/my-skill/assets
```

## Skill Naming

- Use lowercase letters, digits, and hyphens only
- Prefer short, verb-led phrases that describe the action
- Namespace by tool when helpful (e.g., `gh-address-comments`)
- Name the skill folder exactly after the skill name

## Progressive Disclosure

Skills use a three-level loading system:

1. **Metadata (name + description)** - Always in context (~100 words)
2. **SKILL.md body** - When skill triggers (<5k words)
3. **Bundled resources** - As needed by the agent (unlimited)

Keep SKILL.md body under 500 lines. Split content into separate files when approaching this limit.

## What NOT to Include

- README.md, CHANGELOG.md, INSTALLATION_GUIDE.md
- User-facing documentation
- Setup and testing procedures
- Auxiliary context about the creation process

The skill should only contain information needed for an AI agent to do the job.
