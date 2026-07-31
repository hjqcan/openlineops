# Industrial qualification evidence

OpenLineOps keeps simulated, hardware-in-the-loop, physical-cycle, and pilot
qualification claims separate. Unit tests and simulated cycles cannot satisfy a
physical or pilot gate.

Run the verifier against a qualification evidence directory:

```powershell
./eng/verify-industrial-qualification-evidence.ps1 `
  -EvidenceRoot artifacts/industrial-qualification/<qualification-id> `
  -Level Pilot
```

The directory must contain `qualification-summary.json` plus every artifact
referenced by that summary. Artifact paths are relative to the evidence
directory and are bound by exact byte length and lowercase SHA-256.

## Gate levels

| Level | Required evidence |
|---|---|
| `Simulation` | At least 10,000 simulated units, all software fault scenarios, zero repeated non-idempotent actions, complete terminal evidence |
| `Physical` | Simulation requirements plus at least 1,000 physical units and independent emergency-stop and safety-door evidence |
| `Pilot` | Physical requirements plus at least two qualified stations, 168 continuous hours, and 24 hours of enterprise-network isolation |

Every level requires 100% terminal traceability and explicit proof that rollback
keeps old records readable, recovers unfinished execution, and rejects recipe or
test-plan version mismatches.

Physical safety scenarios must declare the
`IndependentSafetyController` execution boundary and prove that platform
availability was not required for the safety action.

The verifier checks evidence integrity and minimum claims. It does not generate
qualification evidence, control safety equipment, or infer a pass from automated
test counts. Safety evidence must originate from the independently acting safety
controller and the physical validation procedure.
