# Whisper ONNX compute benchmark (measured)

- Machine: Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12
- ONNX Runtime: 1.31.0.0, CPU execution provider, app session options
- Generated: 2026-10-09 09:06 UTC; load average at start 1.12 1.75 1.87, at end 1.47 1.70 1.83
- 7 measured runs after one discarded warm-up run; 3 s synthetic audio; 12 teacher-forced tokens per run
- Random-weight exports (marked *random*) have the published Whisper dimensions; their output is meaningless but the arithmetic per step is the same as a trained checkpoint of that shape.
- Peak RSS is the whole benchmark process (runtime + one model), measured in a separate process per row.

| Model | Quant | Window s | Threads | Encoder/decoder MB | Init ms | Log-mel p50 | Encoder p50 / p95 | SOT step p50 | Prompt step p50 | Per token p50 / p95 | Total p50 / p95 | Peak RSS MB |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| shape-tiny *random* | none | 30 | 1 | 36 / 108 | 806 | 25.0 | 665 / 736 | 34.0 | 33.1 | 33.3 / 38.6 | 1150 / 1238 | 551 |
| shape-tiny *random* | none | 30 | 2 | 36 / 108 | 718 | 35.5 | 407 / 424 | 22.4 | 24.9 | 21.5 / 25.3 | 744 / 771 | 554 |
| shape-tiny *random* | none | 30 | 4 | 36 / 108 | 838 | 34.3 | 274 / 293 | 18.8 | 21.7 | 19.8 / 23.9 | 583 / 621 | 550 |
| shape-base *random* | none | 30 | 1 | 91 / 186 | 1347 | 36.3 | 1650 / 1860 | 74.4 | 77.4 | 72.1 / 81.8 | 2722 / 2986 | 821 |
| shape-base *random* | none | 30 | 2 | 91 / 186 | 1567 | 37.1 | 949 / 1019 | 45.7 | 49.7 | 43.4 / 51.3 | 1602 / 1732 | 825 |
| shape-base *random* | none | 30 | 4 | 91 / 186 | 1519 | 26.2 | 598 / 666 | 32.0 | 35.9 | 32.6 / 43.7 | 1073 / 1248 | 825 |
| shape-base-ctx400 *random* | none | 8 | 1 | 88 / 186 | 1530 | 15.5 | 259 / 354 | 32.4 | 36.0 | 34.8 / 43.8 | 777 / 902 | 596 |
| shape-base-ctx400 *random* | none | 8 | 2 | 88 / 186 | 1241 | 12.6 | 187 / 211 | 24.2 | 26.3 | 24.8 / 29.0 | 559 / 639 | 588 |
| shape-base-ctx400 *random* | none | 8 | 4 | 88 / 186 | 1229 | 18.7 | 135 / 155 | 19.8 | 23.3 | 21.7 / 27.2 | 455 / 636 | 589 |
| shape-base-ctx400-int8 *random* | int8 | 8 | 1 | 25 / 149 | 890 | 17.4 | 143 / 173 | 17.7 | 18.7 | 17.1 / 19.8 | 433 / 467 | 353 |
| shape-base-ctx400-int8 *random* | int8 | 8 | 2 | 25 / 149 | 939 | 20.7 | 102 / 114 | 14.9 | 17.5 | 15.9 / 18.9 | 357 / 398 | 353 |
| shape-base-ctx400-int8 *random* | int8 | 8 | 4 | 25 / 149 | 848 | 19.7 | 90 / 121 | 15.1 | 17.7 | 17.1 / 21.6 | 358 / 402 | 354 |

Total = log-mel + encoder + SOT step + prompt step + 12 token steps of one run (one recognition of a 12-token transcript without a second-language decode).
