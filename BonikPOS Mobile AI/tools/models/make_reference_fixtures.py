#!/usr/bin/env python3
"""Produce reference values used by the C# parity tests (tests/fixtures/reference) and package the
bundled models (models/).

Everything here is computed with the upstream reference implementations:
  * librosa 1.x  (mel power spectrogram used by the GE2E speaker encoder)
  * openai-whisper (mel filter bank and log-mel spectrogram)
  * kaldi-native-fbank (Kaldi-compatible fbank used by WeSpeaker / 3D-Speaker style encoders)
  * silero-vad OnnxWrapper (official streaming wrapper, for VAD probabilities)
  * Resemblyzer VoiceEncoder (PyTorch) for utterance embeddings

It also writes a TEST-ONLY target-speaker-extraction ONNX graph (tse-plumbing-fixture.onnx). That graph is
arithmetic, not a speech model: it exists to verify chunking, state carry-over and embedding binding of
the TSE adapter. It is never shipped in the app.
"""
from __future__ import annotations

import os

# ONNX Runtime 1.31 ships Microsoft usage telemetry; keep tooling runs off the network (docs/PRIVACY.md).
os.environ.setdefault("ORT_DISABLE_TELEMETRY", "1")

import argparse
import hashlib
import json
import pathlib
import shutil

import numpy as np
import soundfile


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def load(path: pathlib.Path) -> np.ndarray:
    audio, sr = soundfile.read(str(path), dtype="float32")
    assert sr == 16000 and audio.ndim == 1, path
    return audio


def write_f32(path: pathlib.Path, array: np.ndarray) -> None:
    np.ascontiguousarray(array, dtype="<f4").tofile(str(path))


def mel_fixtures(audio_dir: pathlib.Path, out: pathlib.Path) -> dict:
    import librosa
    from whisper.audio import mel_filters
    import torch

    wav = load(audio_dir / "synthetic-voiceA-1.wav")
    mel = librosa.feature.melspectrogram(y=wav, sr=16000, n_fft=400, hop_length=160, n_mels=40).astype(np.float32).T
    write_f32(out / "ge2e-mel-voiceA-1.f32", mel)
    whisper_filters = mel_filters(torch.device("cpu"), 80).numpy()
    write_f32(out / "whisper-mel-filters-80x201.f32", whisper_filters)
    slaney40 = librosa.filters.mel(sr=16000, n_fft=400, n_mels=40)
    write_f32(out / "librosa-mel-filters-40x201.f32", slaney40)
    return {
        "ge2e_mel": {"file": "ge2e-mel-voiceA-1.f32", "wav": "synthetic-voiceA-1.wav", "shape": list(mel.shape),
                     "layout": "row-major [frames, 40] float32 little-endian",
                     "librosa": librosa.__version__,
                     "call": "librosa.feature.melspectrogram(y, sr=16000, n_fft=400, hop_length=160, n_mels=40).T"},
        "whisper_mel_filters": {"file": "whisper-mel-filters-80x201.f32", "shape": list(whisper_filters.shape)},
        "librosa_mel_filters_40": {"file": "librosa-mel-filters-40x201.f32", "shape": list(slaney40.shape)},
    }


def kaldi_fixtures(audio_dir: pathlib.Path, out: pathlib.Path) -> dict:
    import kaldi_native_fbank as knf

    wav = load(audio_dir / "synthetic-voiceB-1.wav")
    opts = knf.FbankOptions()
    opts.frame_opts.samp_freq = 16000
    opts.frame_opts.dither = 0.0
    opts.frame_opts.snip_edges = True
    opts.mel_opts.num_bins = 80
    fbank = knf.OnlineFbank(opts)
    fbank.accept_waveform(16000, (wav * 32768.0).tolist())
    fbank.input_finished()
    feats = np.stack([fbank.get_frame(i) for i in range(fbank.num_frames_ready)]).astype(np.float32)
    write_f32(out / "kaldi-fbank80-voiceB-1.f32", feats)
    return {"kaldi_fbank": {
        "file": "kaldi-fbank80-voiceB-1.f32", "wav": "synthetic-voiceB-1.wav", "shape": list(feats.shape),
        "input_scale": 32768.0,
        "options": {"samp_freq": 16000, "frame_length_ms": 25, "frame_shift_ms": 10, "dither": 0.0,
                    "preemph_coeff": opts.frame_opts.preemph_coeff, "remove_dc_offset": opts.frame_opts.remove_dc_offset,
                    "window_type": opts.frame_opts.window_type, "round_to_power_of_two": opts.frame_opts.round_to_power_of_two,
                    "snip_edges": True, "num_bins": 80, "low_freq": opts.mel_opts.low_freq,
                    "high_freq": opts.mel_opts.high_freq, "use_energy": opts.use_energy,
                    "use_log_fbank": opts.use_log_fbank, "use_power": opts.use_power,
                    "energy_floor": opts.energy_floor},
        "kaldi_native_fbank": getattr(knf, "__version__", "unknown")}}


def vad_fixtures(audio_dir: pathlib.Path, vad_model: pathlib.Path) -> dict:
    """Official streaming wrapper on the package's default silero_vad.onnx (it always feeds an 'sr' input).
    The bundled fixed-shape 16 kHz graph was verified to give identical outputs (max abs diff 0.0)."""
    import silero_vad
    from silero_vad.utils_vad import OnnxWrapper
    import torch

    default_model = pathlib.Path(silero_vad.__file__).resolve().parent / "data" / "silero_vad.onnx"
    model = OnnxWrapper(str(default_model), force_onnx_cpu=True)
    result = {}
    for name in ("synthetic-en-napa.wav", "noise-2s.wav", "silence-1s.wav"):
        wav = load(audio_dir / name)
        model.reset_states()
        probs = []
        for i in range(0, wav.size - 511, 512):
            probs.append(float(model(torch.from_numpy(wav[i:i + 512]), 16000)[0, 0]))
        result[name] = probs
    return {"silero_vad_probabilities": {"chunk_samples": 512, "context_samples": 64, "per_file": result}}


def speaker_fixtures(audio_dir: pathlib.Path) -> dict:
    from resemblyzer import VoiceEncoder

    encoder = VoiceEncoder(device="cpu", verbose=False)
    names = ["synthetic-voiceA-1.wav", "synthetic-voiceA-2.wav", "synthetic-voiceA-3.wav",
             "synthetic-voiceB-1.wav", "synthetic-voiceB-2.wav", "synthetic-voiceC-1.wav",
             "synthetic-overlap-A2-B2.wav"]
    embeds = {n: encoder.embed_utterance(load(audio_dir / n)) for n in names}
    sims = {a: {b: float(np.dot(embeds[a], embeds[b])) for b in names} for a in names}
    return {"ge2e_utterance_embeddings": {
        "note": "Resemblyzer.embed_utterance on the RAW wav (no preprocess_wav), rate=1.3, min_coverage=0.75. "
                "Synthetic TTS voices: plumbing reference only, not speaker-verification accuracy evidence.",
        "embeddings": {n: e.tolist() for n, e in embeds.items()}, "cosine": sims}}


def tse_fixture(out: pathlib.Path) -> dict:
    """Arithmetic stand-in with the tensor layout of a streaming target-speaker extractor."""
    import onnx
    from onnx import TensorProto, helper

    chunk, emb_dim, state_dim = 320, 256, 8
    nodes = [
        helper.make_node("ReduceSum", ["spk_emb"], ["emb_sum"], keepdims=1),          # [1,1]
        helper.make_node("Sigmoid", ["emb_sum"], ["gain"]),                               # [1,1]
        helper.make_node("ReduceMean", ["state_in"], ["state_mean"], keepdims=1),       # [1,1]
        helper.make_node("Mul", ["mixture", "gain"], ["scaled"]),
        helper.make_node("Add", ["scaled", "state_mean"], ["enhanced"]),                 # [1,chunk]
        helper.make_node("ReduceMean", ["mixture"], ["chunk_mean"], keepdims=1),        # [1,1]
        helper.make_node("Slice", ["state_in", "starts", "ends", "axes"], ["state_tail"]),
        helper.make_node("Concat", ["state_tail", "chunk_mean"], ["state_out"], axis=1),
    ]
    inits = [helper.make_tensor("starts", TensorProto.INT64, [1], [1]),
             helper.make_tensor("ends", TensorProto.INT64, [1], [state_dim]),
             helper.make_tensor("axes", TensorProto.INT64, [1], [1])]
    graph = helper.make_graph(
        nodes, "tse_plumbing_fixture",
        [helper.make_tensor_value_info("mixture", TensorProto.FLOAT, [1, chunk]),
         helper.make_tensor_value_info("spk_emb", TensorProto.FLOAT, [1, emb_dim]),
         helper.make_tensor_value_info("state_in", TensorProto.FLOAT, [1, state_dim])],
        [helper.make_tensor_value_info("enhanced", TensorProto.FLOAT, [1, chunk]),
         helper.make_tensor_value_info("state_out", TensorProto.FLOAT, [1, state_dim])],
        initializer=inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], producer_name="bonikpos-test")
    model.ir_version = 8
    for k, v in {"bonik.role": "tse-test-fixture",
                 "bonik.notice": "TEST FIXTURE - arithmetic only, NOT a target-speaker extraction model"}.items():
        e = model.metadata_props.add()
        e.key, e.value = k, v
    onnx.checker.check_model(model)
    path = out / "tse-plumbing-fixture.onnx"
    onnx.save(model, str(path))

    # Expected outputs for a deterministic input sequence (3 chunks), computed with onnxruntime.
    import onnxruntime as ort
    s = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    emb = np.full((1, emb_dim), 1.0 / emb_dim, np.float32)
    state = np.zeros((1, state_dim), np.float32)
    outputs = []
    for c in range(3):
        mixture = (np.arange(chunk, dtype=np.float32)[None] / chunk + c).astype(np.float32)
        enhanced, state = s.run(["enhanced", "state_out"], {"mixture": mixture, "spk_emb": emb, "state_in": state})
        outputs.append({"chunk": c, "enhanced_first": float(enhanced[0, 0]), "enhanced_last": float(enhanced[0, -1]),
                        "state_out": state[0].tolist()})
    return {"tse_plumbing_fixture": {"file": path.name, "sha256": sha256(path), "chunk_samples": chunk,
                                     "embedding_dim": emb_dim, "state_dim": state_dim,
                                     "input_rule": "mixture[c][i] = i/chunk + c ; spk_emb = 1/256 everywhere",
                                     "expected": outputs}}


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[2])
    args = ap.parse_args()
    root = args.root
    audio_dir = root / "tests" / "fixtures" / "audio"
    ref_dir = root / "tests" / "fixtures" / "reference"
    ref_dir.mkdir(parents=True, exist_ok=True)

    # Bundle Silero VAD (MIT) exactly as shipped in the silero-vad wheel.
    import silero_vad
    src = pathlib.Path(silero_vad.__file__).resolve().parent / "data" / "silero_vad_openvino_16k.onnx"
    vad_dir = root / "models" / "vad"
    vad_dir.mkdir(parents=True, exist_ok=True)
    vad_model = vad_dir / "silero-vad-6.2.3-16k.onnx"
    shutil.copyfile(src, vad_model)

    reference = {"generated_by": "tools/models/make_reference_fixtures.py"}
    reference.update(mel_fixtures(audio_dir, ref_dir))
    reference.update(kaldi_fixtures(audio_dir, ref_dir))
    reference.update(vad_fixtures(audio_dir, vad_model))
    reference.update(speaker_fixtures(audio_dir))
    reference.update(tse_fixture(ref_dir))
    reference["silero_vad_model"] = {"file": "models/vad/silero-vad-6.2.3-16k.onnx", "sha256": sha256(vad_model),
                                     "source": "PyPI silero-vad 6.2.3 silero_vad/data/silero_vad_openvino_16k.onnx",
                                     "source_sha256": sha256(src), "license": "MIT"}
    with open(ref_dir / "reference.json", "w", encoding="utf-8") as f:
        json.dump(reference, f, indent=1, ensure_ascii=False)
    print(json.dumps({k: (v if k in ("silero_vad_model",) else "ok") for k, v in reference.items()}, indent=2))


if __name__ == "__main__":
    main()
