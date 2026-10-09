Measured on Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12, ONNX Runtime 1.31.0.0, CPU execution provider, 10 s of synthetic input per row (after warm-up). One frame = 256 new samples = 16 ms of audio; real-time factor = mean time per frame / 16 ms.

| Model | SHA-256 | Size | Mode | Threads | Frames | Mean ms | p50 ms | p95 ms | Max ms | RTF |
|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|
| gtcrn_simple.onnx | `b4718df6228e` | 523 KB | model only (1 frame / call) | 1 | 625 | 1.901 | 1.752 | 2.743 | 5.382 | 0.1188 |
| gtcrn_simple.onnx | `b4718df6228e` | 523 KB | model only (1 frame / call) | 2 | 625 | 2.202 | 1.916 | 3.391 | 5.913 | 0.1376 |
| ulunas_stream_simple.onnx | `f2e804d54d6a` | 770 KB | model only (1 frame / call) | 1 | 625 | 2.323 | 2.132 | 3.385 | 5.700 | 0.1452 |
| ulunas_stream_simple.onnx | `f2e804d54d6a` | 770 KB | model only (1 frame / call) | 2 | 625 | 4.765 | 3.545 | 12.504 | 49.729 | 0.2978 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | model only (1 frame / call) | 1 | 625 | 1.663 | 1.750 | 2.197 | 7.261 | 0.1039 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 625 | 1.737 | 1.473 | 2.480 | 13.089 | 0.1086 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | model only (1 frame / call) | 2 | 625 | 1.687 | 1.859 | 2.296 | 3.281 | 0.1055 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 625 | 1.574 | 1.446 | 2.325 | 6.029 | 0.0984 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | model only (1 frame / call) | 1 | 625 | 1.295 | 1.239 | 1.681 | 2.488 | 0.0810 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 625 | 1.629 | 1.489 | 2.502 | 3.601 | 0.1018 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | model only (1 frame / call) | 2 | 625 | 1.570 | 1.373 | 2.388 | 3.098 | 0.0981 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 625 | 2.082 | 1.847 | 3.248 | 6.730 | 0.1301 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | model only (1 frame / call) | 1 | 625 | 1.869 | 1.649 | 2.843 | 9.447 | 0.1168 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 625 | 2.008 | 1.852 | 2.982 | 5.542 | 0.1255 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | model only (1 frame / call) | 2 | 625 | 2.028 | 1.936 | 2.899 | 4.122 | 0.1268 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 625 | 2.068 | 1.975 | 2.701 | 5.618 | 0.1293 |
