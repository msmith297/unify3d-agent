# Mold data generators (not shipped)

Everything in `tools/builtin/design-compression-mold/data/` that is data, plus the compiled sizing defaults and the
provisional coefficient table, is generated here. Run each with `python -I` (no site packages, nothing from the current
folder is imported implicitly); `--check` compares with the shipped file and exits 1 when they differ.

| Script | Writes | Notes |
|---|---|---|
| `gen_materials.py` (+ `materials_post.py`) | `data/materials.json` | 96 part-material rows (research data, unchanged) + `processClass`, `processLabel`, `builtByV1`, `short`/`familyShort`, `isDefault`, `shrinkBasisChoice`, `shrinkBasisConfidence`, `shrinkPctAxis`; hot-cast PU rows retagged to the hot-cavity shrink basis (critique A5). Validates ids, key sets, ranges, one default per family, every page Fills path. |
| `build_mold_materials.py` (+ `mold_materials_post.py`) | `data/mold-materials.json` | 16 grades + hardware; adds `short`, `use`, `isDefault`, `swMaterial`, `skimPerFace_mm`, `stockKey`, `agesDimensionally`, the D18 6061 fatigue curve, hardened washers, minor diameters and stock screw lengths (critique E2). |
| `gen_defaults.py` | `data/shop-standards.json`, `data/sizing-defaults.json`, `src/SwAgent/Mold/SizingDefaults.Builtin.cs` | The shop's rules and every judgement factor, with the critique values (A1 scatter + hot proof, A2 lifting, A8 creep divisor, A11 gap budget, A12 shut-off pins, D1 slide-in dT, D3 pour rate). A unit test asserts the compiled copy equals the JSON. |
| `examples_check.py` | `data/validation/examples.json` | Independent reference for the worked examples E1-E11 (design 3.19 re-derived with the critique). E11 is derived by hand in the script header and asserted. Re-run after `coefficients.csv` changes. |
| `gen_coefficients.py` (+ `proto_plate.py`) | stdout -> `src/SwAgent/Mold/Structural/coefficients.csv` | Provisional MITC4 table from the research prototype (needs numpy, ~2 min), header `# provisional`. Track A2's `tools/dev/MoldSweep coefficients` replaces it with the same columns. |

Typical run after editing data:

```
python -I tools/dev/mold-data/gen_materials.py
python -I tools/dev/mold-data/build_mold_materials.py
python -I tools/dev/mold-data/gen_defaults.py
python -I tools/dev/mold-data/examples_check.py
dotnet test tests/SwAgent.Tests --filter FullyQualifiedName~Mold
```
