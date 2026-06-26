# Changelog

Histórico de evolução do **FlatPatternHighlight** — plugin NXOpen C#/.NET para análise de flat pattern de Sheet Metal no Siemens NX 2512.

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).

---

## [Unreleased]

### Adicionado
- **Documentação refinada** — comentários inline detalhados nas heurísticas críticas do código:
  - Estrutura da tupla `bendInfos` (23 campos) documentada campo a campo
  - Tracking de múltiplos candidatos (`bestIdx`/`secondBestIdx`/`farIdx`/`nearIdx`/`bestLineIdx`/`farLineIdx`) com explicação do *porquê* de cada um
  - Heurística de skip de cutout (ratio 0,3) explicada
  - Fallback de dobras diagonais (threshold 0,2) explicado
  - Correção small-edge (`SmallEdgeRatio = 0,5`) explicada
  - Cascata de fallback PMI (indicator-line → point-fallback) documentada nos catch blocks
- **CHANGELOG.md** — este arquivo
- **README** atualizado:
  - Line count corrigido (800+ → 1450)
  - Seção "Chain PMI Dimensioning" reescrita para refletir o algoritmo atual ("nearest corrected cross-side" + skip de cutout)
  - Tabela da cascata de fallback PMI
  - Step 3 expandido com multi-candidate tracking, correção small-edge e dobras diagonais
  - Seção Changelog adicionada

---

## [0.4.0] — 2026-06-26

### Corrigido
- **Erro de compilação**: variável `boundaryIdx` usada sem declaração no método `CreateChainSide` — declarada como `int boundaryIdx = -1` antes do bloco de seleção.
- **Erro de compilação**: `outerPerim` referenciado em `CreateChainSide` sem ser parâmetro — adicionado `List<Curve> outerPerim` à assinatura e atualizadas as chamadas em `CreateChainForGroup`.

### Alterado
- **Unificação da seleção de boundary** — todos os bends agora usam o mesmo método "nearest corrected cross-side":
  - Parte de `bestIdxA`/`bestIdxB` (curva paralela mais próxima de cada lado)
  - Skip de cutout: se `best/secondBest < 0,3`, troca para `secondBest` (a "mais próxima" era um recorte fino)
  - Preferência `Line` sobre `Arc` (Arc causa erro NX 1175009 no builder perpendicular)
  - Fallback final para `nearIdx` (curva mais próxima sem filtro de paralelismo)

---

## [0.3.0] — 2026-06-26

Evolução iterativa do tratamento de **dobras diagonais** (componente significativa em ambos os eixos do plano UV). Várias tentativas até chegar ao comportamento estável:

### Tentativas (commits individuais)
- `diagonal bends now use nearest (not side-classified) parallel perimeter` — abandona classificação por lado
- `diagonal bend boundary searches for parallel Line across BOTH sides` — busca Line em ambos os lados
- `diagonal bends use farthest parallel (farIdx/farLineIdx) to skip cutout edges` — usa mais distante para pular cutouts
- `revert diagonal bends to nearest parallel (bestIdx/bestLineIdx)` — reverte para mais próxima
- `skip cutout edges via secondBestIdx heuristic for diagonal bends` — introduz o `secondBestIdx`
- `diagonal bend uses secondBest on the correct chain side, falls back to other side` — corrige o lado da cadeia

### Resultado
Dobras diagonais (|dir.X|>0,2 E |dir.Y|>0,2) relaxam o filtro de paralelismo: preferem `Line` paralela, caem para `nearIdx` (sem filtro) como último recurso.

### Removido
- **Bridge dimension** — removido inteiramente por ser redundante (`remove bridge dimension entirely`). Antes já tinha sido skipado quando há 1 dobra por lado.

---

## [0.2.0] — 2026-06-26

### Adicionado
- **PmiRapidDimensionBuilder** para cotas PMI em cadeia — substitui a abordagem manual de `DimensionData`.
  - `MeasurementMethod.Perpendicular` referencia 2 Curves reais → NX calcula a distância perpendicular geométrica verdadeira (funciona para horizontais, verticais e diagonais)
  - Cascata de fallback:
    - Indicator-line vermelha (cor 36) quando boundary é `Arc` (erro 1175009)
    - Indicator-line vermelha quando sem licença PMI/GD&T (erro 948802)
    - Point-fallback (cota horizontal/vertical entre 2 Points auxiliares blanked) em outros erros do builder
- **Skip de artefato de perímetro** — dobras cujo midpoint 3D fica a <0,5 mm do perímetro são puladas (NX às vezes retorna bordas como "bend lines")
- **Bridge dimensions** entre dobras consecutivas da cadeia

### Corrigido
- **Dobras inclinadas** agora recebem cota via fallback de perímetro mais próximo (`inclined bend lines now get dimensioned via nearest-perimeter fallback`)
- **Preferência por Line** (não Arc) no boundary de dobras inclinadas

---

## [0.1.0] — 2026-06-24

Versão inicial do plugin — análise em 3 etapas sequenciais.

### Adicionado
- **Step 1 — Outer Perimeter Filtering** — reduz ~74 curvas externas brutas (incl. notches/cutouts) ao contorno externo verdadeiro via `UF_MODL_ask_face_loops` (P/Invoke em `libufun.dll`), filtrando loops externos (type=1) vs internos (type=2)
  - `FindFlatSolidBody` com 3 estratégias: `GetBodies()`, `GetEntities()`, busca exaustiva em `workPart.Bodies` por feature `FlatPattern`/`FlatSolid`
  - Fallback: usa todas as 74 curvas se o body não for encontrado
- **Step 2 — Bend Center Lines** — enumera `GetBendUpCenterLines`/`GetBendDownCenterLines` com highlight e log
- **Step 3 — Bend-to-Perimeter Proximity** — para cada dobra:
  - `DetectNormalAxis` — detecta o plano do flat pattern (XY/XZ/YZ) pelo spread de endpoints
  - Bounding box 2D no plano UV
  - Filtro paralelo (dot > 0,95) + filtro de overlap
  - nearest/farIdx por lado + distância ao bbox via ray-cast
- **Lane Clustering** — agrupa dobras paralelas com overlap em flanges independentes (`LaneLengthRatioThreshold = 0,7`)
- **Chain PMI Dimensioning** inicial com `DimensionData`/`Associativity` manual
- **Arquivo de menu** (`.men`) com sintaxe `ACTIONS NXOpen::FlatPatternHighlight.HighlightFlatPattern::Main` — chama o método C# diretamente, sem `AddMenuAction()`
- **Arquivo de ribbon** (`.rtb`) — aba "Flat Pattern" no ribbon do Modeling
- **Script de build** (`build.ps1`) — patch de HintPath, build, assinatura com `SignDotNet.exe`
- **README** completo com algoritmos, interpretação de resultados, limitações e próximos passos
- **Documentação XML** em todos os métodos + comentários inline

### Corrigido (durante 0.1.0)
- `.rtb`: `TITLE` antes de `VERSION` (exigido pelo parser do NX); removido `BITMAP` inválido
- `build.ps1`: parsing de string com em-dash unicode
- Assinaturas de entry point do ribbon para NX 2512

---

## Notas sobre versionamento

As versões são atribuídas retroativamente para organizar o histórico. Os commits reais não carregam tags de versão. Agrupamento:

| Versão | Foco | Data |
|---|---|---|
| `0.1.0` | Funcionalidade base (3 steps + menu/ribbon) | 2026-06-24 |
| `0.2.0` | PmiRapidDimensionBuilder + fallbacks + dobras inclinadas | 2026-06-26 |
| `0.3.0` | Tratamento iterativo de dobras diagonais + remoção de bridge | 2026-06-26 |
| `0.4.0` | Unificação da seleção de boundary + fix de compilação | 2026-06-26 |
| `Unreleased` | Documentação refinada | 2026-06-26 |
