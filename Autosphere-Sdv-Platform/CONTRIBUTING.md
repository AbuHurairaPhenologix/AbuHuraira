# Contributing to AutoSphere

Thank you for your interest. AutoSphere is an academic prototype; contributions that improve correctness,
test coverage, documentation or the automotive realism of the simulation are especially welcome.

## Development set-up

Prerequisites: .NET SDK 10, Node.js 24 (Angular CLI 21), Docker Desktop / Docker Engine with Compose.

```bash
cp .env.example .env
dotnet restore && dotnet build
dotnet test --project tests/AutoSphere.UnitTests
dotnet test --project tests/AutoSphere.ArchitectureTests
dotnet test --project tests/AutoSphere.IntegrationTests
cd src/frontend/autosphere-web && npm ci && npm run build && npm test -- --watch=false
```

See the README for running the stack locally (`scripts/run-local.sh` / `scripts/run-local.ps1`).

## Ground rules

* **Build warnings are errors.** Keep the analyzers green instead of suppressing them; justify any
  suppression in a comment.
* **Respect the architecture.** Domain has no infrastructure dependencies; controllers stay thin; gateway
  libraries do not depend on the backend. `tests/AutoSphere.ArchitectureTests` enforces this.
* **Tests with every change.** Unit tests for logic, integration tests for cross-component behaviour.
* **Technical honesty.** Describe simulated behaviour as simulated and standards-inspired features as
  *inspired* (UDS, ISO-TP, AUTOSAR E2E, COVESA VSS). Never claim certification or compliance.
* **No secrets.** Never commit `.env`, keys (`.keys/`, `*.pem`), credentials, tokens or build output.

## Commits and pull requests

Use [Conventional Commits](https://www.conventionalcommits.org/):

```text
feat(gateway): add CAN bus abstraction
fix(ota): reject packages with mismatching payload size
test(diagnostics): cover NRC handling for unknown DIDs
docs: describe the OTA state machine
```

Scopes in use: `shared`, `canbus`, `signals`, `diagnostics`, `simulator`, `gateway`, `domain`, `backend`,
`api`, `web`, `ota`, `docker`, `ci`. Avoid commits such as "update", "fix stuff" or "final".

Open a pull request against `main`, fill in the template and make sure CI is green.
