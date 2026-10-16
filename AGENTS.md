# Codex instructions for DeCakes

## Start with evidence

- Read [README.md](README.md), the relevant source and callers, current diffs, and applicable database/test documentation before editing. Confirm every referenced type, dependency, and schema field by reading or searching this checkout.
- Use **Understand → Plan → Implement → Test → Review → Integrate** in [the workflow](docs/DEVELOPMENT_WORKFLOW.md). For non-trivial work, propose affected files, acceptance criteria, risks, and independently testable steps first.
- Ask about missing business rules, public contracts, or conflicting requirements before implementing dependent work. Continue independent inspection; label proposals and unknowns explicitly. Never silently expand scope.

## Minimize Scope

Only modify files directly related to the task.

Does this need to exist?
- No → Skip it. (YAGNI)
- Yes → Prefer:
  1. Standard library.
  2. Native platform feature.
  3. Existing dependency.
  4. The simplest solution.
  5. Only then write the minimum code that works.

Minimum means:
- Follow the existing architecture.
- Match project conventions.
- Optimize for maintainability.
- Avoid unnecessary abstractions.

Avoid:

- Unrelated refactoring.
- Renaming without a stated reason.
- Moving code between layers unnecessarily.
- Architecture changes unless explicitly requested.

## Explain Before Acting

Before writing code:

1. State what you found.
2. State what is missing or incorrect.
3. State what you will change and why.

Then implement. Then validate.

## Architecture Consistency

Follow the existing architecture. Prefer consistency with the repository over personal preference.

When the existing architecture has a problem, name it and propose a fix — but as a separate task, not a side effect of the current one.

## Architecture and conventions

- Keep .NET 10 and ASP.NET Core Controllers. Follow `.editorconfig`: UTF-8, CRLF, file-scoped C# namespaces, `I` interfaces, and underscore-prefixed private fields.
- Dependencies: Domain has no project dependencies; Application references Domain; Infrastructure references Application and Domain; Api references Application and Infrastructure (Infrastructure for composition/DI).
- Follow Accounts: feature DTOs/interfaces/services in Application, parameterized SQL and mapping in Infrastructure, HTTP/ProblemDetails in Api. Domain contains independent business concepts, not generated table copies.
- Reuse `AddApplication()`, `AddInfrastructure(configuration)`, and `IMySqlConnectionFactory`. Propagate `CancellationToken`; dispose connections, commands, and readers with `await using` as existing persistence does.
- Keep MySqlConnector 2.6.2 and ADO.NET. Do not introduce EF Core, DbContext, scaffolding, automatic migrations, Oracle `MySql.Data`, `MySql.EntityFrameworkCore`, or Pomelo. Avoid unnecessary packages, generic repositories, Unit of Work, or duplicate infrastructure.
- Do not modify Accounts or shared infrastructure without explicit developer approval. If a feature needs a shared DI registration, name that file/change in the plan and obtain approval before editing it.

## Database and security

- Treat Aiven as shared. Never seed, reset, truncate, migrate, run installation scripts, or otherwise change its data, schema, users, grants, or procedures. Only necessary, bounded read-only verification is allowed; never call a mutating procedure as a diagnostic.
- Repository SQL is the schema reference, not proof of the live database state. Preserve existing SQL files. Any future schema change requires a separate agreed task; do not apply it automatically.
- Use parameterized SQL for values. Dynamic SQL identifiers/order clauses require a fixed server-side allowlist. MySqlConnector types stay inside Infrastructure.
- Production/development connections retain `SslMode=VerifyFull`, an existing Aiven CA, and a restricted backend account; never use `avnadmin` or weaken TLS to bypass failures. Connections open per operation, not at application startup.
- Six business procedures manage their own transactions; call them without an application transaction already open. Consult [procedure documentation](database/STORED_PROCEDURES.md) before use.
- Preserve secure HttpOnly cookies, CSRF checks on unsafe methods, role policies, session validation, and masked errors. Do not add database diagnostic endpoints or copy the health-only session-validation exception to feature endpoints.
- Never print/store credentials, credential-bearing connection strings, user-secrets, tokens, or password hashes in task output, logs, or tracked files. Do not enumerate secret values. Respect `.gitignore`; reuse `scripts/SetDbSecret.cs` only when configuration is requested, not as validation.

## Tests, Git, and completion

- Reuse the three xUnit projects and their fake/in-memory patterns. Inspect fixtures before running tests: `DECAKES_TEST_MYSQL_ADMIN` enables database/account creation and cleanup. Default checks must not inherit it; use [database-free commands](docs/DEVELOPMENT_WORKFLOW.md#validation-commands).
- Write-based integration tests use an explicitly designated, disposable loopback MySQL 8.4 instance only. Reuse its safety guards; never substitute Aiven, development secrets, or an unrelated local database. Setup/reset/cleanup must be within the developer-approved test scope.
- Record starting branch/status and preserve uncommitted work. Future feature implementation belongs on a dedicated feature branch, created/switched by the developer or explicitly authorized Codex. Never reset, clean, force checkout, or overwrite unrelated work. If Git writes are prohibited, report that constraint and do not create a branch.
- Never commit, push, merge, or create a PR without authorization. Destructive operations require explicit developer approval; workflow approval is not blanket authorization for them.
- Build and run relevant behavior tests, inspect the final diff including untracked files, and check documentation links. Do not add tests that merely mirror documentation or low-impact edits.
- Apply [Definition of Done](docs/DEFINITION_OF_DONE.md). Report Changes, Validation (exact command, actual output, PASS/FAIL/NOT RUN), Result, Limitations, and Risks. Historical results and skipped tests are not fresh validation. Use DONE only when all required checks pass and every reported Validation entry is PASS; otherwise PARTIAL or BLOCKED with the precise remaining work.
