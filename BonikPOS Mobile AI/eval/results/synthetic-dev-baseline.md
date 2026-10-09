### Baseline (before search changes), synthetic-20k DEV

- Dataset: `synthetic-20k-dev.cases.jsonl` (sha256 `008167bd913491cf…`), 2600 cases: 2000 positive, 600 negative
- Catalog: Local product index (synthetic-catalog-20k.json); 20000 products (sha256 `539870d42230de34…`); index build 934 ms
- Run: 2026-10-09 07:52 UTC on Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12
- Thresholds: min candidate 0.55, confident 0.90, ambiguity margin 0.06, min recognition confidence 0.45

| Metric | Value |
|---|---|
| Top-1 accuracy (positives) | 99.0 % (1980/2000) |
| Top-5 accuracy (positives) | 99.3 % (1985/2000) |
| False matches (all cases) | 1.0 % (26/2600) |
| — on positive cases | 0.1 % (2/2000) |
| — on negative cases | 4.0 % (24/600) |
| False matches with decision Confident | 0.1 % (2/2600) |
| Wrong product offered (false match or all-wrong Ambiguous), all cases | 6.5 % (170/2600) |
| — on negative cases | 27.2 % (163/600) |
| Correct top-1 but answered LowConfidence/NoMatch (positives) | 0.4 % (8/2000) |
| Negatives safely handled (NoMatch/LowConfidence) | 72.8 % (437/600) |
| Negatives answered NoMatch | 0.8 % (5/600) |
| Positives answered NoMatch (missed) | 0.2 % (4/2000) |
| Decision agreement (cases with expected decision) | 100.0 % (98/98) |
| Decisions | Confident 1214, NeedsConfirmation 420, Ambiguous 510, LowConfidence 447, NoMatch 9 |
| Matcher latency p50 / p95 / max | 2.08 / 6.12 / 70.88 ms |

| Category | Cases | Top-1 | Top-5 | False matches | Wrong offered | Negatives safe |
|---|---:|---:|---:|---:|---:|---:|
| asr-split | 222 | 99 % (219/222) | 99 % (219/222) | 1 | 2 | – |
| bangla-script | 318 | 98 % (311/318) | 99 % (314/318) | 1 | 1 | – |
| banglish-respelling | 265 | 97 % (256/265) | 97 % (257/265) | 0 | 4 | – |
| brand-form | 213 | 100 % (213/213) | 100 % (213/213) | 0 | 0 | – |
| brand-only | 174 | 100 % (174/174) | 100 % (174/174) | 0 | 0 | – |
| brand-strength | 466 | 100 % (466/466) | 100 % (466/466) | 0 | 0 | – |
| generic-strength | 111 | 100 % (111/111) | 100 % (111/111) | 0 | 0 | – |
| neg-form-mismatch | 150 | – | – | 6 | 12 | 92 % (138/150) |
| neg-not-medicine | 30 | – | – | 3 | 25 | 17 % (5/30) |
| neg-strength-mismatch | 246 | – | – | 1 | 8 | 97 % (238/246) |
| neg-unknown-brand | 174 | – | – | 14 | 118 | 32 % (56/174) |
| typo | 231 | 100 % (230/231) | 100 % (231/231) | 0 | 0 | – |

Failures (183; wrong or missing top-1, false matches, unexpected decisions) — first 60:

- `syn-00001` "ceeprai data five" → NoMatch; correct product not ranked; top: (none)
- `syn-00066` "pupris ten mg" → LowConfidence; correct product not ranked; top: SYN-013957 Bupriil 10 mg Tablet [0.69]
- `syn-00178` "khohine 0.1" → LowConfidence; correct product not ranked; top: SYN-011797 Cosine 300 mg Tablet [0.65]
- `syn-00230` "pespir 0.9" → Ambiguous; wrong products offered as a choice; top: SYN-004783 Spespiil 0.9% Infusion [0.74]
- `syn-00328` "পীব্রুন একশো ইনহেলার" → LowConfidence; correct product not ranked; top: SYN-006882 Pibrouc 100 mg Sustained Release Tablet [0.58]
- `syn-00619` "do guk five" → Ambiguous; wrong products offered as a choice; top: SYN-016975 Gukane 125 mg/5 ml Powder for Suspension [0.85]
- `syn-00837` "জিরো চার" → NoMatch; correct product not ranked; top: (none)
- `syn-00951` "mosuol 100 mcg" → LowConfidence; correct at rank 3; top: SYN-008184 Moseeol 600 mg + 400 IU Tablet [0.69]
- `syn-01030` "স্পোসী বিশ" → Ambiguous; correct at rank 3; top: SYN-001689 Speshi 20 mg Capsule [0.91]
- `syn-01094` "do hir five hundred" → NeedsConfirmation; FALSE MATCH; top: SYN-007052 Hiraiin 500 mg Tablet [0.84]
- `syn-01175` "phahpak 500" → LowConfidence; correct at rank 3; top: SYN-009355 Foheepaac 200 mg + 0.2 mg Capsule [0.73]
- `syn-01177` "ফাজে ১০০" → Ambiguous; correct at rank 2; top: SYN-000427 Baje 100 mg + 200 mg + 200 mcg Tablet [0.82]
- `syn-01208` "টীনিডীটা ১০" → NoMatch; correct product not ranked; top: (none)
- `syn-01287` "ডৌজীড একশো সিরাপ" → NeedsConfirmation; FALSE MATCH; top: SYN-014047 Dojiid 100 mg/5 ml Powder for Suspension [0.88]
- `syn-01617` "khile 80 milligram" → NoMatch; correct product not ranked; top: (none)
- `syn-01639` "khetaik 30 mg" → LowConfidence; correct product not ranked; top: SYN-015296 Keeshutaic 2 mg Tablet [0.56]
- `syn-01829` "cuhior 500" → Ambiguous; wrong products offered as a choice; top: SYN-012796 Tujior 500 mg + 125 mg Tablet [0.65]
- `syn-01918` "sleplis 500 milligram" → Ambiguous; wrong products offered as a choice; top: SYN-015774 Flaiplis 500 mg Tablet [0.72]
- `syn-01940` "গ্লীশীন আশি" → LowConfidence; correct product not ranked; top: SYN-005696 Glishina 200 ml Suspension [0.78]
- `syn-01992` "rudien five" → Ambiguous; wrong products offered as a choice; top: SYN-012721 Feedaien 3.35 g/5 ml Syrup [0.62]
- `syn-02006` "Hibotreeid 10 mg" → Ambiguous; wrong products offered as a choice; top: SYN-017125 Notreeix 10 mg Tablet [0.59]
- `syn-02007` "thank you very much" → NeedsConfirmation; FALSE MATCH; top: SYN-006865 Muchim 40 mg Tablet [0.79]
- `syn-02012` "where is my phone" → Ambiguous; wrong products offered as a choice; top: SYN-017826 Phones 100 mg Tablet [0.83]
- `syn-02017` "Pleefet 200" → Ambiguous; wrong products offered as a choice; top: SYN-003404 Prepheet 100 mg + 200 mg + 200 mcg Tablet [0.90]
- `syn-02018` "Gaipeeor" → Ambiguous; wrong products offered as a choice; top: SYN-003741 Gaifeera 200 mg + 0.2 mg Capsule [0.72]
- `syn-02019` "Neemilaiex" → Ambiguous; wrong products offered as a choice; top: SYN-003541 Nelaiax 30 mg Tablet [0.75]
- `syn-02021` "Bisto" → NeedsConfirmation; FALSE MATCH; top: SYN-010667 Visita 150 mg Capsule [0.82]
- `syn-02022` "give me the change" → Ambiguous; wrong products offered as a choice; top: SYN-017656 Chanuc 100 mg Sustained Release Tablet [0.66]
- `syn-02024` "Phacletaion 500" → Ambiguous; wrong products offered as a choice; top: SYN-008297 Gletion 500 mg + 65 mg Tablet [0.67]
- `syn-02027` "Stepreeta 500" → NeedsConfirmation; FALSE MATCH; top: SYN-017501 Steeprut 500 mg Capsule [0.88]
- `syn-02031` "card e dibo" → Ambiguous; wrong products offered as a choice; top: SYN-014420 Dibora 50 mg Capsule [0.79]
- `syn-02032` "ki khobor" → NeedsConfirmation; FALSE MATCH; top: SYN-010734 Kovaor 2.5 mg/5 ml Syrup [0.90]
- `syn-02038` "taka nai" → Ambiguous; wrong products offered as a choice; top: SYN-008362 Taica 250 mg Chewable Tablet [0.81]
- `syn-02041` "Traijion 50" → Ambiguous; wrong products offered as a choice; top: SYN-010789 Traizain 50 mg Tablet [0.88]
- `syn-02042` "Flatroura 20" → Ambiguous; wrong products offered as a choice; top: SYN-012096 Floutrol 20 mg/ml Injection [0.69]
- `syn-02043` "Rogaiax" → NeedsConfirmation; FALSE MATCH; top: SYN-014497 Rugaiix 1% Cream [0.87]
- `syn-02046` "Muslisi" → Ambiguous; wrong products offered as a choice; top: SYN-004427 Choslis 0.5% + 500 IU Ointment [0.66]
- `syn-02048` "close the door" → Ambiguous; wrong products offered as a choice; top: SYN-016694 Clocee 250 mg Tablet [0.89]
- `syn-02050` "Pigoon 500" → Ambiguous; wrong products offered as a choice; top: SYN-015351 Pizeon 500 mg Tablet [0.79]
- `syn-02058` "Phocloix" → Ambiguous; wrong products offered as a choice; top: SYN-001976 Phiclouix 250 mg Chewable Tablet [0.89]
- `syn-02065` "Brijax 10 mg" → Ambiguous; wrong products offered as a choice; top: SYN-004780 Breesuex 10 mg Tablet [0.66]
- `syn-02067` "আচ্ছা ঠিক আছে" → Ambiguous; wrong products offered as a choice; top: SYN-012105 Chatrik 10 mg Tablet [0.71]
- `syn-02069` "see you tomorrow" → Ambiguous; wrong products offered as a choice; top: SYN-015619 Seetro 500 mg Capsule [0.79]
- `syn-02070` "Staivaan 500" → Ambiguous; wrong products offered as a choice; top: SYN-013606 Staivaan Plus 500 mg Tablet [0.87]
- `syn-02074` "Plephouac 500" → NeedsConfirmation; FALSE MATCH; top: SYN-002852 Pliphac 500 mg Capsule [0.79]
- `syn-02075` "Clato 20" → Ambiguous; wrong products offered as a choice; top: SYN-001358 Clastuol 20 mg/ml Injection [0.61]
- `syn-02076` "bill koto holo" → Ambiguous; wrong products offered as a choice; top: SYN-011557 Kotoulaior 5 mg/5 ml Suspension [0.75]
- `syn-02077` "Pleeleax" → NeedsConfirmation; FALSE MATCH; top: SYN-008606 Plouleeax 0.3% Eye Drops [0.89]
- `syn-02084` "Naipaix tablet" → Ambiguous; wrong products offered as a choice; top: SYN-008300 Naisaix 150 mg Tablet [0.84]
- `syn-02087` "how much is this" → Ambiguous; wrong products offered as a choice; top: SYN-006865 Muchim 40 mg Tablet [0.79]
- `syn-02095` "Jephion 10 mg" → Ambiguous; wrong products offered as a choice; top: SYN-007078 Jeephod 10 mg Tablet [0.67]
- `syn-02098` "Trepraigool 10 mg" → Ambiguous; wrong products offered as a choice; top: SYN-013403 Treepleol 10 mg Tablet [0.70]
- `syn-02099` "ar kichu lagbe" → Ambiguous; wrong products offered as a choice; top: SYN-018054 Dichu 100 mg Vaginal Tablet [0.78]
- `syn-02100` "bhai ektu daran" → Ambiguous; wrong products offered as a choice; top: SYN-005180 Douraan 200 mg/5 ml Powder for Suspension [0.76]
- `syn-02102` "Roplo" → Ambiguous; wrong products offered as a choice; top: SYN-019705 Ropel 100 mg/5 ml Suspension [0.79]
- `syn-02105` "hello" → Ambiguous; wrong products offered as a choice; top: SYN-003582 Hela 250 mg Capsule [0.88]
- `syn-02107` "ধন্যবাদ" → Ambiguous; wrong products offered as a choice; top: SYN-016088 Mivad 0.5 mg Tablet [0.57]
- `syn-02108` "Haplidoor 500" → Ambiguous; wrong products offered as a choice; top: SYN-010390 Haflit 500 mg Tablet [0.61]
- `syn-02110` "Niflaphoid" → Ambiguous; wrong products offered as a choice; top: SYN-006715 Flaveid 40 mg Injection [0.66]
- `syn-02111` "change nai" → Ambiguous; wrong products offered as a choice; top: SYN-003398 Naijas 500 mg Tablet [0.79]
