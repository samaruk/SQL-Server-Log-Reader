#!/usr/bin/env python3
"""Writes demo-authored.cases.jsonl: hand-written labelled queries for the DEMO catalog.

Provenance: authored by the developer of the matcher (not field data, not real ASR output). The set was written BEFORE
the correction-candidate / alternative-hypothesis changes were implemented, so that the baseline run is not tuned to it.
It is a regression set for the matching logic, NOT an estimate of real-world accuracy. See docs/SEARCH_EVALUATION.md.
"""
import json, pathlib

def case(q, products, kind, lang="en", **extra):
    d = {"query": q, "kind": kind if isinstance(kind, list) else [kind], "lang": lang, "expect": {"products": products}}
    if "decisions" in extra:
        d["expect"]["decisions"] = extra.pop("decisions")
    d.update(extra)
    return d

NAPA = ["DEMO-0001", "DEMO-0002", "DEMO-0003"]
C = []
# --- exact brand (Latin), with and without strength / form -------------------------------------------------------
C += [
    case("Napa", NAPA, "ambiguous", decisions=["Ambiguous"]),
    case("Napa 500", ["DEMO-0001"], "en-strength"),
    case("Napa syrup", ["DEMO-0002"], "en-form"),
    case("Napa drops", ["DEMO-0003"], "en-form"),
    case("Napa Extra", ["DEMO-0004"], "en-exact"),
    case("Napa Extend", ["DEMO-0005"], "en-exact"),
    case("Napa Extend 665", ["DEMO-0005"], "en-strength"),
    case("Ace", ["DEMO-0006", "DEMO-0007"], "ambiguous", decisions=["Ambiguous"]),
    case("Ace 500 tablet", ["DEMO-0006"], "en-strength-form"),
    case("Ace syrup", ["DEMO-0007"], "en-form"),
    case("Ace Plus", ["DEMO-0008"], "en-exact"),
    case("Fast 500", ["DEMO-0009"], "en-strength"),
    case("Seclo", ["DEMO-0010", "DEMO-0011"], "ambiguous", decisions=["Ambiguous"]),
    case("Seclo 20", ["DEMO-0010"], "en-strength"),
    case("Seclo 40 capsule", ["DEMO-0011"], "en-strength-form"),
    case("Losectil 20", ["DEMO-0012"], "en-strength"),
    case("Sergel", ["DEMO-0013", "DEMO-0014"], "ambiguous", decisions=["Ambiguous"]),
    case("Sergel 20", ["DEMO-0013"], "en-strength"),
    case("Maxpro 20", ["DEMO-0015"], "en-strength"),
    case("Maxpro 40 tablet", ["DEMO-0016"], "en-strength-form"),
    case("Nexum 20", ["DEMO-0017"], "en-strength"),
    case("Pantonix 40", ["DEMO-0019"], "en-strength"),
    case("Fexo 120", ["DEMO-0020"], "en-strength"),
    case("Fexo 180", ["DEMO-0021"], "en-strength"),
    case("Alatrol syrup", ["DEMO-0023"], "en-form"),
    case("Alatrol tablet", ["DEMO-0022"], "en-form"),
    case("Monas", ["DEMO-0024", "DEMO-0025", "DEMO-0026"], "ambiguous", decisions=["Ambiguous"]),
    case("Monas 10", ["DEMO-0024"], "en-strength"),
    case("Monas 4", ["DEMO-0026"], "en-strength"),
    case("Montair 10", ["DEMO-0027"], "en-strength"),
    case("Zimax 500", ["DEMO-0028"], "en-strength"),
    case("Zimax suspension", ["DEMO-0029"], "en-form"),
    case("Zimax powder", ["DEMO-0029"], "en-form", note="catalog form is 'Powder for Suspension'"),
    case("Azithrocin", ["DEMO-0030"], "en-exact"),
    case("Ciprocin 250", ["DEMO-0032"], "en-strength"),
    case("Moxacil 500", ["DEMO-0033"], "en-strength"),
    case("Moxacil syrup", ["DEMO-0035"], "en-form"),
    case("Cef-3", ["DEMO-0036"], "en-exact"),
    case("cef 3", ["DEMO-0036"], "en-exact"),
    case("Filmet 400", ["DEMO-0037"], "en-strength"),
    case("Amodis", ["DEMO-0038"], "en-exact"),
    case("Flagyl", ["DEMO-0039"], "en-exact"),
    case("Tufnil", ["DEMO-0040"], "en-exact"),
    case("Clofenac 50", ["DEMO-0041"], "en-strength"),
    case("Rolac", ["DEMO-0042"], "en-exact"),
    case("Comet 850", ["DEMO-0044"], "en-strength"),
    case("Osartil 50", ["DEMO-0045"], "en-strength"),
    case("Amdocal", ["DEMO-0046"], "en-exact"),
    case("Camlodin", ["DEMO-0047"], "en-exact"),
    case("Ceevit", ["DEMO-0048"], "en-exact"),
    case("Calbo D", ["DEMO-0049"], "en-exact"),
    case("Neuro B", ["DEMO-0050"], "en-exact"),
    case("Entacyd Plus", ["DEMO-0051"], "en-exact"),
    case("Azmasol inhaler", ["DEMO-0052"], "en-form"),
    case("Orsaline", ["DEMO-0053"], "en-exact"),
    case("Nebanol powder", ["DEMO-0054"], "en-form"),
    case("Betnovate N cream", ["DEMO-0055"], "en-form"),
]
# --- English number words ---------------------------------------------------------------------------------------
C += [
    case("napa five hundred", ["DEMO-0001"], "en-number-words"),
    case("seclo twenty milligram", ["DEMO-0010"], "en-number-words"),
    case("sergel forty", ["DEMO-0014"], "en-number-words"),
    case("monas four", ["DEMO-0026"], "en-number-words"),
    case("montair ten milligram tablet", ["DEMO-0027"], "en-number-words"),
    case("moxacil two hundred fifty", ["DEMO-0034"], "en-number-words"),
    case("fexo one twenty", ["DEMO-0020"], "en-number-words", note="'one twenty' = 120 as commonly said in Bangladesh"),
    case("fexo one eighty", ["DEMO-0021"], "en-number-words"),
    case("comet eight fifty", ["DEMO-0044"], "en-number-words"),
    case("ciprocin two fifty", ["DEMO-0032"], "en-number-words"),
]
# --- Bangla script: products that have a Bangla local name in the catalog -------------------------------------
C += [
    case("নাপা এক্সট্রা", ["DEMO-0004"], "bn-script", "bn"),
    case("নাপা ৫০০", ["DEMO-0001"], "bn-script", "bn"),
    case("নাপা পাঁচশো মিলিগ্রাম", ["DEMO-0001"], ["bn-script", "bn-number-words"], "bn"),
    case("নাপা সিরাপ", ["DEMO-0002"], "bn-script", "bn"),
    case("সেকলো ২০", ["DEMO-0010"], "bn-script", "bn"),
    case("সেকলো বিশ মিলিগ্রাম ক্যাপসুল", ["DEMO-0010"], ["bn-script", "bn-number-words"], "bn"),
    case("সেকলো চল্লিশ", ["DEMO-0011"], ["bn-script", "bn-number-words"], "bn"),
    case("এইস প্লাস", ["DEMO-0008"], "bn-script", "bn"),
    case("ফেক্সো ১২০", ["DEMO-0020"], "bn-script", "bn"),
    case("ফেক্সো একশো আশি", ["DEMO-0021"], ["bn-script", "bn-number-words"], "bn"),
    case("মোনাস দশ", ["DEMO-0024"], ["bn-script", "bn-number-words"], "bn"),
    case("মোনাস চার", ["DEMO-0026"], ["bn-script", "bn-number-words"], "bn"),
    case("জিম্যাক্স ৫০০", ["DEMO-0028"], "bn-script", "bn"),
    case("সিপ্রোসিন পাঁচশো", ["DEMO-0031"], ["bn-script", "bn-number-words"], "bn"),
    case("মোক্সাসিল আড়াইশো", ["DEMO-0034"], ["bn-script", "bn-number-words"], "bn", note="আড়াইশো = 250"),
    case("কমেট আটশো পঞ্চাশ", ["DEMO-0044"], ["bn-script", "bn-number-words"], "bn"),
    case("ওসারটিল ৫০", ["DEMO-0045"], "bn-script", "bn"),
    case("সিভিট", ["DEMO-0048"], "bn-script", "bn"),
    case("ক্যালবো ডি", ["DEMO-0049"], "bn-script", "bn"),
    case("নিউরো বি", ["DEMO-0050"], "bn-script", "bn"),
    case("অ্যাজমাসল ইনহেলার", ["DEMO-0052"], "bn-script", "bn"),
    case("অরস্যালাইন", ["DEMO-0053"], "bn-script", "bn"),
    case("ফিলমেট ৪০০", ["DEMO-0037"], "bn-script", "bn"),
    case("ফ্ল্যাজিল", ["DEMO-0039"], "bn-script", "bn"),
    case("অ্যামোডিস", ["DEMO-0038"], "bn-script", "bn"),
    case("প্যান্টোনিক্স চল্লিশ", ["DEMO-0019"], ["bn-script", "bn-number-words"], "bn"),
    case("ম্যাক্সপ্রো বিশ", ["DEMO-0015"], ["bn-script", "bn-number-words"], "bn"),
    case("সার্জেল ৪০", ["DEMO-0014"], "bn-script", "bn"),
    case("লোসেকটিল", ["DEMO-0012"], "bn-script", "bn"),
    case("অ্যালাট্রল সিরাপ", ["DEMO-0023"], "bn-script", "bn"),
    case("এন্টাসিড প্লাস", ["DEMO-0051"], "bn-script", "bn"),
]
# --- Bangla script for products WITHOUT a Bangla name in the catalog (transliteration path) ---------------------
C += [
    case("নেক্সাম ২০", ["DEMO-0017"], "bn-transliteration", "bn"),
    case("মন্টেয়ার ১০", ["DEMO-0027"], "bn-transliteration", "bn"),
    case("অ্যাজিথ্রোসিন", ["DEMO-0030"], "bn-transliteration", "bn"),
    case("সেফ থ্রি", ["DEMO-0036"], "bn-transliteration", "bn"),
    case("টাফনিল", ["DEMO-0040"], "bn-transliteration", "bn"),
    case("ক্লোফেনাক ৫০", ["DEMO-0041"], "bn-transliteration", "bn"),
    case("রোলাক", ["DEMO-0042"], "bn-transliteration", "bn"),
    case("অ্যামডোকাল", ["DEMO-0046"], "bn-transliteration", "bn"),
    case("ক্যামলোডিন", ["DEMO-0047"], "bn-transliteration", "bn"),
    case("নেবানল", ["DEMO-0054"], "bn-transliteration", "bn"),
    case("বেটনোভেট এন ক্রিম", ["DEMO-0055"], "bn-transliteration", "bn"),
    case("ফাস্ট ৫০০", ["DEMO-0009"], "bn-transliteration", "bn"),
]
# --- Banglish (romanised Bangla, with filler words) -------------------------------------------------------------
C += [
    case("napa extra dao", ["DEMO-0004"], "banglish", "banglish"),
    case("ek pata seclo 20 den", ["DEMO-0010"], "banglish", "banglish"),
    case("dui pata napa 500 lagbe", ["DEMO-0001"], "banglish", "banglish"),
    case("ace plus ache", ["DEMO-0008"], "banglish", "banglish"),
    case("fexo 120 ekta dao", ["DEMO-0020"], "banglish", "banglish"),
    case("seklo bish", ["DEMO-0010"], ["banglish", "bn-number-words"], "banglish"),
    case("napa pachsho", ["DEMO-0001"], ["banglish", "bn-number-words"], "banglish"),
    case("sergel chollish", ["DEMO-0014"], ["banglish", "bn-number-words"], "banglish"),
    case("monas dosh", ["DEMO-0024"], ["banglish", "bn-number-words"], "banglish"),
    case("alatrol syrap", ["DEMO-0023"], "banglish", "banglish"),
    case("napa sirap dao", ["DEMO-0002"], "banglish", "banglish"),
    case("zimax 500 er ekta pata", ["DEMO-0028"], "banglish", "banglish"),
    case("bhai ceevit ache", ["DEMO-0048"], "banglish", "banglish"),
    case("orsaline er packet", ["DEMO-0053"], "banglish", "banglish"),
]
# --- typed misspellings ---------------------------------------------------------------------------------------
T = dict(source="Typed")
C += [
    case("Nappa Extra", ["DEMO-0004"], "typo", **T),
    case("Napa Extand", ["DEMO-0005"], "typo", **T),
    case("seclow 20", ["DEMO-0010"], "typo", **T),
    case("Sargel 40", ["DEMO-0014"], "typo", **T),
    case("Maxpo 20", ["DEMO-0015"], "typo", **T),
    case("Pantonics 40", ["DEMO-0019"], "typo", **T),
    case("Fexxo 120", ["DEMO-0020"], "typo", **T),
    case("Monaas 10", ["DEMO-0024"], "typo", **T),
    case("Montaire 10", ["DEMO-0027"], "typo", **T),
    case("Zymax 500", ["DEMO-0028"], "typo", **T),
    case("Azitrocin", ["DEMO-0030"], "typo", **T),
    case("Ciprosin 500", ["DEMO-0031"], "typo", **T),
    case("Moxasil 500", ["DEMO-0033"], "typo", **T),
    case("Filmat", ["DEMO-0037"], "typo", **T),
    case("Flagile", ["DEMO-0039"], "typo", **T),
    case("Tufnill", ["DEMO-0040"], "typo", **T),
    case("Clofenak 50", ["DEMO-0041"], "typo", **T),
    case("Rollac", ["DEMO-0042"], "typo", **T),
    case("Komet 850", ["DEMO-0044"], "typo", **T),
    case("Osartel 50", ["DEMO-0045"], "typo", **T),
    case("Amdokal", ["DEMO-0046"], "typo", **T),
    case("Camlodine", ["DEMO-0047"], "typo", **T),
    case("Ceevitt", ["DEMO-0048"], "typo", **T),
    case("Nuero B", ["DEMO-0050"], "typo", **T),
    case("Entasid plus", ["DEMO-0051"], "typo", **T),
    case("Asmasol", ["DEMO-0052"], "typo", **T),
    case("Orsalin", ["DEMO-0053"], "typo", **T),
]
# --- ASR-style splits / mishearings of brand names (English recogniser) -------------------------------------------
C += [
    case("sec low twenty", ["DEMO-0010"], "asr-split"),
    case("max pro forty", ["DEMO-0016"], "asr-split"),
    case("zee max five hundred", ["DEMO-0028"], "asr-split"),
    case("film it four hundred", ["DEMO-0037"], "asr-split"),
    case("flag ill", ["DEMO-0039"], "asr-split"),
    case("see vit", ["DEMO-0048"], "asr-split"),
    case("mon us ten", ["DEMO-0024"], "asr-split"),
    case("pan tonics forty", ["DEMO-0019"], "asr-split"),
    case("osar till fifty", ["DEMO-0045"], "asr-split"),
    case("row lack", ["DEMO-0042"], "asr-split"),
    case("tough nil", ["DEMO-0040"], "asr-split"),
    case("cal bo d", ["DEMO-0049"], "asr-split"),
    case("neuro bee", ["DEMO-0050"], "asr-split"),
    case("nap a extra", ["DEMO-0004"], "asr-split"),
    case("sir gel twenty", ["DEMO-0013"], "asr-split"),
    case("loss ectil", ["DEMO-0012"], "asr-split"),
    case("next um twenty", ["DEMO-0017"], "asr-split"),
    case("mont air ten", ["DEMO-0027"], "asr-split"),
    case("amo dis", ["DEMO-0038"], "asr-split"),
    case("cam lo din", ["DEMO-0047"], "asr-split"),
    case("fax oh one eighty", ["DEMO-0021"], "asr-split"),
]
# --- recogniser n-best: primary hypothesis wrong, an alternative is right ----------------------------------------
C += [
    case("make sure pro forty", ["DEMO-0016"], "asr-nbest", alternatives=["maxpro forty"]),
    case("next to twenty", ["DEMO-0017"], "asr-nbest", alternatives=["nexum twenty"]),
    case("set low twenty", ["DEMO-0010"], "asr-nbest", alternatives=["সেকলো বিশ"]),
    case("নাপা এক্সটা", ["DEMO-0004"], "asr-nbest", "bn", alternatives=["napa extra"]),
    case("the mass ten", ["DEMO-0024"], "asr-nbest", alternatives=["monas ten"]),
    case("fix oh one twenty", ["DEMO-0020"], "asr-nbest", alternatives=["fexo one twenty"]),
]
# --- generic names ------------------------------------------------------------------------------------------------
C += [
    case("omeprazole 20", ["DEMO-0010", "DEMO-0012"], "generic"),
    case("esomeprazole 40", ["DEMO-0014", "DEMO-0016"], "generic"),
    case("paracetamol syrup", ["DEMO-0002", "DEMO-0007"], "generic"),
    case("paracetamol 500 tablet", ["DEMO-0001", "DEMO-0006", "DEMO-0009"], "generic"),
    case("montelukast 10", ["DEMO-0024", "DEMO-0027"], "generic"),
    case("azithromycin 500", ["DEMO-0028", "DEMO-0030"], "generic"),
    case("metronidazole 400", ["DEMO-0037", "DEMO-0038", "DEMO-0039"], "generic"),
    case("amlodipine 5", ["DEMO-0046", "DEMO-0047"], "generic"),
    case("cefixime", ["DEMO-0036"], "generic"),
    case("cetirizine syrup", ["DEMO-0023"], "generic"),
    case("salbutamol inhaler", ["DEMO-0052"], "generic"),
    case("fexofenadine 120", ["DEMO-0020"], "generic"),
    case("ciprofloxacin 250", ["DEMO-0032"], "generic"),
    case("losartan", ["DEMO-0045"], "generic"),
    case("metformin 850", ["DEMO-0044"], "generic"),
    case("diclofenac", ["DEMO-0041"], "generic"),
    case("ketorolac", ["DEMO-0042"], "generic"),
    case("tolfenamic acid", ["DEMO-0040"], "generic"),
    case("pantoprazole 40", ["DEMO-0019"], "generic"),
    case("amoxicillin 250 capsule", ["DEMO-0034"], "generic"),
    case("ascorbic acid", ["DEMO-0048"], "generic"),
    case("প্যারাসিটামল সিরাপ", ["DEMO-0002", "DEMO-0007"], ["generic", "bn-transliteration"], "bn"),
    case("ওমিপ্রাজল ২০", ["DEMO-0010", "DEMO-0012"], ["generic", "bn-transliteration"], "bn"),
    case("মন্টিলুকাস্ট ১০", ["DEMO-0024", "DEMO-0027"], ["generic", "bn-transliteration"], "bn"),
    case("মেট্রোনিডাজল ৪০০", ["DEMO-0037", "DEMO-0038", "DEMO-0039"], ["generic", "bn-transliteration"], "bn"),
    case("সিপ্রোফ্লক্সাসিন ৫০০", ["DEMO-0031"], ["generic", "bn-transliteration"], "bn"),
]
# --- quantities (must not be read as strengths) -------------------------------------------------------------------
C += [
    case("two strips napa extra", ["DEMO-0004"], "quantity"),
    case("দুই পাতা নাপা ৫০০", ["DEMO-0001"], "quantity", "bn"),
    case("এক বক্স সেকলো ২০", ["DEMO-0010"], "quantity", "bn"),
    case("3 pata ace plus", ["DEMO-0008"], "quantity", "banglish"),
    case("ten pieces fexo 120", ["DEMO-0020"], "quantity"),
    case("one bottle napa syrup", ["DEMO-0002"], "quantity"),
]
# --- upstream uncertainty caps (correct product, but the decision must be capped) -------------------------------
C += [
    case("napa extra", ["DEMO-0004"], "cap-low-asr-confidence", recognition_confidence=0.3, decisions=["LowConfidence"]),
    case("seclo 20", ["DEMO-0010"], "cap-speaker-uncertain", speaker_decision="Uncertain", decisions=["NeedsConfirmation"]),
    case("fexo 120", ["DEMO-0020"], "cap-speaker-uncertain", speaker_decision="NoProfile", decisions=["NeedsConfirmation"]),
]
# --- NEGATIVES: nothing in the catalog satisfies the request ------------------------------------------------------
C += [
    # requested strength / form does not exist for that brand
    case("napa 250", [], "neg-strength-mismatch", note="Napa exists, not 250 mg"),
    case("seclo 10", [], "neg-strength-mismatch"),
    case("fexo 60", [], "neg-strength-mismatch"),
    case("monas 20", [], "neg-strength-mismatch"),
    case("zimax 250", [], "neg-strength-mismatch"),
    case("comet 1000", [], "neg-strength-mismatch"),
    case("maxpro 10", [], "neg-strength-mismatch"),
    case("seclo syrup", [], "neg-form-mismatch"),
    case("fexo syrup", [], "neg-form-mismatch"),
    case("azmasol tablet", [], "neg-form-mismatch"),
    case("comet injection", [], "neg-form-mismatch"),
    # real-world brand names that are NOT in the demo catalog (some sound like catalog names)
    case("panadol", [], "neg-unknown-brand"),
    case("tylenol", [], "neg-unknown-brand"),
    case("brufen 400", [], "neg-unknown-brand"),
    case("omidon", [], "neg-unknown-brand"),
    case("nexium 20", [], "neg-unknown-brand", note="close to Nexum 20, but a different brand"),
    case("esoral 20", [], "neg-unknown-brand"),
    case("rupa 10", [], "neg-unknown-brand"),
    case("tusca syrup", [], "neg-unknown-brand"),
    case("histacin", [], "neg-unknown-brand"),
    case("deslor", [], "neg-unknown-brand"),
    case("ventolin inhaler", [], "neg-unknown-brand"),
    case("augmentin 625", [], "neg-unknown-brand"),
    # speech that is not a medicine request
    case("hello", [], "neg-not-medicine"),
    case("thank you", [], "neg-not-medicine"),
    case("how much is it", [], "neg-not-medicine"),
    case("ki khobor bhai", [], "neg-not-medicine", "banglish"),
    case("আজকে বৃষ্টি হবে", [], "neg-not-medicine", "bn"),
    case("দাম কত", [], "neg-not-medicine", "bn"),
    case("ধন্যবাদ", [], "neg-not-medicine", "bn"),
    case("wait a minute", [], "neg-not-medicine"),
    case("xyzzy", [], "neg-gibberish"),
    case("blorptan 500", [], "neg-gibberish"),
]

out = pathlib.Path(__file__).with_name("demo-authored.cases.jsonl")
with out.open("w", encoding="utf-8") as f:
    for i, c in enumerate(C, 1):
        c = {"id": f"demo-{i:03d}", **c}
        f.write(json.dumps(c, ensure_ascii=False) + "\n")
print(f"{len(C)} cases -> {out.name}")
