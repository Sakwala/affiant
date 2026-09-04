# Affiant reach scorecard

## Purpose

Momentum is a fact a stranger can check, not a claim the maintainer makes. Every number below
counts non-owner activity only, and sits next to the exact command or URL that reproduces it —
run it yourself and you get the same number. Snapshots are appended weekly at the bottom of this
file and are never rewritten; if a past number turns out wrong, a later snapshot corrects it in
the open, it doesn't get edited away.

## The maintainer's declared attention

The maintainer has declared **60% of a working week** on Affiant from 2026-09-04, rising to
**79% for the four weeks in which the TypeScript core is built** — calendar weeks 5–8 from
2026-09-04, i.e. **2026-10-02 → 2026-10-29**. The public roadmap carries no dates; the pace at
which its *Now* items move to *Recently shipped* is the check on this figure.

## Surfaces and criteria

Only non-owner activity counts. "Owner" means the GitHub account `seevali`.

| Surface | What counts | How to measure |
|---|---|---|
| GitHub stars | Stars on `Sakwala/affiant`, `Sakwala/affiant-ts`, `Sakwala/affiant-protocol` | `gh api repos/Sakwala/<repo> --jq '{stargazers_count,forks_count,subscribers_count}'` |
| Forks | Forks on any of the three repos | same command, `.forks_count` |
| Watchers | Subscribers on any of the three repos | same command, `.subscribers_count` |
| Dependents | Other repositories GitHub's dependency graph shows depending on a package | `https://github.com/Sakwala/<repo>/network/dependents` (page) |
| Non-owner issues | Issues opened by anyone other than `seevali` | `gh api "repos/Sakwala/<repo>/issues?state=all&per_page=100" --jq '[.[] \| select(.user.login != "seevali")] \| length'` — GitHub's issues endpoint returns issues and pull requests together, so this is a combined count (see note below the table) |
| Non-owner pull requests | Pull requests opened by anyone other than `seevali` | same endpoint as above — not separable from issues without a second, PR-only query, which will be added once the combined count is non-zero |
| Non-owner Discussion posts | Discussion threads or replies authored by anyone other than `seevali` | `gh api graphql -f query='{ repository(owner:"Sakwala", name:"<repo>"){ discussions(first:100){ totalCount nodes{ author{login} } } } }'`, filtered to `author.login != "seevali"` |
| NuGet downloads | Total downloads per package (CI/test installs are not separable from real ones on NuGet — the total is reported honestly as a total, not as a proxy for adoption) | `curl -s "https://azuresearch-usnc.nuget.org/query?q=packageid:<id>&prerelease=true&semVerLevel=2.0.0" \| jq '.data[0].totalDownloads'` for each of the ten package IDs |
| npm downloads | Weekly download count once a package is live | none published yet — see snapshot below |
| Talk / podcast acceptances | Accepted conference talks or podcast appearances about Affiant | tracked manually against submission and acceptance emails; no API exists for this |
| External-issue replies | Replies from accounts other than `seevali` in the four issues where the problem Affiant solves was written down in public, before Affiant existed | `gh api --paginate repos/<owner>/<repo>/issues/<n>/comments --jq '.[] \| select(.user.login != "seevali") \| .id'` piped to `wc -l`, summed across pages, for `openai/openai-agents-js#1097`, `mastra-ai/mastra#20757`, `vercel/ai#19979`, `vercel/ai#13215` |

## Gates

Two dated checkpoints, set before any results were visible, against a stated zero baseline.

- **Baseline — 2026-08-30:** zero on every GitHub surface (stars, forks, dependents,
  non-owner issues, PRs, Discussions); NuGet downloads were non-zero from the 2026-08-23
  publish but cannot be separated from CI restores.
- **"Heard" — 2026-11-23** (three months after the first public release): a launch write-up
  published, at least two talk or podcast submissions made, a quickstart guide live, and any
  nontrivial engagement from someone who isn't the maintainer.
- **"Wanted" — targets by 2027-02-23** (six months after the first public release), against the
  2026-08-30 zero baseline:
  - ≥ 1 external project depending on an Affiant package (GitHub dependents ≠ 0)
  - ≥ 3 substantive non-owner issues, pull requests, or Discussion posts across
    `Sakwala/affiant` + `Sakwala/affiant-ts`
  - ≥ 25 stars, combined across all Affiant repositories
  - non-CI downloads distinguishable from zero on at least 2 of NuGet, npm, GitHub
  - ≥ 1 talk or podcast acceptance

## Snapshots

### Snapshot #1 — 2026-09-04

| Date | Stars (affiant) | Stars (affiant-ts) | Stars (protocol) | Forks | Watchers | Dependents | Non-owner issues | Non-owner PRs | Non-owner discussions | NuGet total downloads | npm | Talks | External-issue replies | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2026-09-04 | 0 | 0 | 0 | 0 | 0 | 0 | 0¹ | 0¹ | 0 | 2,285 | not published yet | 0 | 81² | First snapshot. All GitHub surfaces read zero. `vercel/ai#19979` and `vercel/ai#13215` are closed issues. |

¹ GitHub's issues API returns issues and pull requests as one combined list; the combined
non-owner count is 0, reported in both columns until a PR-only query is worth adding.
² Sum of non-owner comments across the four external issues: 4 + 3 + 3 + 71 = 81. The maintainer
has not replied in any of them yet (0 comments from `seevali` in all four).

**NuGet downloads by package (2026-09-04):**

| Package | Total downloads |
|---|---|
| Affiant.Abstractions | 326 |
| Affiant.Core | 321 |
| Affiant.SemanticKernel | 247 |
| Affiant.AgentFramework | 131 |
| Affiant.Transport.SignalR | 251 |
| Affiant.EntityFramework | 246 |
| Affiant.Docket | 257 |
| Affiant.Policies | 254 |
| Affiant.Testing.ComplianceHarness | 128 |
| Affiant.Extensions.AI | 124 |
| **Sum** | **2,285** |

<details>
<summary>Raw command output — snapshot #1, 2026-09-04</summary>

```
$ gh api repos/Sakwala/affiant --jq '{stargazers_count,forks_count,subscribers_count}'
{"forks_count":0,"stargazers_count":0,"subscribers_count":0}

$ gh api repos/Sakwala/affiant-ts --jq '{stargazers_count,forks_count,subscribers_count}'
{"forks_count":0,"stargazers_count":0,"subscribers_count":0}

$ gh api repos/Sakwala/affiant-protocol --jq '{stargazers_count,forks_count,subscribers_count}'
{"forks_count":0,"stargazers_count":0,"subscribers_count":0}

$ gh api "repos/Sakwala/affiant/issues?state=all&per_page=100" --jq '[.[] | select(.user.login != "seevali")] | length'
0   (47 total issues+PRs, all opened by seevali)

$ gh api "repos/Sakwala/affiant-ts/issues?state=all&per_page=100" --jq '[.[] | select(.user.login != "seevali")] | length'
0   (0 total)

$ gh api "repos/Sakwala/affiant-protocol/issues?state=all&per_page=100" --jq '[.[] | select(.user.login != "seevali")] | length'
0   (0 total)

$ gh api graphql -f query='{ repository(owner:"Sakwala", name:"affiant"){ discussions(first:100){ totalCount nodes{ author{login} } } } }'
{"data":{"repository":{"discussions":{"totalCount":0,"nodes":[]}}}}

$ curl -s https://github.com/Sakwala/affiant/network/dependents | grep -oE '[0-9,]+\s+Repositor(y|ies)' | head -2
0 Repositories

$ for id in Affiant.Abstractions Affiant.Core Affiant.SemanticKernel Affiant.AgentFramework \
            Affiant.Transport.SignalR Affiant.EntityFramework Affiant.Docket Affiant.Policies \
            Affiant.Testing.ComplianceHarness Affiant.Extensions.AI; do
    curl -s "https://azuresearch-usnc.nuget.org/query?q=packageid:${id}&prerelease=true&semVerLevel=2.0.0" \
      | jq '.data[0].totalDownloads'
  done
326  321  247  131  251  246  257  254  128  124

$ gh api --paginate repos/openai/openai-agents-js/issues/1097/comments --jq '.[] | select(.user.login != "seevali") | .id' | wc -l
4

$ gh api --paginate repos/mastra-ai/mastra/issues/20757/comments --jq '.[] | select(.user.login != "seevali") | .id' | wc -l
3

$ gh api --paginate repos/vercel/ai/issues/19979/comments --jq '.[] | select(.user.login != "seevali") | .id' | wc -l
3

$ gh api --paginate repos/vercel/ai/issues/13215/comments --jq '.[] | select(.user.login != "seevali") | .id' | wc -l
71
```

The unpaginated form of this call (no `--paginate`) silently caps at GitHub's default
`per_page=30` and previously reported 30 for `vercel/ai#13215` instead of 71 — the error this
snapshot corrects.

</details>

## How to add a snapshot

1. `gh api repos/Sakwala/<repo> --jq '{stargazers_count,forks_count,subscribers_count}'` for
   `affiant`, `affiant-ts`, `affiant-protocol`.
2. `gh api "repos/Sakwala/<repo>/issues?state=all&per_page=100" --jq '[.[] | select(.user.login != "seevali")] | length'` for each of the three repos.
3. `gh api graphql -f query='{ repository(owner:"Sakwala", name:"<repo>"){ discussions(first:100){ totalCount nodes{ author{login} } } } }'` for each repo; count authors that aren't `seevali`.
4. `curl -s https://github.com/Sakwala/<repo>/network/dependents | grep -oE '[0-9,]+\s+Repositor(y|ies)' | head -2` for each repo.
5. Loop the ten NuGet package IDs through `curl -s "https://azuresearch-usnc.nuget.org/query?q=packageid:<id>&prerelease=true&semVerLevel=2.0.0" | jq '.data[0].totalDownloads'` and sum them.
6. Once `@affiant/core` is on npm, record its weekly download count; until then write "not
   published yet".
7. Update talk/podcast acceptances manually from submission and acceptance records.
8. `gh api --paginate repos/<owner>/<repo>/issues/<n>/comments --jq '.[] | select(.user.login != "seevali") | .id'` piped to `wc -l` for each of the four external issues, and sum the four totals. Omitting `--paginate` silently caps the count at GitHub's default `per_page=30`.
9. Append one wide row to the Snapshots table with today's date. Never edit a previous row —
   corrections land as a note in a later snapshot's row.
