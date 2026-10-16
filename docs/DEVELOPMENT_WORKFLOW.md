# Development workflow

Use one task description, one short implementation plan, and evidence from the affected checks. No additional process tools or dependencies are required. Read [AGENTS.md](../AGENTS.md) and [Definition of Done](DEFINITION_OF_DONE.md) first. This workflow grants no permission to commit, push, merge, create PRs, or perform destructive operations.

## Start a feature

Run at the repository root (the directory containing `DeCakes.slnx`):

```powershell
git status --short
git branch --show-current
git diff
git diff --cached
dotnet --version
```

Preserve existing changes and agree on the scope, source branch, and acceptance criteria. The developer creates a dedicated feature branch from the agreed base, or explicitly authorizes Codex to do so. Example only, after choosing the base and authorizing Git writes:

```powershell
git switch -c feature/catalog-read
```

Do not switch branches over unrelated work or automatically stash/reset it. If a task forbids Git writes, stay on the current branch, report the exception, and leave integration to the developer. The workflow-setup task uses that exception.

## Lifecycle and gates

### 1. Understand

- **Objective:** establish the actual requirement and existing behavior.
- **Inputs:** task, acceptance examples, current branch/diffs, README, relevant source/callers, tests, and SQL documentation.
- **Developer:** supplies business decisions, desired contracts, and environment constraints.
- **Codex:** traces request → controller → Application → Infrastructure → schema; identifies reusable code, safeguards, unknowns, and existing validation commands.
- **Output:** concise audit with verified file references, proposed scope, and unresolved questions.
- **Gate:** requirements and affected paths are understood. Unknown business rules or API contracts block dependent implementation, not independent investigation.

### 2. Plan

- **Objective:** choose the smallest safe change and independently testable increments.
- **Inputs:** audit, confirmed requirements, architecture and environment constraints.
- **Developer:** resolves contract questions and approves changes when requested or required, including Accounts/shared infrastructure changes.
- **Codex:** lists files, behavior, test cases, risks, and implementation order; labels proposed contracts instead of presenting them as existing APIs.
- **Output:** short file/change plan and acceptance checks for each increment. Do not create a separate plan document for every small task.
- **Gate:** no unresolved decision affects the next increment; required approvals are explicit. Approval of a plan does not authorize Git publication or destructive operations.

### 3. Implement

- **Objective:** implement only the agreed increment using existing patterns.
- **Inputs:** settled contract, scoped plan, authorized branch/environment.
- **Developer:** answers newly discovered business questions and reviews scope changes.
- **Codex:** makes minimal edits, preserves unrelated work, uses parameterized SQL, and reuses connection/security infrastructure. Stop dependent work if a new decision is needed.
- **Output:** one reviewable increment with relevant behavior tests and documentation updates.
- **Gate:** increment matches the plan and architecture; no hidden schema, dependency, security, or Accounts changes. Return to Plan for scope changes.

### 4. Test

- **Objective:** demonstrate the affected behavior safely.
- **Inputs:** increment, test cases, inspected fixtures, suitable test environment.
- **Developer:** designates any disposable MySQL instance and authorizes write-test setup/cleanup; supplies missing prerequisites without posting secrets.
- **Codex:** runs build and relevant tests, captures exact output, fixes failures and reruns failed checks. For feature work, verify HTTP/service/persistence behavior at the layers changed.
- **Output:** command/output/status evidence, counts including skips, and explicit environment limits.
- **Gate:** required checks pass. Skipped or unavailable required behavior checks remain unverified; do not substitute compile success or historical results.

### 5. Review

- **Objective:** assess correctness, security, and unintended impact.
- **Inputs:** full tracked/untracked changes, requirements, test evidence, updated documentation.
- **Developer:** reviews behavior, unresolved risks, and contract compatibility.
- **Codex:** checks architecture boundaries, SQL parameters, visibility/authentication, secret exposure, errors, unrelated changes, and documentation links/commands. Inspect new files directly because ordinary `git diff` omits untracked files.
- **Output:** focused diff, corrected findings, and a proposed commit/PR description if requested.
- **Gate:** relevant [completion criteria](DEFINITION_OF_DONE.md) pass and findings are resolved or explicitly reported as outstanding.

### 6. Integrate and hand off

- **Objective:** deliver a reviewable result and integrate only with authorization.
- **Inputs:** reviewed changes, validation evidence, agreed base/branch, developer authorization for each Git action.
- **Developer:** authorizes or performs staging/commit/push/PR/merge and follows team review requirements. Do not infer authorization to merge from permission to create a PR.
- **Codex:** reports changes, exact validation, limitations, risks, and next task; performs only authorized Git actions and reports their actual results. Describe trigger, resulting behavior, and validation in a PR; omit credentials and unrelated history.
- **Output:** final handoff; optionally an authorized commit/PR. A code/documentation task can finish with a reviewed working-tree diff when integration is outside scope.
- **Gate:** Definition of Done and the agreed deliverable are satisfied. Never represent an uncommitted handoff as merged or published.

## Validation commands

The SDK is selected by [global.json](../global.json). Reuse the solution and xUnit projects; no new runner is needed. Restore only declared dependencies:

```powershell
dotnet restore DeCakes.slnx --disable-parallel -m:1 -nr:false
dotnet build DeCakes.slnx --no-restore -m:1 -nr:false
```

For database-free checks, run the following in a PowerShell process. It temporarily clears only the test connection variable, restores it afterward, and excludes the write-based persistence class. Never print its saved value:

```powershell
$savedTestAdmin = [Environment]::GetEnvironmentVariable('DECAKES_TEST_MYSQL_ADMIN', 'Process')
try {
    [Environment]::SetEnvironmentVariable('DECAKES_TEST_MYSQL_ADMIN', $null, 'Process')
    dotnet test DeCakes.slnx --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName!~MySqlUserAccountStoreTests'
    if ($LASTEXITCODE -ne 0) { throw 'Database-free tests failed; inspect the test output.' }
}
finally {
    [Environment]::SetEnvironmentVariable('DECAKES_TEST_MYSQL_ADMIN', $savedTestAdmin, 'Process')
}
```

Application/API tests use fakes or an in-memory store. Infrastructure safety tests need no server. The real Kestrel HTTPS test needs an existing development certificate and otherwise skips; see [tests/README.md](../tests/README.md). Do not create/trust certificates as an unreported side effect of a check. No MySQL persistence proof comes from this database-free run.

For database-related feature changes, additionally use the separate MySQL 8.4 setup and command in [tests/README.md](../tests/README.md), after inspecting its fixtures and confirming the disposable loopback target. Never run SQL installation, seed, migration, grants, or write tests against shared Aiven. Only bounded read-only queries are permitted there; avoid sensitive data and mutating procedures.

Final read-only review:

```powershell
git diff --check
git diff --stat
git diff
git diff --cached
git status --short
```

Read every new file, validate local Markdown destinations relative to each document, and ensure only agreed files changed. For documentation-only work, review commands against source and run the safe build/test baseline; do not execute secret setup, branch examples, database writes, or publication commands just to validate the documentation.

## Repository audit baseline — 2026-10-16

- Four `net10.0` source projects and three xUnit projects already exist. Accounts supplies the service/store/controller pattern; the connection factory enforces TLS/CA checks and per-operation ownership.
- Existing safeguards to reuse: `.editorconfig`, `.gitignore`, `scripts/SetDbSecret.cs`, `database/README.md`, and the guarded loopback MySQL fixture. Its restricted test user currently has INSERT/UPDATE only on `users`; Catalog fixture changes require separate approval.
- At audit start the working tree was clean on `AnDev`, SDK `10.0.401`; none of the four requested workflow files existed, and there was no `.agents` or `.github/workflows` directory. No CI was added by this task.
- Workflow setup adds persistent operating rules and completion gates, and corrects stale EF/authentication/test wording. SQL files remain reference inputs. Earlier validation recorded in README is historical, not evidence for a later task.
- [Catalog plan](CATALOG_PLAN.md) is a future proposal. Resolve its public contract before implementation. Risks include inherited write-test configuration, running SQL scripts on Aiven, exposing secrets in diagnostics, and treating schema status values as a complete visibility policy.
