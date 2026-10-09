#!/usr/bin/env python3
"""Verify, export and benchmark-prepare the pGTCRN streaming model (see pgtcrn.py).

1) --verify-upstream DIR : proves pgtcrn.py reproduces upstream GTCRN. DIR is a checkout/download of
   github.com/Xiaobin-Rong/gtcrn (commit 502ebfa) containing stream/onnx_models/gtcrn_simple.onnx,
   stream/onnx_models/model_trained_on_dns3.tar and stream/test_wavs/mix.wav. The upstream checkpoint is loaded into
   PGTCRN(channels=16, emb_dim=0) with torch.load(weights_only=True) (no code is executed from DIR), then
     a) offline PGTCRN vs StreamPGTCRN frame by frame (PyTorch), and
     b) StreamPGTCRN vs the upstream streaming ONNX run in ONNX Runtime
   are compared on the upstream test file.
2) --export OUT.onnx : exports StreamPGTCRN (opset 17) with inputs mix, spk_emb, conv_cache, tra_cache, inter_cache and
   checks ONNX Runtime against PyTorch frame by frame. Without --checkpoint the weights are RANDOM (seeded): such a
   file is labelled "untrained" in its metadata and may only be used for contract/parity/compute tests.
3) --fixture-dir DIR : writes a C# parity fixture (input audio, embedding, expected output) computed with the same
   streaming STFT/overlap-add as the C# adapter (pgtcrn.stream_stft / stream_istft) and ONNX Runtime.
"""
from __future__ import annotations

import os

os.environ.setdefault("ORT_DISABLE_TELEMETRY", "1")  # keep tooling runs off the network (docs/PRIVACY.md)

import argparse
import hashlib
import json
import pathlib
import sys

import numpy as np
import onnx
import onnxruntime as ort
import soundfile
import torch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from pgtcrn import HOP, N_FFT, PGTCRN, StreamPGTCRN, sqrt_hann, stream_istft, stream_stft  # noqa: E402


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def spec_of(audio: np.ndarray) -> torch.Tensor:
    x = torch.from_numpy(audio.astype(np.float32))
    s = torch.stft(x, N_FFT, HOP, N_FFT, sqrt_hann(), return_complex=True)  # (257,T)
    return torch.view_as_real(s)[None]  # (1,257,T,2)


def run_stream_torch(stream: StreamPGTCRN, spec: torch.Tensor, emb: torch.Tensor | None) -> torch.Tensor:
    conv, tra, inter = stream.zero_state()
    frames = []
    with torch.no_grad():
        for t in range(spec.shape[2]):
            args = (spec[:, :, t:t + 1], emb, conv, tra, inter) if emb is not None else (spec[:, :, t:t + 1], conv, tra, inter)
            y, conv, tra, inter = stream(*args)
            frames.append(y)
    return torch.cat(frames, dim=2)


def run_stream_ort(path: pathlib.Path, spec: np.ndarray, emb: np.ndarray | None, threads: int = 1) -> np.ndarray:
    so = ort.SessionOptions()
    so.intra_op_num_threads = threads
    sess = ort.InferenceSession(str(path), so, providers=["CPUExecutionProvider"])
    inputs = {i.name: i for i in sess.get_inputs()}
    state = {n: np.zeros(inputs[n].shape, np.float32) for n in ("conv_cache", "tra_cache", "inter_cache")}
    out = []
    for t in range(spec.shape[2]):
        feed = {"mix": spec[:, :, t:t + 1].astype(np.float32), **state}
        if emb is not None:
            feed["spk_emb"] = emb.astype(np.float32)
        enh, c, tr, it = sess.run(["enh", "conv_cache_out", "tra_cache_out", "inter_cache_out"], feed)
        state = {"conv_cache": c, "tra_cache": tr, "inter_cache": it}
        out.append(enh)
    return np.concatenate(out, axis=2)


def verify_upstream(root: pathlib.Path) -> dict:
    onnx_path = root / "stream/onnx_models/gtcrn_simple.onnx"
    ckpt_path = root / "stream/onnx_models/model_trained_on_dns3.tar"
    wav_path = root / "stream/test_wavs/mix.wav"
    model = PGTCRN(channels=16, emb_dim=0).eval()
    state = torch.load(ckpt_path, map_location="cpu", weights_only=True)["model"]
    model.load_state_dict(state, strict=True)
    stream = StreamPGTCRN(model).eval()
    audio, sr = soundfile.read(str(wav_path), dtype="float32")
    assert sr == 16000, sr
    audio = audio[: 16000 * 4]
    spec = spec_of(audio)
    with torch.no_grad():
        offline = model(spec)
    streamed = run_stream_torch(stream, spec, None)
    upstream = run_stream_ort(onnx_path, spec.numpy(), None)
    scale = float(offline.abs().max())
    report = {
        "upstream_onnx": {"file": onnx_path.name, "sha256": sha256(onnx_path)},
        "upstream_checkpoint": {"file": ckpt_path.name, "sha256": sha256(ckpt_path)},
        "frames": int(spec.shape[2]),
        "offline_vs_stream_max_abs": float((offline - streamed).abs().max()),
        "stream_vs_upstream_onnx_max_abs": float(np.abs(streamed.numpy() - upstream).max()),
        "output_abs_max": scale,
    }
    report["pass"] = report["offline_vs_stream_max_abs"] < 1e-4 * max(1, scale) and report["stream_vs_upstream_onnx_max_abs"] < 1e-4 * max(1, scale)
    return report


def export(out: pathlib.Path, channels: int, emb_dim: int, seed: int, checkpoint: pathlib.Path | None) -> dict:
    torch.manual_seed(seed)
    model = PGTCRN(channels=channels, emb_dim=emb_dim).eval()
    trained = checkpoint is not None
    smoke = False
    if trained:
        ckpt = torch.load(checkpoint, map_location="cpu", weights_only=True)
        model.load_state_dict(ckpt["model"], strict=True)
        smoke = bool(ckpt.get("smoke_test", False)) if isinstance(ckpt, dict) else False
    else:
        with torch.no_grad():  # random but non-trivial: FiLM and BatchNorm statistics must not be identities
            for name, p in model.named_parameters():
                if name.startswith("film."):
                    p.normal_(0, 0.05)
            for mod in model.modules():
                if isinstance(mod, torch.nn.BatchNorm2d):
                    mod.running_mean.normal_(0, 0.1)
                    mod.running_var.uniform_(0.5, 1.5)
    stream = StreamPGTCRN(model).eval()
    conv, tra, inter = stream.zero_state()
    mix = torch.randn(1, 257, 1, 2)
    emb = torch.nn.functional.normalize(torch.rand(1, emb_dim), dim=-1) if emb_dim > 0 else None
    args = (mix, emb, conv, tra, inter) if emb is not None else (mix, conv, tra, inter)
    names = ["mix"] + (["spk_emb"] if emb is not None else []) + ["conv_cache", "tra_cache", "inter_cache"]
    out.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(stream, args, str(out), input_names=names,
                      output_names=["enh", "conv_cache_out", "tra_cache_out", "inter_cache_out"], opset_version=17, dynamo=False)
    proto = onnx.load(str(out))
    meta = {
        "bonik.model_id": f"pgtcrn-c{channels}" + (f"-e{emb_dim}" if emb_dim else ""),
        "bonik.role": "target-speaker-extraction" if emb_dim else "speech-enhancement",
        "bonik.weights": (f"SMOKE-TEST checkpoint {sha256(checkpoint)} (code check only, NOT trained)" if smoke
                          else f"trained:{sha256(checkpoint)}" if trained
                          else f"RANDOM-UNTRAINED seed={seed} (contract/parity/compute tests only)"),
        "bonik.architecture": "pGTCRN = GTCRN (MIT, Xiaobin-Rong/gtcrn@502ebfa) + FiLM speaker conditioning",
        "bonik.n_fft": str(N_FFT), "bonik.hop": str(HOP), "bonik.window": "sqrt_hann_periodic", "bonik.sample_rate": "16000",
    }
    del proto.metadata_props[:]
    for k, v in meta.items():
        e = proto.metadata_props.add()
        e.key, e.value = k, v
    proto.producer_name = "bonikpos-voicesearch-export"
    onnx.checker.check_model(proto)
    onnx.save(proto, str(out))

    # ORT vs PyTorch, frame by frame on 2 s of noise-like spectra
    rng = np.random.default_rng(seed)
    audio = (rng.standard_normal(32000) * 0.05).astype(np.float32)
    spec = spec_of(audio)
    torch_out = run_stream_torch(stream, spec, emb)
    ort_out = run_stream_ort(out, spec.numpy(), emb.numpy() if emb is not None else None)
    with torch.no_grad():
        offline = model(spec, emb) if emb is not None else model(spec)
    params = sum(p.numel() for p in model.parameters() if p.requires_grad)
    return {
        "file": str(out), "sha256": sha256(out), "bytes": out.stat().st_size, "trainable_params": int(params),
        "ops": sorted({n.op_type for n in proto.graph.node}),
        "inputs": [(i.name, [d.dim_value for d in i.type.tensor_type.shape.dim]) for i in proto.graph.input],
        "offline_vs_stream_max_abs": float((offline - torch_out).abs().max()),
        "ort_vs_torch_stream_max_abs": float(np.abs(ort_out - torch_out.numpy()).max()),
        "metadata": meta,
        "smoke_checkpoint": smoke,
    }


def write_fixture(model_path: pathlib.Path, out_dir: pathlib.Path, audio_path: pathlib.Path, emb_dim: int, seed: int) -> dict:
    audio, sr = soundfile.read(str(audio_path), dtype="float32")
    assert sr == 16000
    audio = audio[: (len(audio) // HOP) * HOP][: 16000 * 2 // HOP * HOP]
    rng = np.random.default_rng(seed)
    emb = rng.random((1, emb_dim)).astype(np.float32)
    emb /= np.linalg.norm(emb)
    spectra = stream_stft(audio)                                     # (frames,257) complex128
    spec = np.stack([spectra.real, spectra.imag], axis=-1).T[None].transpose(0, 2, 1, 3)  # placeholder, fixed below
    spec = np.zeros((1, 257, len(spectra), 2), np.float32)
    spec[0, :, :, 0] = spectra.real.T
    spec[0, :, :, 1] = spectra.imag.T
    enh = run_stream_ort(model_path, spec, emb)                       # (1,257,frames,2)
    enh_c = enh[0, :, :, 0].T.astype(np.float64) + 1j * enh[0, :, :, 1].T.astype(np.float64)
    expected = stream_istft(enh_c).astype(np.float32)
    out_dir.mkdir(parents=True, exist_ok=True)
    audio.astype("<f4").tofile(out_dir / "pgtcrn-fixture-input.f32")
    emb.astype("<f4").tofile(out_dir / "pgtcrn-fixture-embedding.f32")
    expected.astype("<f4").tofile(out_dir / "pgtcrn-fixture-expected.f32")
    return {"input_samples": int(len(audio)), "frames": int(len(spectra)), "embedding_dim": emb_dim,
            "expected_samples": int(len(expected)), "delay_samples": N_FFT - HOP, "input_source": audio_path.name}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--verify-upstream", type=pathlib.Path)
    ap.add_argument("--export", type=pathlib.Path)
    ap.add_argument("--channels", type=int, default=16)
    ap.add_argument("--emb-dim", type=int, default=256)
    ap.add_argument("--seed", type=int, default=1234)
    ap.add_argument("--checkpoint", type=pathlib.Path)
    ap.add_argument("--fixture-dir", type=pathlib.Path)
    ap.add_argument("--fixture-audio", type=pathlib.Path)
    ap.add_argument("--register", type=pathlib.Path, help="models/models.json to register the exported TRAINED model in")
    ap.add_argument("--model-id", default="pgtcrn-owner-tse")
    ap.add_argument("--model-version", default="1")
    ap.add_argument("--license", help="verified licence of the trained weights (required with --register)")
    ap.add_argument("--source", help="training data / recipe description (required with --register)")
    ap.add_argument("--encoder-id", default="ge2e-resemblyzer", help="speaker encoder whose embeddings the model was trained on")
    args = ap.parse_args()
    if args.register and (args.checkpoint is None or not args.license or not args.source):
        print("--register needs --checkpoint (trained weights), --license and --source; random weights are never registered.", file=sys.stderr)
        return 2
    report: dict = {}
    ok = True
    if args.verify_upstream:
        report["verify_upstream"] = r = verify_upstream(args.verify_upstream)
        ok &= r["pass"]
    if args.export:
        report["export"] = r = export(args.export, args.channels, args.emb_dim, args.seed, args.checkpoint)
        ok &= r["offline_vs_stream_max_abs"] < 1e-4 and r["ort_vs_torch_stream_max_abs"] < 1e-4
        if args.fixture_dir:
            report["fixture"] = write_fixture(args.export, args.fixture_dir, args.fixture_audio, args.emb_dim, args.seed)
        if args.register and report["export"]["smoke_checkpoint"]:
            print("Refusing to register a smoke-test checkpoint.", file=sys.stderr)
            return 2
        if args.register and ok:
            sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
            from register_model import register
            rel = args.export.resolve().relative_to(args.register.resolve().parent).as_posix()
            register(args.register, {
                "id": args.model_id, "version": args.model_version, "role": "TargetSpeakerExtraction",
                "format": "streaming-tse-stft-onnx/1", "files": {"model": rel}, "license": args.license, "source": args.source,
                "options": {"embedding_dim": args.emb_dim, "required_encoder_id": args.encoder_id, "n_fft": N_FFT, "hop": HOP,
                            "window": "sqrt_hann_periodic", "spec_input": "mix", "spec_output": "enh", "embedding_input": "spk_emb"},
            })
            report["registered_in"] = str(args.register)
    print(json.dumps(report, indent=2))
    if not ok:
        print("VALIDATION FAILED", file=sys.stderr)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
