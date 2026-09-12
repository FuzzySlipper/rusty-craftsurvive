# Selection notes

## Actual commands and reads

- Read `/home/dev/rusty-craftsurvive/docs/evidence/procgen-trial-7910/bank.json` with `jq . /home/dev/rusty-craftsurvive/docs/evidence/procgen-trial-7910/bank.json`.
- Ran `dotnet run --project /home/dev/rusty-craftsurvive/src/CraftSurvive.Procgen.Tool -c Release --no-build -- trial-inspect --bank /home/dev/rusty-craftsurvive/docs/evidence/procgen-trial-7910/bank.json` from `/home/dev/rusty-craftsurvive` with login disabled.

## Unverified assumptions

- The configured model was not exposed to this lane, so `model` is recorded as `unknown`.
- The spatial preference is inferred only from retained room coordinates and route layout; it is not a measurement of fun or play quality.
- The selected candidates have no listed operation, so each submission `operation` is the required empty string.

## Missing telemetry

- No runtime, browser, GPU, screenshot, collision, navigation, performance, or player-experience telemetry was available. The permitted inspection command provides bounded state and contract output only.
