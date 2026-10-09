Measured on Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12, ONNX Runtime 1.31.0.0, CPU execution provider, 20 s of synthetic input per row (after warm-up). One frame = 256 new samples = 16 ms of audio; real-time factor = mean time per frame / 16 ms.

| Model | SHA-256 | Size | Mode | Threads | Frames | Mean ms | p50 ms | p95 ms | Max ms | RTF |
|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|
| gtcrn_simple.onnx | `b4718df6228e` | 523 KB | model only (1 frame / call) | 1 | 1250 | 2.076 | 1.855 | 3.236 | 7.357 | 0.1298 |
| gtcrn_simple.onnx | `b4718df6228e` | 523 KB | model only (1 frame / call) | 2 | 1250 | 3.192 | 3.345 | 4.151 | 10.354 | 0.1995 |
| ulunas_stream_simple.onnx | `f2e804d54d6a` | 770 KB | model only (1 frame / call) | 1 | 1250 | 2.665 | 2.325 | 4.301 | 9.798 | 0.1666 |
| ulunas_stream_simple.onnx | `f2e804d54d6a` | 770 KB | model only (1 frame / call) | 2 | 1250 | 3.660 | 3.391 | 5.354 | 11.903 | 0.2287 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | model only (1 frame / call) | 1 | 1250 | 1.418 | 1.258 | 2.014 | 7.819 | 0.0887 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 1250 | 1.543 | 1.354 | 2.557 | 12.975 | 0.0964 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | model only (1 frame / call) | 2 | 1250 | 2.026 | 1.750 | 3.814 | 6.862 | 0.1266 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 1250 | 1.604 | 1.387 | 2.381 | 9.625 | 0.1003 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | model only (1 frame / call) | 1 | 1250 | 1.377 | 1.215 | 2.145 | 11.020 | 0.0861 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 1250 | 1.795 | 1.589 | 2.842 | 10.155 | 0.1122 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | model only (1 frame / call) | 2 | 1250 | 2.377 | 2.130 | 3.596 | 36.835 | 0.1486 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 1250 | 2.032 | 1.821 | 2.964 | 7.119 | 0.1270 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | model only (1 frame / call) | 1 | 1250 | 1.985 | 1.737 | 2.956 | 7.513 | 0.1241 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 1250 | 2.156 | 1.895 | 3.467 | 7.411 | 0.1347 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | model only (1 frame / call) | 2 | 1250 | 3.356 | 3.027 | 4.878 | 12.139 | 0.2098 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 1250 | 2.592 | 2.316 | 3.851 | 7.395 | 0.1620 |
