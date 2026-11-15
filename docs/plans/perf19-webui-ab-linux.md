# PERF.19 first half, measured: web UI before and after per-frame updates (Linux)

Date: 2026-11-15. ROADMAP PERF.19, first half (`e39ab2b`: job updates applied once per animation frame). Method and
numbers as in `perf-baseline-how.md` (`scripts/perf-webui.mjs`), with a shorter window: 30 s after a 10 s warm-up.

**The machine was not quiet** (load average 7 to 17: two VirtualBox VMs and another project's test run on the same
28 cores). So the two builds were measured **alternately under the same load**: baseline `perf-baseline-a2`
(`ff949a4`), then `main` (`e0b5713`), then both again. Each build and its web UI was built from its own commit with
`scripts/perf-seed.sh build --skip-window`. Absolute numbers are noisy; read the comparison, and only where both rounds
agree.

**Caveat on "20 active":** the baseline commit's seeding ran only 8 downloads in that case, `main`'s ran 16 (the seeding
script's raise of `maxConnectionsPerHost` above 8 came after the baseline commit; all downloads come from 127.0.0.1). So the 20-active rows are not the same work; `main` carried twice the
messages there. The 8-active rows are the like-for-like comparison.

## Steady state, per second (round 1 / round 2)

| Jobs | Running | Build | Script ms | Main-thread busy ms | WS messages | Heap after GC MB |
| ---: | ---: | --- | ---: | ---: | ---: | ---: |
| 500 | 8 | baseline | 131 / 180 | 269 / 369 | 13.9 / 14.0 | 14.4 / 14.4 |
| 500 | 8 | main | 184 / 110 | 388 / 227 | 13.8 / 13.8 | 16.7 / 14.5 |
| 5,000 | 8 | baseline | 902 / 898 | 1,000 / 1,000 | 13.0 / 11.3 | 268 / 197 |
| 5,000 | 8 | main | 825 / 833 | 983 / 992 | 13.7 / 14.0 | 136 / 136 |
| 5,000 | 8 → 16 | baseline (8) | 900 / 901 | 1,000 / 1,000 | 12.0 / 12.1 | 180 / 238 |
| 5,000 | 16 | main | 877 / 864 | 997 / 997 | 27.9 / 27.7 | 136 / 115 |

## Load, from the sign-in Enter key (round 1 / round 2)

| Jobs | Running | Build | Usable ms | Long tasks: count / total ms |
| ---: | ---: | --- | ---: | --- |
| 5,000 | 8 | baseline | 1,191 / 1,635 | 50 / 6,208 and 137 / 20,701 |
| 5,000 | 8 | main | 1,328 / 1,469 | 31 / 4,478 and 24 / 4,679 |

Long tasks here are counted over the whole load and the seconds after it, while job events already arrive.

## What it says

- **5,000 jobs, 8 running:** the baseline page took in 11 to 13 of the about 14 messages a second the server sends, so
  it fell behind (as the A2 baseline found). `main` takes in all of them (13.7, 14.0) with 8 to 9% less script time,
  and its main thread is no longer pinned at exactly 1,000 ms per second. Live heap after a collection halves
  (about 136 MB against 197 to 268 MB): the backlog of unapplied updates is gone.
- **5,000 jobs, 16 running (`main` only):** twice the messages (28 a second) are all taken in, with less script time
  than the baseline spent on 8. The page still keeps its main thread about 99% busy.
- **During the load,** the long tasks after the list appears drop from 50 to 137 (6 to 21 s in total) to 24 to 31
  (about 4.5 s). Time to a usable list does not change: that is the first render of 125,000 elements.
- **500 jobs:** no difference that rises above the noise (the two rounds disagree in both builds).

So the first half does what it was for: the page keeps up with the event stream and stops piling up work. It does not
make a 5,000-job page cheap: the main thread is still about 99% busy, because the list is not virtualised (25 elements
per job). That is PERF.19's second half (a windowed list), not started.

Not measured here: other browsers, a real screen's frame timing, phones. Raw CSV and tables were kept in the session's
scratch folder only.
