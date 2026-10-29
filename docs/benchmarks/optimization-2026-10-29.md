# Runner optimization comparison

Before: `1.0.0+22873302283ffe4caf5f27dd213a7d9c4ebd05b3`. After: `1.0.0+2d00787b6ea0463457be7d9bcd955a08eaa40aa4`.

200 jobs/run, 10 ms body, 2 worker processes with 8 slots each; 20 warm-up jobs, 35 s settling, 30 s idle measurement. Runner effective polling/lease/history settings are identical before and after.

Median and min–max across local samples; ranges are not confidence intervals. Shared development hardware, not production capacity or a universal ranking. Completed SQL requests are RPC + batches, including connection resets; they do not measure all database resources. Business P95 ends before framework finalization; throughput includes durable drain. See the benchmark methodology and raw reports.

| Profile | Engine | Stage | Samples | Jobs/s median (range) | P95 business ms median | Load SQL requests/job | Idle completed SQL requests/s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| responsive | runner | before | 3 | 53.17 (46.70–54.12) | 1550.0 | 20.99 | 8.320 |
| responsive | runner | after | 3 | 61.46 (61.31–73.85) | 1400.0 | 15.24 | 6.592 |
| responsive | hangfire | after | 3 | 105.63 (100.02–106.54) | 753.3 | 28.18 | 5.457 |
| responsive | quartz | after | 3 | 57.16 (47.84–59.84) | 1236.7 | 27.10 | 10.519 |

Raw input files:

- `docs/benchmarks/raw/optimization-before-1-2026-10-29.json`
- `docs/benchmarks/raw/optimization-before-2-2026-10-29.json`
- `docs/benchmarks/raw/optimization-before-3-2026-10-29.json`
- `docs/benchmarks/raw/optimization-after-1-2026-10-29.json`
- `docs/benchmarks/raw/optimization-after-2-2026-10-29.json`
- `docs/benchmarks/raw/optimization-after-3-2026-10-29.json`
