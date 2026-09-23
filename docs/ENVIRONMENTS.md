# ENVIRONMENTS

Reference. Read when you need URLs or ports. All agent traffic uses the public FQDNs below —
never localhost, never container names, never host ports directly. TLS terminates on the external
reverse proxy; every FQDN is https on 443.

Rule: an agent dials only its own environment's FQDNs (plus prod, for PA). Cross-environment
dials give false results: a dev1 browser hitting a qa FQDN tests qa's build, not dev1's.

## dev1 (DEV, worktree dev1)

| node | public FQDN (dial this) | host port (ops only) |
|---|---|---|
| iris-a | https://dev1-iris-a.luit.ink | 10081 |
| iris-b | https://dev1-iris-b.luit.ink | 10082 |
| lemmy | https://dev1-lemmy.luit.ink | 10091 |
| mastodon | https://dev1-mastodon.luit.ink | 10092 |

## dev2 (DEV, worktree dev2)

| node | public FQDN (dial this) | host port (ops only) |
|---|---|---|
| iris-a | https://dev2-iris-a.luit.ink | 20081 |
| iris-b | https://dev2-iris-b.luit.ink | 20082 |
| lemmy | https://dev2-lemmy.luit.ink | 20091 |
| mastodon | https://dev2-mastodon.luit.ink | 20092 |

## qa (QA, worktree qa)

| node | public FQDN (dial this) | host port (ops only) |
|---|---|---|
| iris-a | https://qa-iris-a.luit.ink | 30081 |
| iris-b | https://qa-iris-b.luit.ink | 30082 |
| lemmy | https://qa-lemmy.luit.ink | 30091 |
| mastodon | https://qa-mastodon.luit.ink | 30092 |

## prod (PA inspection only — read-only, never deploy)

| node | public FQDN |
|---|---|
| iris | https://iris.luit.ink |
| lemmy | https://lemmy.luit.ink |
| mastodon | https://mastodon.luit.ink |

Built from root's active branch (LOOP-CONFIG). Any agent may dial prod read-only; only PA does so as part of its turn.

## Environment binding

| role | environment |
|---|---|
| DEV | dev1 or dev2 — the one matching its claimed worktree. Never the other dev env. |
| QA | qa. Live verification counts only on the qa stack. |
| PA | prod, or any unused dev/qa environment for exploration. Check both .state files first; use an env whose worktree is unclaimed. Never use the env of the worktree you are not holding. |

## Stack ops

Stacks are built from worktrees, never from root. The compose file builds with
`context: ${REPO_ROOT}`, and each env's `.env` sets `REPO_ROOT` to the owning worktree
(dev1 env -> `.worktrees/dev1`, qa env -> `.worktrees/qa`). Run from root:

```
docker compose -f environments/stack/docker-compose.yml --env-file environments/<env>/.env -p <env> up -d --build
```

`<env>` is `dev1`, `dev2`, or `qa`.

You may build and deploy **only your bound environment's stack** (binding table above).
Deploy means exactly this one command, from root, against your env's `.env` — nothing else:

```
docker compose -f environments/stack/docker-compose.yml --env-file environments/<env>/.env -p <env> up -d --build
```

Never run compose against another agent's env, another env's `.env` file, or with a different
`-p` project name. Never `down`, `rm`, `restart -f`, or prune containers or images of any env.
If `up -d --build` fails twice, `BLOCKED: stack <env> build failed` in your .state file, stop.

Rebuild after every merge you ship so the stack matches `<active>`.
Health: `https://<FQDN_IRIS_A>/ap/v1/health`.
