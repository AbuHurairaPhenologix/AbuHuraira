# Security policy

AutoSphere is a research prototype. It is **not** intended to control real vehicles and has not undergone
a security certification (e.g. ISO/SAE 21434, UNECE R155/R156) or functional-safety assessment (ISO 26262).

## Supported versions

Only the latest commit on `main` receives fixes.

## Reporting a vulnerability

Please do **not** open a public issue for security problems. Report them privately through GitHub's
"Report a vulnerability" (Security Advisories) feature of this repository, including:

* affected component (gateway, simulator, backend, web, Docker configuration),
* steps to reproduce or a proof of concept that does not target third-party systems,
* expected impact.

You can expect an acknowledgement within 7 days.

## Security design and known limitations

The security architecture (JWT/RBAC, secret handling, ECDSA-signed OTA packages, vehicle-side verification,
A/B rollback) and its known limitations (anonymous MQTT in the development stack, no TLS in the default
Compose file, no HSM/KMS, no device certificates) are documented in
[docs/architecture/security.md](docs/architecture/security.md). Review that document before deploying
AutoSphere anywhere beyond a local machine.

Never commit secrets: `.env`, `.keys/`, `*.pem`, connection strings with passwords or JWT signing keys are
excluded by `.gitignore`.
