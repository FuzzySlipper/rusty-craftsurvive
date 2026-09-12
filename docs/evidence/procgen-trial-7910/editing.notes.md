# Editing lane notes

## Actual commands and reads

- Read `/home/dev/rusty-craftsurvive/docs/evidence/procgen-trial-7910/bank.json` with `jq .`.
- Read candidate metadata from the same bank with `jq -c '.entries[] | {id, identity, motif:.candidate.motif, seed:.candidate.seed, rooms:.candidate.rooms, routes:.candidate.routes, switchEnabled:.candidate.switchEnabled, recoveryEnabled:.candidate.recoveryEnabled, previewOpening:.candidate.previewOpening}'`.
- Ran `dotnet run --project /home/dev/rusty-craftsurvive/src/CraftSurvive.Procgen.Tool -c Release --no-build -- trial-inspect --bank /home/dev/rusty-craftsurvive/docs/evidence/procgen-trial-7910/bank.json` from `/home/dev/rusty-craftsurvive`.

## Unverified assumptions

- The configured model identifier was not exposed to this lane, so the submission records `unknown`.
- The spatial preferences use room bounds and route geometry as qualitative proxies; they are not measurements of fun, comfort, visibility, or play quality.
- The submission leaves operations empty because the selected candidates carry the motif flags in the retained bank; parent evaluation remains authoritative.

## Missing telemetry

- No first-person, browser, GPU, collision, occlusion, sightline, traversal-time, or interactive playtest telemetry was available.
- No direct player-fun or usability measurement was available.
