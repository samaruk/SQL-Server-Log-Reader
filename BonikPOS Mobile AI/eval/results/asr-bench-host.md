# Whisper ONNX compute benchmark (measured)

- Machine: Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12
- ONNX Runtime: 1.31.0.0, CPU execution provider, app session options
- Generated: 2026-10-09 08:30 UTC; load average at start 1.40 1.81 1.41, at end 1.90 1.81 1.45
- 7 measured runs after one discarded warm-up run; 3 s synthetic audio; 12 teacher-forced tokens per run
- Random-weight exports (marked *random*) have the published Whisper dimensions; their output is meaningless but the arithmetic per step is the same as a trained checkpoint of that shape.
- Peak RSS is the whole benchmark process (runtime + one model), measured in a separate process per row.

| Model | Quant | Window s | Threads | Encoder/decoder MB | Init ms | Log-mel p50 | Encoder p50 / p95 | SOT step p50 | Prompt step p50 | Per token p50 / p95 | Total p50 / p95 | Peak RSS MB |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| shape-tiny *random* | none | 30 | 1 | 36 / 108 | 821 | 30.8 | 657 / 794 | 33.3 | 34.6 | 33.5 / 39.1 | 1176 / 1337 | 548 |
| shape-tiny *random* | none | 30 | 2 | 36 / 108 | 752 | 39.0 | 393 / 423 | 18.9 | 20.9 | 18.8 / 23.0 | 706 / 765 | 550 |
| shape-tiny *random* | none | 30 | 4 | 36 / 108 | 814 | 38.7 | 246 / 258 | 28.4 | 32.3 | 13.8 / 21.4 | 531 / 579 | 552 |
| shape-base *random* | none | 30 | 1 | 91 / 186 | 2280 | 29.1 | 1668 / 2282 | 71.5 | 82.1 | 69.5 / 80.7 | 2756 / 3312 | 825 |
| shape-base *random* | none | 30 | 2 | 91 / 186 | 1303 | 30.5 | 809 / 1556 | 42.3 | 46.5 | 39.2 / 49.2 | 1401 / 2226 | 829 |
| shape-base *random* | none | 30 | 4 | 91 / 186 | 1267 | 34.2 | 482 / 848 | 46.1 | 45.3 | 22.6 / 29.3 | 897 / 1321 | 823 |
| shape-base-ctx400 *random* | none | 8 | 1 | 88 / 186 | 1297 | 13.6 | 267 / 303 | 34.1 | 32.2 | 32.0 / 42.4 | 774 / 888 | 580 |
| shape-base-ctx400 *random* | none | 8 | 2 | 88 / 186 | 1318 | 19.1 | 171 / 232 | 21.6 | 21.6 | 21.2 / 29.1 | 555 / 568 | 588 |
| shape-base-ctx400 *random* | none | 8 | 4 | 88 / 186 | 1384 | 13.9 | 137 / 200 | 29.3 | 37.0 | 15.3 / 26.1 | 430 / 555 | 585 |
| shape-base-ctx400-int8 *random* | int8 | 8 | 1 | 25 / 149 | 1083 | 21.1 | 138 / 180 | 15.1 | 17.9 | 16.4 / 21.2 | 424 / 452 | 353 |
| shape-base-ctx400-int8 *random* | int8 | 8 | 2 | 25 / 149 | 849 | 13.7 | 80 / 97 | 11.7 | 12.0 | 12.0 / 15.1 | 277 / 322 | 353 |
| shape-base-ctx400-int8 *random* | int8 | 8 | 4 | 25 / 149 | 895 | 18.6 | 95 / 126 | 17.3 | 23.3 | 11.0 / 21.3 | 318 / 343 | 353 |

Total = log-mel + encoder + SOT step + prompt step + 12 token steps of one run (one recognition of a 12-token transcript without a second-language decode).
