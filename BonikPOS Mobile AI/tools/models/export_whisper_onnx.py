#!/usr/bin/env python3
"""Export an OpenAI Whisper model (or a Hugging Face Whisper fine-tune) to the ONNX contract used by
BonikPOS.VoiceSearch.AI (WhisperOnnxRecognizer), and validate the export against PyTorch.

Why a custom export: the app needs (a) a fixed, documented tensor contract, (b) a decoder with an
explicit self-attention KV cache so that each decoding step processes only the new token, and
(c) optionally a reduced audio context for short medicine queries. Every run validates the ONNX
graphs against the original PyTorch model and fails loudly if they disagree.

ONNX contract (batch is always 1)
  encoder:  in  "mel"      float32 [1, n_mels, n_frames]            n_frames = 2 * n_audio_ctx
            out "cross_k"  float32 [n_text_layer, 1, n_audio_ctx, n_text_state]
            out "cross_v"  float32 [n_text_layer, 1, n_audio_ctx, n_text_state]
  decoder:  in  "tokens"   int64   [1, n_tokens]
            in  "past_k"   float32 [n_text_layer, 1, n_past, n_text_state]   (n_past may be 0)
            in  "past_v"   float32 [n_text_layer, 1, n_past, n_text_state]
            in  "cross_k"  float32 [n_text_layer, 1, n_audio_ctx, n_text_state]
            in  "cross_v"  float32 [n_text_layer, 1, n_audio_ctx, n_text_state]
            out "logits"   float32 [1, n_vocab]          logits of the LAST input position only
            out "present_k" / "present_v" float32 [n_text_layer, 1, n_past + n_tokens, n_text_state]
  <prefix>-tokens.txt   one line per token id: "<id> <base64 of token bytes>"
  <prefix>-config.json  dimensions, special token ids, language token ids, suppress lists,
                        feature parameters and provenance (checksums, licence, export options)

Sources (pick one)
  --model tiny|base|small|medium|large-v3|...  official OpenAI weights through openai-whisper
                                               (downloads from openaipublic.azureedge.net and checks
                                               the SHA-256 embedded in the URL). License: MIT.
  --checkpoint file.pt                          an OpenAI-format checkpoint already on disk
  --hf-model <hub id or local dir>              a Hugging Face WhisperForConditionalGeneration
                                               checkpoint (e.g. a Bangla fine-tune). Check its licence!
  --random-preset test-tiny                     random weights - ONLY for tests of the tensor plumbing.

Examples
  python export_whisper_onnx.py --model small --out-dir ../../models/asr --prefix whisper-small
  python export_whisper_onnx.py --model base --n-audio-ctx 500 --quantize int8 --out-dir ../../models/asr \
      --prefix whisper-base-ctx500-int8
  python export_whisper_onnx.py --random-preset test-tiny --out-dir ../../tests/fixtures/whisper-test-tiny \
      --prefix whisper-test-tiny --fixture-wav ../../tests/fixtures/audio/synthetic-en-napa.wav
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import pathlib
import sys
import time

import numpy as np
import onnx
import onnxruntime as ort
import torch
import torch.nn.functional as F

import whisper
import whisper.model as wm
from whisper.audio import N_FFT, HOP_LENGTH, SAMPLE_RATE, log_mel_spectrogram
from whisper.model import ModelDimensions, Whisper
from whisper.tokenizer import LANGUAGES, get_tokenizer

RANDOM_PRESETS = {
    # Small enough to commit as a test fixture, but with the real vocabulary so token ids are real.
    "test-tiny": dict(n_mels=80, n_audio_ctx=150, n_audio_state=32, n_audio_head=2, n_audio_layer=2,
                      n_vocab=51865, n_text_ctx=448, n_text_state=32, n_text_head=2, n_text_layer=2),
}


def sha256_file(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


# --------------------------------------------------------------------------------------------------
# Model loading
# --------------------------------------------------------------------------------------------------

def load_random(preset: str, seed: int) -> Whisper:
    dims = ModelDimensions(**RANDOM_PRESETS[preset])
    torch.manual_seed(seed)
    model = Whisper(dims)
    with torch.no_grad():
        for module in model.modules():
            if isinstance(module, torch.nn.LayerNorm):
                module.weight.fill_(1.0)
                module.bias.zero_()
            elif isinstance(module, (torch.nn.Linear, torch.nn.Conv1d)):
                module.weight.normal_(0.0, 0.2)
                if module.bias is not None:
                    module.bias.normal_(0.0, 0.05)
        model.decoder.token_embedding.weight.normal_(0.0, 0.5)
        model.decoder.positional_embedding.normal_(0.0, 0.2)
    return model.eval()


HF_RENAMES = [
    ("model.encoder.conv1.", "encoder.conv1."),
    ("model.encoder.conv2.", "encoder.conv2."),
    ("model.encoder.layer_norm.", "encoder.ln_post."),
    ("model.decoder.embed_tokens.weight", "decoder.token_embedding.weight"),
    ("model.decoder.embed_positions.weight", "decoder.positional_embedding"),
    ("model.decoder.layer_norm.", "decoder.ln."),
]
HF_LAYER_RENAMES = [
    ("self_attn.q_proj.", "attn.query."), ("self_attn.k_proj.", "attn.key."),
    ("self_attn.v_proj.", "attn.value."), ("self_attn.out_proj.", "attn.out."),
    ("self_attn_layer_norm.", "attn_ln."),
    ("encoder_attn.q_proj.", "cross_attn.query."), ("encoder_attn.k_proj.", "cross_attn.key."),
    ("encoder_attn.v_proj.", "cross_attn.value."), ("encoder_attn.out_proj.", "cross_attn.out."),
    ("encoder_attn_layer_norm.", "cross_attn_ln."),
    ("fc1.", "mlp.0."), ("fc2.", "mlp.2."), ("final_layer_norm.", "mlp_ln."),
]


def hf_to_openai(hf_model) -> Whisper:
    """Convert a transformers WhisperForConditionalGeneration into an openai-whisper Whisper module."""
    cfg = hf_model.config
    dims = ModelDimensions(
        n_mels=cfg.num_mel_bins, n_audio_ctx=cfg.max_source_positions, n_audio_state=cfg.d_model,
        n_audio_head=cfg.encoder_attention_heads, n_audio_layer=cfg.encoder_layers,
        n_vocab=cfg.vocab_size, n_text_ctx=cfg.max_target_positions, n_text_state=cfg.d_model,
        n_text_head=cfg.decoder_attention_heads, n_text_layer=cfg.decoder_layers)
    model = Whisper(dims)
    target = model.state_dict()
    converted = {}
    for key, value in hf_model.state_dict().items():
        if key in ("proj_out.weight",):
            continue  # tied to decoder.embed_tokens
        if key == "model.encoder.embed_positions.weight":
            converted["encoder.positional_embedding"] = value
            continue
        new = None
        for a, b in HF_RENAMES:
            if key.startswith(a):
                new = b + key[len(a):]
                break
        if new is None:
            for side in ("encoder", "decoder"):
                prefix = f"model.{side}.layers."
                if key.startswith(prefix):
                    rest = key[len(prefix):]
                    idx, _, tail = rest.partition(".")
                    for a, b in HF_LAYER_RENAMES:
                        if tail.startswith(a):
                            new = f"{side}.blocks.{idx}.{b}{tail[len(a):]}"
                            break
        if new is None:
            raise KeyError(f"Unmapped Hugging Face parameter: {key}")
        converted[new] = value
    missing = set(target) - set(converted)
    # alignment_heads is a buffer that only matters for word timestamps
    missing.discard("alignment_heads")
    if missing:
        raise KeyError(f"Missing parameters after conversion: {sorted(missing)[:10]}")
    model.load_state_dict(converted, strict=False)
    return model.eval()


def load_model(args) -> tuple[Whisper, dict]:
    if args.random_preset:
        return load_random(args.random_preset, args.seed), {
            "source": f"RANDOM WEIGHTS preset={args.random_preset} seed={args.seed} - test fixture, not a speech model",
            "license": "n/a (random weights)", "model_id": f"whisper-random-{args.random_preset}"}
    if args.hf_model:
        from transformers import WhisperForConditionalGeneration
        hf = WhisperForConditionalGeneration.from_pretrained(args.hf_model, torch_dtype=torch.float32)
        return hf_to_openai(hf), {"source": f"Hugging Face {args.hf_model}",
                                  "license": "CHECK THE MODEL CARD - not verified by this script",
                                  "model_id": "whisper-hf-" + pathlib.Path(str(args.hf_model)).name}
    if args.checkpoint:
        model = whisper.load_model(str(args.checkpoint), device="cpu")
        return model, {"source": f"checkpoint {args.checkpoint} sha256={sha256_file(pathlib.Path(args.checkpoint))}",
                       "license": "MIT (OpenAI Whisper) unless the checkpoint states otherwise",
                       "model_id": "whisper-" + pathlib.Path(args.checkpoint).stem}
    url = whisper._MODELS[args.model]
    model = whisper.load_model(args.model, device="cpu")
    return model, {"source": f"OpenAI Whisper '{args.model}' {url} (sha256 {url.split('/')[-2]})",
                   "license": "MIT", "model_id": f"whisper-{args.model}"}


# --------------------------------------------------------------------------------------------------
# Export modules
# --------------------------------------------------------------------------------------------------

def attention(q, k, v, n_head: int, mask=None):
    n_ctx_q, n_state = q.shape[1], q.shape[2]
    head_dim = n_state // n_head
    scale = head_dim ** -0.25
    q = q.reshape(1, -1, n_head, head_dim).permute(0, 2, 1, 3) * scale
    k = k.reshape(1, -1, n_head, head_dim).permute(0, 2, 3, 1) * scale
    v = v.reshape(1, -1, n_head, head_dim).permute(0, 2, 1, 3)
    qk = q @ k
    if mask is not None:
        qk = qk + mask
    w = torch.softmax(qk, dim=-1)
    return (w @ v).permute(0, 2, 1, 3).reshape(1, -1, n_state)


class EncoderExport(torch.nn.Module):
    def __init__(self, model: Whisper, n_audio_ctx: int):
        super().__init__()
        self.encoder = model.encoder
        self.blocks = model.decoder.blocks
        self.n_audio_ctx = n_audio_ctx

    def forward(self, mel):
        enc = self.encoder
        x = F.gelu(enc.conv1(mel))
        x = F.gelu(enc.conv2(x))
        x = x.permute(0, 2, 1)
        x = x + enc.positional_embedding[: self.n_audio_ctx]
        for block in enc.blocks:
            x = block(x)
        x = enc.ln_post(x)
        ks = [b.cross_attn.key(x) for b in self.blocks]
        vs = [b.cross_attn.value(x) for b in self.blocks]
        return torch.stack(ks), torch.stack(vs)


class DecoderExport(torch.nn.Module):
    def __init__(self, model: Whisper):
        super().__init__()
        self.decoder = model.decoder
        self.n_head = model.dims.n_text_head

    def forward(self, tokens, past_k, past_v, cross_k, cross_v):
        dec = self.decoder
        n_past = past_k.shape[2]
        n_tokens = tokens.shape[1]
        x = dec.token_embedding(tokens) + dec.positional_embedding[n_past: n_past + n_tokens]
        q_pos = torch.arange(n_tokens).unsqueeze(1) + n_past
        k_pos = torch.arange(n_past + n_tokens).unsqueeze(0)
        mask = torch.where(k_pos > q_pos, torch.tensor(float("-inf")), torch.tensor(0.0))
        present_k, present_v = [], []
        for i, block in enumerate(dec.blocks):
            h = block.attn_ln(x)
            q = block.attn.query(h)
            k = torch.cat([past_k[i], block.attn.key(h)], dim=1)
            v = torch.cat([past_v[i], block.attn.value(h)], dim=1)
            present_k.append(k)
            present_v.append(v)
            x = x + block.attn.out(attention(q, k, v, self.n_head, mask))
            h = block.cross_attn_ln(x)
            q = block.cross_attn.query(h)
            x = x + block.cross_attn.out(attention(q, cross_k[i], cross_v[i], self.n_head))
            x = x + block.mlp(block.mlp_ln(x))
        x = dec.ln(x[:, -1, :])
        # F.linear exports as Gemm(transB=1) on the embedding initializer, so the 51k x n_state
        # embedding matrix is stored once instead of a second, constant-folded transposed copy.
        logits = F.linear(x, dec.token_embedding.weight)
        return logits, torch.stack(present_k), torch.stack(present_v)


# --------------------------------------------------------------------------------------------------
# Decoding helpers shared by the validation (mirrors WhisperOnnxRecognizer in C#)
# --------------------------------------------------------------------------------------------------

def suppress_lists(tokenizer):
    suppress = set(tokenizer.non_speech_tokens)
    suppress.update([tokenizer.transcribe, tokenizer.translate, tokenizer.sot, tokenizer.sot_prev,
                     tokenizer.sot_lm])
    if tokenizer.no_speech is not None:
        suppress.add(tokenizer.no_speech)
    blank = list(tokenizer.encode(" ")) + [tokenizer.eot]
    return sorted(suppress), blank


def apply_filters(logits: np.ndarray, step: int, suppress, blank, timestamp_begin: int):
    logits = logits.copy()
    logits[list(suppress)] = -np.inf
    logits[timestamp_begin:] = -np.inf  # the app always decodes with <|notimestamps|>
    if step == 0:
        logits[blank] = -np.inf
    return logits


def greedy_onnx(enc_sess, dec_sess, mel, tokenizer, language: str, dims, max_tokens: int):
    cross_k, cross_v = enc_sess.run(["cross_k", "cross_v"], {"mel": mel})
    empty = np.zeros((dims.n_text_layer, 1, 0, dims.n_text_state), np.float32)
    feeds = {"tokens": np.array([[tokenizer.sot]], np.int64), "past_k": empty, "past_v": empty,
             "cross_k": cross_k, "cross_v": cross_v}
    logits, pk, pv = dec_sess.run(["logits", "present_k", "present_v"], feeds)
    probs = softmax(logits[0])
    no_speech = float(probs[tokenizer.no_speech]) if tokenizer.no_speech is not None else float("nan")
    lang_ids = {code: tokenizer.to_language_token(code) for code in ("bn", "en")}
    lang_logits = {code: float(logits[0, tid]) for code, tid in lang_ids.items()}
    prompt = [tokenizer.to_language_token(language), tokenizer.transcribe, tokenizer.no_timestamps]
    suppress, blank = suppress_lists(tokenizer)
    tokens, logprobs = [], []
    feed_tokens = prompt
    for step in range(max_tokens):
        logits, pk, pv = dec_sess.run(["logits", "present_k", "present_v"], {
            "tokens": np.array([feed_tokens], np.int64), "past_k": pk, "past_v": pv,
            "cross_k": cross_k, "cross_v": cross_v})
        filtered = apply_filters(logits[0], step, suppress, blank, tokenizer.timestamp_begin)
        token = int(np.argmax(filtered))
        logprobs.append(float(log_softmax(filtered)[token]))
        if token == tokenizer.eot:
            break
        tokens.append(token)
        feed_tokens = [token]
    return tokens, logprobs, no_speech, lang_logits


def greedy_torch(model: Whisper, mel, tokenizer, language: str, max_tokens: int):
    """Reference decoder: recomputes the full sequence with the original openai-whisper modules."""
    with torch.no_grad():
        audio = model.encoder(torch.from_numpy(mel))
        seq = [tokenizer.sot, tokenizer.to_language_token(language), tokenizer.transcribe, tokenizer.no_timestamps]
        suppress, blank = suppress_lists(tokenizer)
        out = []
        for step in range(max_tokens):
            logits = model.decoder(torch.tensor([seq]), audio)[0, -1].numpy()
            token = int(np.argmax(apply_filters(logits, step, suppress, blank, tokenizer.timestamp_begin)))
            if token == tokenizer.eot:
                break
            out.append(token)
            seq.append(token)
    return out


def dedupe_tied_embedding(path: pathlib.Path) -> int:
    """The TorchScript exporter constant-folds W.T for the output projection, storing the token embedding
    twice (for whisper-small that is ~160 MB of duplicated fp32). Rewrite MatMul(x, W.T) into
    Gemm(x, W, transB=1) on the original embedding initializer. Returns the number of bytes saved."""
    from onnx import helper, numpy_helper
    proto = onnx.load(str(path))
    inits = {i.name: i for i in proto.graph.initializer}
    emb = inits.get("decoder.token_embedding.weight")
    if emb is None:
        return 0
    emb_np = numpy_helper.to_array(emb)
    saved = 0
    for node in list(proto.graph.node):
        if node.op_type != "MatMul" or node.input[1] not in inits:
            continue
        cand = inits[node.input[1]]
        if list(cand.dims) != [emb_np.shape[1], emb_np.shape[0]]:
            continue
        if not np.array_equal(numpy_helper.to_array(cand), emb_np.T):
            continue
        gemm = helper.make_node("Gemm", [node.input[0], emb.name], list(node.output), name=node.name + "_tied",
                                transB=1)
        idx = list(proto.graph.node).index(node)
        proto.graph.node.remove(node)
        proto.graph.node.insert(idx, gemm)
        proto.graph.initializer.remove(cand)
        saved += emb_np.nbytes
    if saved:
        onnx.save(proto, str(path))
    return saved


def softmax(x):
    e = np.exp(x - np.max(x))
    return e / e.sum()


def log_softmax(x):
    finite = x[np.isfinite(x)]
    m = finite.max()
    return x - (m + np.log(np.exp(finite - m).sum()))


# --------------------------------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--model")
    src.add_argument("--checkpoint", type=pathlib.Path)
    src.add_argument("--hf-model")
    src.add_argument("--random-preset", choices=sorted(RANDOM_PRESETS))
    ap.add_argument("--out-dir", required=True, type=pathlib.Path)
    ap.add_argument("--prefix", required=True)
    ap.add_argument("--n-audio-ctx", type=int, default=0,
                    help="audio context (frames/2). 0 = model default (1500 = 30 s). Smaller is faster "
                         "for short queries but MUST be re-evaluated for accuracy.")
    ap.add_argument("--quantize", choices=["none", "int8"], default="none")
    ap.add_argument("--opset", type=int, default=17)
    ap.add_argument("--seed", type=int, default=20261009)
    ap.add_argument("--max-tokens", type=int, default=24, help="tokens decoded during validation")
    ap.add_argument("--fixture-wav", type=pathlib.Path, help="16 kHz mono wav for validation/fixtures")
    args = ap.parse_args()

    wm.MultiHeadAttention.use_sdpa = False  # explicit attention math exports deterministically
    model, provenance = load_model(args)
    dims = model.dims
    n_audio_ctx = args.n_audio_ctx or dims.n_audio_ctx
    if n_audio_ctx > dims.n_audio_ctx:
        raise SystemExit(f"--n-audio-ctx {n_audio_ctx} exceeds the model's {dims.n_audio_ctx}")
    n_frames = 2 * n_audio_ctx
    multilingual = dims.n_vocab >= 51865
    tokenizer = get_tokenizer(multilingual, num_languages=dims.n_vocab - 51765 - int(multilingual),
                              language="en", task="transcribe")
    if tokenizer.encoding.n_vocab != dims.n_vocab:
        raise SystemExit(f"Tokenizer vocabulary ({tokenizer.encoding.n_vocab}) != model n_vocab ({dims.n_vocab}). "
                         "This checkpoint uses a different vocabulary and is not supported.")

    args.out_dir.mkdir(parents=True, exist_ok=True)
    enc_path = args.out_dir / f"{args.prefix}-encoder.onnx"
    dec_path = args.out_dir / f"{args.prefix}-decoder.onnx"

    with torch.no_grad():
        mel_dummy = torch.zeros(1, dims.n_mels, n_frames)
        torch.onnx.export(EncoderExport(model, n_audio_ctx).eval(), (mel_dummy,), str(enc_path),
                          input_names=["mel"], output_names=["cross_k", "cross_v"],
                          opset_version=args.opset, dynamo=False)
        ck, cv = EncoderExport(model, n_audio_ctx)(mel_dummy)
        past = torch.zeros(dims.n_text_layer, 1, 2, dims.n_text_state)
        torch.onnx.export(DecoderExport(model).eval(),
                          (torch.tensor([[tokenizer.sot, tokenizer.sot]]), past, past, ck, cv), str(dec_path),
                          input_names=["tokens", "past_k", "past_v", "cross_k", "cross_v"],
                          output_names=["logits", "present_k", "present_v"],
                          dynamic_axes={"tokens": {1: "n_tokens"}, "past_k": {2: "n_past"},
                                        "past_v": {2: "n_past"},
                                        "present_k": {2: "n_total"}, "present_v": {2: "n_total"}},
                          opset_version=args.opset, dynamo=False)

    # onnxruntime's dynamic quantizer rewrites Gemm(transB=1) into MatMul by transposing the weight in place,
    # which would corrupt an initializer shared with the embedding Gather. So only fp32 exports are
    # de-duplicated; int8 exports keep the separate (then int8, 1/4 size) transposed projection matrix.
    dedupe_saved = dedupe_tied_embedding(dec_path) if args.quantize == "none" else 0
    for path in (enc_path, dec_path):
        proto = onnx.load(str(path))
        onnx.checker.check_model(proto)

    fp32_paths = (enc_path, dec_path)
    if args.quantize == "int8":
        from onnxruntime.quantization import QuantType, quantize_dynamic
        q_paths = []
        for path in fp32_paths:
            q = path.with_name(path.stem + ".int8.onnx")
            quantize_dynamic(str(path), str(q), weight_type=QuantType.QInt8, op_types_to_quantize=["MatMul", "Gemm"])
            q_paths.append(q)
        final_paths = tuple(q_paths)
    else:
        final_paths = fp32_paths

    for path, role in zip(final_paths, ("asr-encoder", "asr-decoder")):
        proto = onnx.load(str(path))
        del proto.metadata_props[:]
        for k, v in {"bonik.model_id": provenance["model_id"], "bonik.role": role,
                     "bonik.license": provenance["license"], "bonik.source": provenance["source"],
                     "bonik.n_audio_ctx": str(n_audio_ctx), "bonik.quantization": args.quantize}.items():
            e = proto.metadata_props.add()
            e.key, e.value = k, v
        onnx.save(proto, str(path))

    # ---- tokens + config ----------------------------------------------------------------------------
    tokens_path = args.out_dir / f"{args.prefix}-tokens.txt"
    special = {v: k for k, v in tokenizer.encoding._special_tokens.items()}
    with open(tokens_path, "w", encoding="utf-8", newline="\n") as f:
        for tid in range(dims.n_vocab):
            if tid in special:
                data = special[tid].encode("utf-8")
            else:
                data = tokenizer.encoding.decode_single_token_bytes(tid)
            f.write(f"{tid} {base64.b64encode(data).decode('ascii')}\n")
    suppress, blank = suppress_lists(tokenizer)
    languages = {code: tokenizer.to_language_token(code) for code in LANGUAGES
                 if f"<|{code}|>" in tokenizer.encoding._special_tokens
                 and tokenizer.to_language_token(code) < dims.n_vocab}
    config = {
        "format": "bonikpos-whisper-onnx/1",
        "model_id": provenance["model_id"],
        "license": provenance["license"],
        "source": provenance["source"],
        "exported_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "quantization": args.quantize,
        "files": {"encoder": final_paths[0].name, "decoder": final_paths[1].name, "tokens": tokens_path.name},
        "sha256": {p.name: sha256_file(p) for p in (*final_paths, tokens_path)},
        "dims": {**dims.__dict__, "n_audio_ctx_exported": n_audio_ctx},
        "features": {"sample_rate": SAMPLE_RATE, "n_fft": N_FFT, "hop_length": HOP_LENGTH,
                     "n_mels": dims.n_mels, "n_frames": n_frames, "n_samples": n_frames * HOP_LENGTH,
                     "mel_filters": "librosa slaney, as whisper/assets/mel_filters.npz"},
        "special_tokens": {"eot": tokenizer.eot, "sot": tokenizer.sot, "sot_prev": tokenizer.sot_prev,
                           "sot_lm": tokenizer.sot_lm, "no_speech": tokenizer.no_speech,
                           "no_timestamps": tokenizer.no_timestamps, "transcribe": tokenizer.transcribe,
                           "translate": tokenizer.translate, "timestamp_begin": tokenizer.timestamp_begin},
        "languages": languages,
        "suppress_tokens": suppress,
        "suppress_at_begin": blank,
    }

    # ---- validation -----------------------------------------------------------------------------------
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    enc = ort.InferenceSession(str(final_paths[0]), so, providers=["CPUExecutionProvider"])
    dec = ort.InferenceSession(str(final_paths[1]), so, providers=["CPUExecutionProvider"])
    io = {name: {"inputs": [(i.name, i.type, i.shape) for i in s.get_inputs()],
                 "outputs": [(o.name, o.type, o.shape) for o in s.get_outputs()]}
          for name, s in (("encoder", enc), ("decoder", dec))}

    if args.fixture_wav:
        import soundfile
        audio, sr = soundfile.read(str(args.fixture_wav), dtype="float32")
        if sr != SAMPLE_RATE or audio.ndim != 1:
            raise SystemExit("fixture wav must be 16 kHz mono")
    else:
        t = np.arange(SAMPLE_RATE * 2) / SAMPLE_RATE
        audio = (0.1 * np.sin(2 * np.pi * 220 * t)).astype(np.float32)
    padded = np.zeros(n_frames * HOP_LENGTH, np.float32)
    padded[: min(audio.size, padded.size)] = audio[: padded.size]
    mel = log_mel_spectrogram(torch.from_numpy(padded), dims.n_mels)[:, :n_frames].numpy()[None].astype(np.float32)

    # 1) encoder vs PyTorch
    with torch.no_grad():
        ref_k, ref_v = EncoderExport(model, n_audio_ctx)(torch.from_numpy(mel))
    got_k, got_v = enc.run(["cross_k", "cross_v"], {"mel": mel})
    enc_diff = float(max(np.abs(ref_k.numpy() - got_k).max(), np.abs(ref_v.numpy() - got_v).max()))
    enc_scale = float(np.abs(ref_k.numpy()).max())

    # 2) decoder with KV cache vs full-sequence PyTorch decoder (teacher forcing)
    rng = np.random.default_rng(args.seed)
    seq = [tokenizer.sot, tokenizer.to_language_token("bn"), tokenizer.transcribe, tokenizer.no_timestamps]
    seq += [int(t) for t in rng.integers(0, tokenizer.eot, size=12)]
    with torch.no_grad():
        if n_audio_ctx == dims.n_audio_ctx:
            audio_features = model.encoder(torch.from_numpy(mel))
        else:  # reduced-context reference (same math as EncoderExport, without the cross projections)
            e = model.encoder
            x = F.gelu(e.conv2(F.gelu(e.conv1(torch.from_numpy(mel))))).permute(0, 2, 1)
            x = x + e.positional_embedding[:n_audio_ctx]
            for b in e.blocks:
                x = b(x)
            audio_features = e.ln_post(x)
        full = model.decoder(torch.tensor([seq]), audio_features)[0].numpy()
    empty = np.zeros((dims.n_text_layer, 1, 0, dims.n_text_state), np.float32)
    logits, pk, pv = dec.run(None, {"tokens": np.array([seq[:4]], np.int64), "past_k": empty, "past_v": empty,
                                    "cross_k": got_k, "cross_v": got_v})
    dec_diff = float(np.abs(logits[0] - full[3]).max())
    teacher_forcing = [{"position": 3, "top5": np.argsort(-full[3])[:5].tolist(),
                        "top5_logits": np.sort(full[3])[::-1][:5].tolist()}]
    for i in range(4, len(seq)):
        logits, pk, pv = dec.run(None, {"tokens": np.array([[seq[i]]], np.int64), "past_k": pk, "past_v": pv,
                                        "cross_k": got_k, "cross_v": got_v})
        dec_diff = max(dec_diff, float(np.abs(logits[0] - full[i]).max()))
        teacher_forcing.append({"position": i, "top5": np.argsort(-full[i])[:5].tolist(),
                                "top5_logits": np.sort(full[i])[::-1][:5].tolist()})
    dec_scale = float(np.abs(full).max())

    # 3) greedy decoding: ONNX (KV cache) vs PyTorch (full recompute) must produce the same tokens
    onnx_tokens, logprobs, no_speech, lang_logits = greedy_onnx(enc, dec, mel, tokenizer, "bn", dims, args.max_tokens)
    if args.quantize == "none":
        torch_tokens = greedy_torch(model, mel, tokenizer, "bn", args.max_tokens) if n_audio_ctx == dims.n_audio_ctx else None
    else:
        torch_tokens = None

    tol = 1e-3 if args.quantize == "none" else float("inf")
    ok = enc_diff <= tol * max(1.0, enc_scale) and dec_diff <= tol * max(1.0, dec_scale) and \
        (torch_tokens is None or torch_tokens == onnx_tokens)

    report = {
        "files": {p.name: {"bytes": p.stat().st_size, "sha256": config["sha256"].get(p.name)} for p in final_paths},
        "tied_embedding_bytes_saved": dedupe_saved,
        "io": io,
        "encoder_max_abs_diff": enc_diff, "encoder_output_scale": enc_scale,
        "decoder_max_abs_diff_kv_cache_vs_full": dec_diff, "decoder_logit_scale": dec_scale,
        "greedy_tokens_onnx": onnx_tokens, "greedy_tokens_pytorch": torch_tokens,
        "greedy_text_onnx": tokenizer.decode(onnx_tokens),
        "no_speech_prob": no_speech, "language_logits": lang_logits,
        "validation_passed": ok,
    }
    config["validation"] = {k: report[k] for k in ("encoder_max_abs_diff", "decoder_max_abs_diff_kv_cache_vs_full",
                                                   "validation_passed")}
    with open(args.out_dir / f"{args.prefix}-config.json", "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2, ensure_ascii=False)

    if args.fixture_wav:
        fixture = {
            "description": "Reference outputs computed by export_whisper_onnx.py with onnxruntime (Python).",
            "wav": args.fixture_wav.name, "language": "bn", "max_tokens": args.max_tokens,
            "mel_shape": list(mel.shape),
            "mel_first_frame": mel[0, :, 0].tolist(), "mel_frame_100": mel[0, :, 100].tolist(),
            "mel_max": float(mel.max()), "mel_min": float(mel.min()), "mel_mean": float(mel.mean()),
            "expected_tokens": onnx_tokens, "expected_logprobs": logprobs,
            "no_speech_prob": no_speech, "language_logits": lang_logits,
            "teacher_forcing_sequence": seq,
            "teacher_forcing_reference": teacher_forcing,
        }
        with open(args.out_dir / f"{args.prefix}-reference.json", "w", encoding="utf-8") as f:
            json.dump(fixture, f, indent=2, ensure_ascii=False)

    print(json.dumps(report, indent=2, ensure_ascii=False))
    if not ok:
        print("VALIDATION FAILED", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
