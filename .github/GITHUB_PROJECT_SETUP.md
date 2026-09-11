# GitHub Project Setup Guide

**Status**: Ready to Create Project  
**Platform**: GitHub Projects (new table format)  
**Repository**: gwhitdev/betsi

---

## Prerequisites

1. **GitHub Personal Access Token (PAT)**
   - Visit: https://github.com/settings/tokens
   - Create new token with scopes: `repo`, `project`
   - Copy token (you'll only see it once)

2. **GitHub CLI (Optional but recommended)**
   - Install from https://cli.github.com/
   - Or use the PowerShell scripts provided (no CLI required)

---

## Option A: Automated Setup (PowerShell Script)

### Step 1: Create the GitHub Project

```powershell
# Set your token
$token = "ghp_xxxxxxxxxxxxxxxxxxxx"

# Create project and first test issue
.\create-github-project.ps1 -GitHubToken $token
```

**What it does**:
- Creates "Betsi Patient Flow MVP" project
- Creates MVP-001 as a test issue
- Returns project URL

### Step 2: Create All 115 MVP Issues

```powershell
# Create all issues (this may take 2-3 minutes)
.\create-all-issues.ps1 -GitHubToken $token

# Or dry-run first to see what would be created
.\create-all-issues.ps1 -GitHubToken $token -DryRun
```

**What it does**:
- Creates issues MVP-001 through MVP-115
- Tags with labels (MVP, Core, Escalation, Paediatric, etc.)
- Includes acceptance criteria in issue body

### Step 3: Configure Project Columns

1. Go to your project: `https://github.com/gwhitdev/betsi`
2. Click **Projects** tab → **Betsi Patient Flow MVP**
3. Add columns (if using table view):
   - **Backlog**: New issues, not yet refined
   - **Ready**: Accepted, clear AC, dependencies identified
   - **In Progress**: Currently being worked
   - **In Review**: PR submitted, code review
   - **Staging**: Merged, tested on staging
   - **Done**: Released to production

4. Configure automation (GitHub Projects → Project settings → Automation):
   - When issue opened → Auto-add to Backlog
   - When PR opened → Auto-update status
   - When PR merged → Auto-move to Done

---

## Option B: Manual Setup via GitHub Web UI

### Step 1: Create Project
1. Go to: https://github.com/gwhitdev/betsi/projects
2. Click **New project**
3. Name: `Betsi Patient Flow MVP`
4. Template: **Table**
5. Click **Create**

### Step 2: Create Issues Manually
1. Go to Issues tab: https://github.com/gwhitdev/betsi/issues
2. Click **New issue**
3. For each of 115 issues, fill:
   - **Title**: MVP-NNN – Issue Name
   - **Description**: Copy from `.github/ISSUES.md`
   - **Labels**: MVP, component (Core, Escalation, etc.), type (Feature, Tech-Debt)
   - **Click Submit**

*Alternative*: Use CSV bulk import if GitHub supports it for your plan

---

## Project Configuration

### Kanban Board Layout

```
Backlog          Ready           In Progress     In Review       Staging         Done
─────────────────────────────────────────────────────────────────────────────────
MVP-050          MVP-001         MVP-005         MVP-003         MVP-002         MVP-004
MVP-051          MVP-006         MVP-010         MVP-007         MVP-009         MVP-008
MVP-052          MVP-020         MVP-021                         MVP-025         MVP-030
...              ...             ...             ...             ...             ...
```

### Table View Columns

| Column | Type | Filter |
|---|---|---|
| Title | Text | Issue title |
| Status | Single Select | Backlog, Ready, In Progress, In Review, Staging, Done |
| Priority | Single Select | P0 (CLI), P1 (High), P2 (Medium), P3 (Nice-to-have) |
| Component | Single Select | Core, API, UI, Integration, Testing, Ops |
| Assignee | User | Team member |
| Epic | Single Select | Core Engine, Escalation, Paediatric, Clinical, etc. |
| Milestone | Single Select | MVP Phase 0, v1.1, v2.0 |

---

## Workflow Automation

### GitHub Actions

Create `.github/workflows/project-automation.yml`:

```yaml
name: Project Automation

on:
  issues:
	types: [opened, closed]
  pull_request:
	types: [opened, closed, merged]

jobs:
  update-project:
	runs-on: ubuntu-latest
	steps:
	  - name: Update project status
		uses: actions/github-script@v6
		with:
		  script: |
			// Add automation logic to update project based on issue/PR events
			// Example: move issue to "In Review" when PR opened
```

Alternatively, use GitHub Project's built-in automation settings for simpler workflows.

---

## Issue Labels

Create these labels in your repository:

```yaml
# Priority
- P0 (CLI/Blocking) – Color: #d73a4a (red)
- P1 (High) – Color: #f97316 (orange)
- P2 (Medium) – Color: #eab308 (yellow)
- P3 (Nice-to-have) – Color: #22c55e (green)

# Type
- Feature – Color: #0369a1 (blue)
- Bug – Color: #dc2626 (red)
- Tech-Debt – Color: #7c3aed (purple)
- Documentation – Color: #06b6d4 (cyan)

# Component
- Core – Color: #4f46e5 (indigo)
- API – Color: #2563eb (blue)
- UI – Color: #9333ea (purple)
- Integration – Color: #db2777 (pink)
- Testing – Color: #059669 (green)
- Ops – Color: #78350f (brown)

# Epic
- MVP – Color: #1f2937 (dark gray)
- Escalation – Color: #dc2626 (red)
- Paediatric – Color: #f472b6 (pink)
- Clinical – Color: #06b6d4 (cyan)
- Multi-Tenant – Color: #6366f1 (indigo)
```

---

## How to Get Your GitHub Token

1. Go to: https://github.com/settings/tokens
2. Click **Generate new token (classic)**
3. Name: `Betsi MVP Setup`
4. Scopes: Check `repo` and `project`
5. Expiration: 90 days (or custom)
6. Click **Generate token**
7. ⚠️ **Copy immediately and save securely** (you won't see it again)

**Security Note**: Never commit your token to Git. Use environment variables or secrets management.

---

## Verify Setup

Once issues are created, verify:

```powershell
# List all open issues
gh issue list --repo gwhitdev/betsi --state open

# Count issues by label
gh issue list --repo gwhitdev/betsi --label "MVP" --json title

# View project
gh project list --owner gwhitdev
```

Or visit: https://github.com/gwhitdev/betsi/issues?q=label:MVP

---

## Milestone Setup

Create milestones in Settings → Milestones:

| Milestone | Start | End | Issues |
|---|---|---|---|
| **MVP Phase 0** | Week 1 | Week 4 | MVP-001–115 |
| **MVP Phase 1** | Week 5 | Week 6 | Testing, Pilot Feedback |
| **MVP Phase 2** | Week 7+ | Week 9+ | Adoption, Full ED rollout |
| **v1.1 (Future)** | Post-MVP | +4-6 weeks | Site customization, risk assessments |
| **v2.0 (Future)** | Post-v1.1 | +3-4 months | AI, portals |

---

## Team Setup

In project or repository settings, add team members:

```
Roles:
- Maintainer (admin): Tech Lead, Product Owner
- Developer: Development team (create PRs, resolve issues)
- Viewer: Stakeholders (read-only access to progress)
```

Assign issues to team members during sprint planning.

---

## Notification Settings

Configure notifications:

1. **Watch repository**: https://github.com/gwhitdev/betsi/notifications
2. **Customize notifications**:
   - Issues assigned to me: Email
   - PRs mentioning me: Email
   - Project updates: Web only
   - Discussions: Off (or as needed)

---

## Integration with CI/CD

Link GitHub Project to CI/CD:

1. **GitHub Actions**: Automatically move issues when tests pass/fail
2. **Branch protection**: Require PR review before merge
3. **Auto-deploy**: Merge to main → Deploy to staging; manual approval → Production

Example `.github/workflows/ci.yml`:

```yaml
name: CI/CD

on: [push, pull_request]

jobs:
  build-test:
	runs-on: ubuntu-latest
	steps:
	  - uses: actions/checkout@v3
	  - uses: actions/setup-dotnet@v3
		with:
		  dotnet-version: '10.0'
	  - run: dotnet build
	  - run: dotnet test
```

---

## Quick Reference: GitHub Project URLs

| Resource | URL |
|---|---|
| **Project Board** | https://github.com/gwhitdev/betsi/projects |
| **Issues List** | https://github.com/gwhitdev/betsi/issues |
| **Betsi Patient Flow MVP Project** | https://github.com/gwhitdev/betsi/projects/[NUMBER] |
| **Milestones** | https://github.com/gwhitdev/betsi/milestones |
| **Settings/Labels** | https://github.com/gwhitdev/betsi/labels |

---

## Troubleshooting

### Issue: "Failed to create issue – 401 Unauthorized"
**Solution**: Token expired or invalid. Generate a new token with correct scopes.

### Issue: "Failed to create issue – 422 Validation Failed"
**Solution**: Duplicate title or invalid input. Check issue titles and labels exist.

### Issue: Issues created but not appearing in project
**Solution**: Add automation rule in project settings, or manually drag issues to columns.

### Issue: Rate limit exceeded
**Solution**: GitHub allows 5000 API calls/hour for authenticated requests. If hitting limit, wait and retry, or create issues in batches.

---

## Support & Documentation

- **GitHub Docs**: https://docs.github.com/en/projects
- **GitHub CLI**: https://cli.github.com/manual/
- **Personal Access Tokens**: https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/creating-a-personal-access-token
- **Issue Automation**: https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project

---

**Status**: Ready for Setup  
**Next Action**: Provide GitHub Token or proceed with manual setup  
**Estimated Time**: 5-10 minutes (automated) or 30-45 minutes (manual)
