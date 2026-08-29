# Test-count history

> Extracted from the historical changelog. This is the per-slice test tally for `dotnet test Iris.slnx`.

| Slice | Total | Core | Client | Testing | Server |
|---|---:|---:|---:|---:|---:|
| Phase 0 close | 7 | — | — | 4 | 3 |
| Phase 2 close | 223 | — | 88 | 4 | — |
| Phase 3 close | 249 | 130 | 88 | 4 | 27 |
| Phase 4: inbound validation | 287 | 135 | 88 | 4 | 60 |
| Phase 4: Follow→Accept loop | 288 | 135 | 88 | 4 | 61 |
| Phase 4: Accept/Reject handlers | 292 | 135 | 88 | 4 | 65 |
| Phase 4: per-actor delivery signing | 296 | 135 | 88 | 4 | 69 |
| Phase 4: remote actor-doc fetch | 300 | 135 | 88 | 4 | 73 |
| Phase 4: WebFinger account resolution | 308 | 135 | 88 | 4 | 81 |
| Phase 4: paged local collections | 321 | 135 | 88 | 4 | 94 |
| Phase 4: remote collection fetch | 330 | 135 | 88 | 4 | 103 |
| Phase 4: Announce handler | 335 | 135 | 88 | 4 | 108 |
| Phase 4: Reject full-loop test | 336 | 135 | 88 | 4 | 109 |
| Phase 5: community store + membership | 349 | 135 | 88 | 4 | 122 |
| Phase 5: community endpoints | 356 | 135 | 88 | 4 | 129 |
| Phase 5: community feed | 362 | 135 | 90 | 4 | 133 |
| Phase 5: community following | 385 | 135 | 90 | 4 | 156 |
| Phase 5: search + capabilities | 394 | 135 | 90 | 4 | 165 |
| Phase 5: community collections | 401 | 135 | 90 | 4 | 172 |
| Phase 6: server proxy endpoint | 411 | 135 | 90 | 4 | 177 |
| Phase 6: client ProxyFallbackHandler | 417 | 135 | 96 | 4 | 177 |
| Phase 7: Client.Extensions package | 431 | 135 | 96 | 4 | 177 (+19 Client.Extensions) |
| Phase 7: SampleServer app | 440 | 135 | 96 | 4 | 177 (+19 Client.Extensions, +9 SampleServer) |
| Phase 7: SampleBlazorClient + E2E + collection fix | 444 | 135 | 96 | 4 | 177 (+19 Client.Extensions, +9 SampleServer, +4 SampleBlazorClient) |
| Phase 8: Docker composition + UseKestrel fix | 444 | 135 | 96 | 4 | 177 (unchanged) |
| Phase 10 Slice 1.1 | 449 | 140 | 96 | 4 | 177 |
| Phase 10 Slice 1.2 | 460 | 151 | 96 | 4 | 177 |
| Phase 10 Slice 1.3 | 466 | 157 | 96 | 4 | 177 |
| Phase 10 Slice 1.4 | 466 | 157 | 96 | 4 | 177 |
| Phase 10 Slice 1.5 | 466 | 157 | 96 | 4 | 177 |
| Phase 10 Slice 1.6 | 466 | 157 | 96 | 4 | 177 |
| Phase 10 Slice 1.7 | 478 | 157 | 96 | 4 | 177 |
| Phase 10 Slice 1.7b | 478 | 157 | 96 | 4 | 177 |
| Phase 11 Slice 11.1 | 482 | 157 | 96 | 4 | 181 |
| Phase 11 Slice 11.2 | 482 | 157 | 96 | 4 | 181 |
| Phase 11 Slice 11.3 | 486 | 157 | 96 | 8 | 181 |
| Phase 11 Slice 11.4 | 490 | 157 | 99 | 4 | 181 |
| Phase 11 Slice 11.5 | 491 | 157 | 102 | 4 | 181 |
| Phase 11 Slice 11.6 | 500 | 157 | 102 | 12 | 189 |
| Phase 11 Slice 11.7 | 507 | 157 | 102 | 12 | 196 |
| Phase 11 Slice 11.8 | 513 | 159 | 102 | 12 | 199 |
| Phase 11 Slice 11.9 | 524 | 159 | 102 | 12 | 210 |
| Phase 11 Slice 11.10 | 536 | 159 | 102 | 12 | 222 |
| Phase 12 Slice 12.2 | 542 | 159 | 102 | 12 | 228 |
| Phase 12 Slice 12.3 | 557 | 159 | 102 | 12 | 247 |
| Phase 12 Slice 12.4 | 581 | 179 | 102 | 12 | 266 |
| Phase 12 Slice 12.5 | 602 | 179 | 102 | 12 | 278 |
| Phase 12 Slice 12.6 | 611 | 179 | 102 | 12 | 287 |
| Phase 12 Slice 12.7 | 624 | 179 | 102 | 12 | 300 |
| Phase 12 Slice 12.8 | 653 | 179 | 102 | 12 | 329 |
| Phase 12 Slice 12.9 | 689 | 195 | 102 | 12 | 354 |
| Phase 12 Slice 12.10 | 708 | 195 | 102 | 12 | 356 |
| Phase 12 Slice 12.11 | 722 | 195 | 102 | 12 | 370 |
| Phase 12 Slice 12.12 | 730 | 195 | 102 | 12 | 378 |

## Note

These totals were recorded as the suite evolved. The table is the historical record; the live project should still use the current test run and project status as the source of truth for any new build result.
