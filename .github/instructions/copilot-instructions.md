// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
---
applyTo: '**'
---

# TansuCloud – AI Coding Guidelines and Project Context

These guidelines apply to all code generation, refactoring, documentation, and reviews across this repository. Favor modern .NET practices, maintainability, and clear separation of concerns.

## Core Principles
- Prefer modern .NET features and idioms (.NET 9 as of today), including async/await, nullable reference types, dependency injection, logging, configuration, and health checks.
- Maintain a clean, maintainable codebase with clear separation of concerns (controllers thin; business logic in services; data access in repositories or EF Core DbContexts; configuration via Options pattern).
- Apply SOLID, clean architecture boundaries, and avoid leaking infrastructure concerns into domain logic.
- Write testable code (interfaces, dependency injection, minimal side effects). Add unit/integration tests where changes affect behavior.
- Use cancellation tokens, structured logging, and consistent error handling (ProblemDetails for APIs) when appropriate.

## End-of-block Comments (Long-lived Maintenance Aid)
Adopt explicit end-of-block comments to improve code folding and long-term readability in multi-member types:
- Classes: `} // End of Class ClassName`
- Methods: `} // End of Method MethodName`
- Constructors: `} // End of Constructor ClassName`
- Properties: `} // End of Property PropertyName`

Apply these consistently for non-trivial members and files with multiple classes or long types.

## Documentation and Explanations
- Search Microsoft’s latest official documentation (Microsoft Learn/Docs) whenever needed, especially for .NET, ASP.NET Core, EF Core, C#, and Azure. Prefer first-party guidance and current practices.
- Add explanatory comments whenever needed to clarify non-obvious intent, invariants, thread-safety, performance-sensitive code paths, and public surface behaviors. Use XML doc comments for public APIs.

## Mandatory File Header
Add the following exact line at the very top of every file you create or edit in this repository:

// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

Keep this as the first line (above using directives, shebangs, etc.).

## Coding Style and Patterns
- Enable nullable reference types and treat warnings seriously; prefer explicit null-handling.
- Use records and readonly structs when immutability is beneficial; prefer init-only setters for DTOs.
- Prefer async methods suffixed with "Async" and pass CancellationToken to I/O and long-running operations.
- Keep controllers thin; validate inputs (DataAnnotations/FluentValidation), delegate to services, return ProblemDetails for errors.
- Configuration via IOptions<T> (Options pattern); no hard-coded settings or secrets in code.
- Favor minimal APIs or conventional controllers based on project consistency; keep endpoint mapping discoverable.
- Log with structured templates (e.g., logger.LogInformation("Processing {OrderId}", orderId)). Avoid logging sensitive data.

## Testing and Quality
- Add or update unit tests for new behavior and critical bug fixes. Prefer xUnit with fluent assertions if available.
- Prefer small, focused tests over broad, fragile ones. Cover happy path + 1–2 edge cases.
- Keep public behavior backward-compatible unless called out with migration notes.

## Project Specs and Task Acceptance
- Follow these repository documents as sources of truth and align implementations accordingly: `Requirements.md`, `Architecture.md`, and `Tasks.md`.
- Conflict precedence: Requirements > Architecture > Tasks > Code comments. If updates are needed, revise the docs alongside code.
- A task can be marked completed only after its Acceptance criteria are tested and verified. Provide evidence via automated tests and/or documented manual steps (screenshots, logs, or Playwright MCP runs) and reference them in the PR.

## Web App Testing with Playwright MCP Tools
- Use Playwright MCP tools for web app development testing.
- Copilot can load the web app, type, login, and click.
- Analyse screenshots and browser console logs to test and validate the web app.
- Also leverage navigation and interaction (navigate, click, type, fill, select, drag, upload), visuals (screenshot, accessibility snapshot), and telemetry (console messages, network requests) as needed.
- Apply pragmatic waits/retries to reduce flakiness; prefer robust selectors and avoid brittle timing assumptions.
- Keep credentials and secrets out of logs; use test accounts and mask sensitive values.

## Security and Compliance
- Don’t commit secrets. Use configuration providers (user secrets, environment variables, Key Vault) and secure defaults.
- Validate and sanitize external inputs. Apply authorization and authentication consistently.

## Performance and Reliability
- Use CancellationToken, IAsyncEnumerable where beneficial, and avoid synchronous-over-async.
- Be mindful of allocations and logging levels on hot paths; prefer pooling/caching where appropriate and safe.

By following these rules, contributors and AI assistants will produce modern, maintainable, and well-documented .NET code for TansuCloud.