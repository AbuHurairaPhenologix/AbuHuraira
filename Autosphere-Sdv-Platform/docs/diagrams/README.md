# Diagrams

All diagrams are written in [Mermaid](https://mermaid.js.org/) so they render directly on GitHub and stay
versioned with the code. Standalone sources are kept here for reuse in the thesis (export with
`npx @mermaid-js/mermaid-cli -i <file>.mmd -o <file>.svg`).

| Diagram | Source | Also embedded in |
|---|---|---|
| System context and containers | [system-context.mmd](system-context.mmd) | README, [system-architecture.md](../architecture/system-architecture.md) |
| OTA deployment state machine | [ota-state-machine.mmd](ota-state-machine.mmd) | [ota-update-flow.md](../architecture/ota-update-flow.md) |
| Battery-overheat scenario | [overheat-sequence.mmd](overheat-sequence.mmd) | README, [data-flow.md](../architecture/data-flow.md) |

Further diagrams (data flow, diagnostics sequence, database ER model, CAN/ISO-TP) are embedded in the
documents under `docs/architecture` and `docs/automotive`.
