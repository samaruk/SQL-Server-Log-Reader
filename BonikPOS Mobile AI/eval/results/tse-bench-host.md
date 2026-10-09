Measured on Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12, ONNX Runtime 1.31.0.0, CPU execution provider, 20 s of synthetic input per row (after warm-up). One frame = 256 new samples = 16 ms of audio; real-time factor = mean time per frame / 16 ms.

| Model | SHA-256 | Size | Mode | Threads | Frames | Mean ms | p50 ms | p95 ms | Max ms | RTF |
|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|
| gtcrn_simple.onnx | `b4718df6228e` | 523 KB | model only (1 frame / call) | 1 | 1250 | 2.007 | 1.760 | 3.181 | 5.852 | 0.1255 |
| gtcrn_simple.onnx | `b4718df6228e` | 523 KB | model only (1 frame / call) | 2 | 1250 | 2.594 | 2.249 | 3.787 | 21.018 | 0.1621 |
| ulunas_stream_simple.onnx | `f2e804d54d6a` | 770 KB | model only (1 frame / call) | 1 | 1250 | 1.955 | 1.889 | 2.337 | 4.219 | 0.1222 |
| ulunas_stream_simple.onnx | `f2e804d54d6a` | 770 KB | model only (1 frame / call) | 2 | 1250 | 3.092 | 2.858 | 4.700 | 9.507 | 0.1933 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | model only (1 frame / call) | 1 | 1250 | 1.151 | 1.027 | 1.802 | 6.568 | 0.0719 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 1250 | 1.587 | 1.429 | 2.436 | 9.419 | 0.0992 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | model only (1 frame / call) | 2 | 1250 | 1.885 | 1.647 | 2.762 | 10.481 | 0.1178 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 1250 | 2.330 | 2.023 | 3.513 | 5.797 | 0.1456 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | model only (1 frame / call) | 1 | 1250 | 1.593 | 1.330 | 2.337 | 5.935 | 0.0996 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 1250 | 1.714 | 1.524 | 2.664 | 9.673 | 0.1071 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | model only (1 frame / call) | 2 | 1250 | 2.305 | 2.089 | 3.551 | 15.810 | 0.1441 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 1250 | 2.476 | 2.243 | 3.737 | 6.823 | 0.1548 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | model only (1 frame / call) | 1 | 1250 | 1.771 | 1.567 | 2.733 | 14.801 | 0.1107 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 1250 | 1.959 | 1.752 | 3.247 | 5.650 | 0.1224 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | model only (1 frame / call) | 2 | 1250 | 3.221 | 2.871 | 4.598 | 12.881 | 0.2013 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 1250 | 2.940 | 2.778 | 4.381 | 7.370 | 0.1838 |
