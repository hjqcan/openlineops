# Industrial Device Sessions

This built-in plugin implements four version-1 device-session adapters without
loading a vendor SDK into the host process:

- `scpiTcp`: a serialized, long-lived SCPI TCP session.
- `modbusTcp`: a validated, serialized Modbus TCP client session.
- `tcpLineScanner`: a passive line-oriented TCP scanner session.
- `replay`: verified JSONL command and signal replay.

The adapters implement `IOpenLineOpsDeviceSessionPlugin`. They reject
safety-critical commands because emergency stop, guard-door, safe-torque-off,
and other safety functions belong in a certified safety controller.

## SCPI TCP

```json
{
  "adapter": "scpiTcp",
  "scpiTcp": {
    "host": "192.0.2.10",
    "port": 5025,
    "connectTimeoutMilliseconds": 3000,
    "operationTimeoutMilliseconds": 10000,
    "heartbeatMilliseconds": 1000,
    "lineTerminator": "lf",
    "encoding": "ascii",
    "reconnect": {
      "enabled": true,
      "maxAttempts": 3,
      "initialDelayMilliseconds": 100,
      "maximumDelayMilliseconds": 2000
    }
  },
  "recording": {
    "journalPath": "C:\\OpenLineOpsData\\journals\\station-a.jsonl"
  }
}
```

`Read` treats each Signal ID as an exact SCPI query. `Write` sends
`<SignalId> <CanonicalValue>`. `Invoke` supports:

```json
{"operation":"Query","inputPayload":{"command":"*IDN?"}}
{"operation":"Read","inputPayload":{"command":"MEAS:VOLT?"}}
{"operation":"Write","inputPayload":{"command":"OUTP ON"}}
```

All request/response exchanges are serialized. An idempotent exchange may be
retried after reconnect. Once any bytes of a conditional or non-idempotent
command have been transmitted, an ambiguous disconnect is returned as a
failure and that command is never resent. Reusing the same Command ID with the
same evidence returns the stored result; different operation data, fencing
token, deadline, idempotency class, or safety class is rejected.

A live non-idempotent command requires `recording.journalPath`. Its request is
flushed to the hash-chained journal before transport. On process restart, a
completed request returns its durable response without sending again. A
request that has no response is restored as `RecoveryRequired` with unknown
completion and is never resent automatically. This deliberately favors a
manual equipment-state check over duplicate physical action.

`maxAttempts` is the number of reconnect retries. Zero means unlimited retries.

## Modbus TCP

```json
{
  "adapter": "modbusTcp",
  "modbusTcp": {
    "host": "192.0.2.30",
    "port": 502,
    "unitId": 17,
    "connectTimeoutMilliseconds": 3000,
    "operationTimeoutMilliseconds": 10000,
    "heartbeatMilliseconds": 1000,
    "historyCapacity": 4096,
    "reconnect": {
      "enabled": true,
      "maxAttempts": 3,
      "initialDelayMilliseconds": 100,
      "maximumDelayMilliseconds": 2000
    },
    "signals": [
      {
        "signalId": "fixture.clamped",
        "kind": "coil",
        "address": 10
      },
      {
        "signalId": "fixture.present",
        "kind": "discreteInput",
        "address": 20
      },
      {
        "signalId": "drive.speed",
        "kind": "holdingRegister",
        "address": 100,
        "unit": "rpm"
      },
      {
        "signalId": "drive.temperature",
        "kind": "inputRegister",
        "address": 200,
        "unit": "Cel"
      }
    ]
  },
  "recording": {
    "journalPath": "C:\\OpenLineOpsData\\journals\\station-a.jsonl"
  }
}
```

Addresses are zero-based protocol addresses, not the human-facing
`00001`/`40001` notation. Each stable Signal ID maps to exactly one kind and
address. `Read` supports coils, discrete inputs, holding registers, and input
registers. Bit values are returned as `Boolean`; one 16-bit register is returned
as a non-negative `SignedInteger`. This first adapter revision deliberately does
not apply byte swapping, scaling, signed conversion, or multi-register numeric
encoding. Those transformations belong in an explicit, versioned engineering
mapping rather than an implicit driver convention.

`Write` accepts coils and holding registers only. A single value uses function
05 or 06. Multiple values must belong to one kind and one contiguous address
range; they use function 15 or 16 in one Modbus request. Coil values must be
`Boolean`; holding-register values must be integers from 0 through 65535, and
the request unit must exactly match the configured engineering unit.

The session validates the complete MBAP response header, including transaction
ID, protocol ID zero, declared length, and Unit ID. It also validates function
codes, exception frames, read byte counts and padding, and exact write echoes.
Fragmented TCP frames are reassembled without assuming one socket read equals
one Modbus frame. A malformed response closes the connection and records a
protocol diagnostic. A valid Modbus exception response records a device
diagnostic without corrupting the TCP stream.

Reads and writes declared `Idempotent` may reconnect and retry. Once transmission
of a `Conditional` or `NonIdempotent` write may have started, a disconnect yields
an operation result with `completionState: Unknown` and
`recoveryRequired: true`; the request is never resent. A connection failure
proven to occur before transmission remains an ordinary failure. Non-idempotent writes use
the same durable command journal, cold-start replay, fencing-token, deadline,
and safety-controller rejection rules as SCPI commands.

The automated tests use a loopback protocol simulator and fault injection. They
are not evidence of compatibility with a physical PLC or of hardware-in-the-loop
qualification.

## TCP line scanner

```json
{
  "adapter": "tcpLineScanner",
  "tcpLineScanner": {
    "host": "192.0.2.20",
    "port": 4001,
    "signalId": "scanner.code",
    "connectTimeoutMilliseconds": 3000,
    "heartbeatMilliseconds": 1000,
    "historyCapacity": 4096,
    "encoding": "utf8",
    "reconnect": {
      "enabled": true,
      "maxAttempts": 0,
      "initialDelayMilliseconds": 100,
      "maximumDelayMilliseconds": 5000
    }
  }
}
```

Each received line produces one `Text` sample with the configured stable Signal
ID, UTC source and receive timestamps, a `Good` quality value, and a strictly
increasing session sequence. Active subscriptions remain registered while the
TCP connection is restored. `resumeAfterSequence` replays retained samples
without crossing the requested sequence boundary.

## Record and replay

Recording is enabled by adding an absolute `recording.journalPath` to a live
adapter configuration. Each canonical JSONL event contains its journal
sequence, device sequence or command correlation, timestamps, payload, the
previous event hash, and its own lowercase SHA-256. Existing journals are
verified before append. Command and signal sequence state is recovered for the
same stable Device Instance ID when a live adapter reopens the journal.

```json
{
  "adapter": "replay",
  "replay": {
    "journalPath": "C:\\OpenLineOpsData\\journals\\station-a.jsonl",
    "speedFactor": 1.0,
    "additionalDelayMilliseconds": 0,
    "disconnectAtJournalSequence": 25,
    "disconnectDurationMilliseconds": 500,
    "badQualitySignalIds": ["scanner.code"],
    "heartbeatMilliseconds": 1000,
    "historyCapacity": 4096
  }
}
```

Replay verifies the full hash chain before opening, rejects out-of-order signal
sequences, preserves recorded signal timing and sequence, and can inject a
temporary disconnect, additional latency, or bad signal quality. Recorded
command responses are consumed in their original order. Command ID, fencing,
deadline, safety, and duplicate-evidence checks remain active during replay.
