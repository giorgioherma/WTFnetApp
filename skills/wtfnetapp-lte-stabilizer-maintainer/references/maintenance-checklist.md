# WTFnetApp Maintenance Checklist

## Ownership checks
- `NetworkControlState` defined once.
- `DecisionEngine` class defined once.
- `NetworkController.ProcessSnapshot` defined once and follows:
  1) assign `ExternalPingMs`
  2) `metricsEngine.Process(snapshot)`
  3) `decisionEngine.Update(snapshot, metricsEngine.DataSet)`
  4) `speedifyController.Apply(state, snapshot)`

## Dead-code checks
Run targeted searches before deleting files:

```bash
rg -n "ClassName|new ClassName|ClassName\." *.cs WTFnetApp.csproj
```

Delete only when:
- no runtime references remain
- no project include is needed
- removal does not break architectural invariants

## Project-file checks
- No duplicate `<Compile Include="..." />` entries.
- Removed files are absent from `.csproj`.
- Retained files appear once.

## Safe refactor boundary
Allowed:
- duplicate removal
- merge/collision cleanup
- unused private symbol removal
- constant extraction (no value change)

Not allowed in cleanup-only pass:
- state-machine redesign
- threshold tuning changes
- new predictor/control path
- public behavior changes

## Report template
- Files cleaned
- Files deleted
- Ownership consolidations
- Invariants verified
- Risks not changed (with reason)
