# Quality Gate sample plugin

This process-command plugin demonstrates the production result boundary used by
OpenLineOps. `Evaluate` accepts JSON with `resultJudgement` set to `Passed`,
`Failed`, or `Aborted` and returns `ExecutionStatus=Completed` with the selected
business judgement. A failed product judgement is therefore not reported as a
plugin execution failure.

Example input:

```json
{
  "resultJudgement": "Failed",
  "detail": "Measured voltage exceeded the upper limit."
}
```
