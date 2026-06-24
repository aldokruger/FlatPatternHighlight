# FlatPatternHighlight

**NXOpen C#/.NET plugin** para análise automatizada de **flat pattern** de Sheet Metal no Siemens NX 2512.

Substitui a contagem manual de dobras e a identificação visual do contorno externo por uma análise em 3 etapas, com saída diagnóstica no Log File do NX e criação opcional de cotas PMI em cadeia.

---

## Índice

- [Problema](#problema)
- [Fluxo de Análise (3 Steps)](#fluxo-de-análise-3-steps)
- [Estrutura do Projeto](#estrutura-do-projeto)
- [Dependências NX](#dependências-nx)
- [Build](#build)
- [Execução](#execução)
- [Algoritmos em Detalhe](#algoritmos-em-detalhe)
  - [Step 1 — Outer Perimeter Filtering](#step-1--outer-perimeter-filtering)
  - [Step 2 — Bend Center Lines](#step-2--bend-center-lines)
  - [Step 3 — Bend-to-Perimeter Proximity](#step-3--bend-to-perimeter-proximity)
  - [Lane Clustering](#lane-clustering)
  - [Chain PMI Dimensioning](#chain-pmi-dimensioning)
- [Formato do Log File](#formato-do-log-file)
- [Interpretação dos Resultados](#interpretação-dos-resultados)
- [Limitações Conhecidas](#limitações-conhecidas)
- [Próximos Passos](#próximos-passos)

---

## Problema

Em peças de chapa dobrada (Sheet Metal), o flat pattern gerado pelo NX contém:

- **74+ curvas externas** (`GetExteriorCurves`) que incluem **recortes internos**, **alívios**, **notches** e **furos** — não apenas o contorno externo real.
- **14+ linhas de centro de dobra** que precisam ser correlacionadas com o perímetro externo para determinar qual lado de cada dobra aponta para a borda da peça.
- Processo manual: contar ~12 dobras e inspecionar visualmente o contorno → sujeito a erro e inconsistente.

O plugin resolve isso com 3 etapas automáticas e determinísticas.

---

## Fluxo de Análise (3 Steps)

```
Main()
  │
  ├─ FindFlatPattern() ─── localiza a Feature FlatPattern
  │
  ├─ [STEP 1] HighlightOuterPerimeter()
  │   ├─ GetExteriorCurves()         → 74 curvas brutas
  │   ├─ FindFlatSolidBody()         → 3 estratégias de busca
  │   ├─ UF_MODL_ask_face_loops()    → P/Invoke: outer loops das faces
  │   └─ outerPerim                  → somente curvas do contorno externo verdadeiro
  │
  ├─ [STEP 2] HighlightBendCenterLines()
  │   ├─ GetBendUpCenterLines()      → linhas de centro (up)
  │   ├─ GetBendDownCenterLines()    → linhas de centro (down)
  │   └─ bendLines                   → lista completa
  │
  └─ [STEP 3] AnalyzeBendToPerimeter()
      ├─ DetectNormalAxis()          → plano do flat pattern (XY, XZ ou YZ)
      ├─ Bounding box das curvas de perímetro
      ├─ Para cada bend line:
      │   ├─ Direção + normal perpendicular
      │   ├─ Filtra curvas paralelas (dot > 0.95) com overlap
      │   ├─ nearest = curva de perímetro mais próxima em cada lado
      │   ├─ farIdx  = curva de perímetro mais distante em cada lado
      │   └─ bboxDist = distância até a borda do bounding box
      ├─ [SUBPASSO] Lane Clustering
      │   └─ Agrupa dobras paralelas com overlap em "flanges" independentes
      └─ [SUBPASSO] Chain PMI Dimensions
          └─ Cria cotas PMI: borda → 1ª dobra → 2ª dobra → ...
```

---

## Estrutura do Projeto

```
FlatPatternHighlight/
├── FlatPatternHighlight.cs      # Código fonte (800+ linhas, completamente documentado)
├── FlatPatternHighlight.csproj  # Projeto .NET Framework 4.8 / x64
├── FlatPatternHighlight.men     # Arquivo de menu NX (antes do Help)
├── build.ps1                    # Script de build + assinatura
├── .gitignore                   # Exclui bin/, obj/, *.prt, *.pdf, dados de teste
└── README.md                    # Esta documentação
```

### Arquivo `.men` — Registro no Menu

O arquivo `FlatPatternHighlight.men` registra o plugin no menu do NX:

```
BEFORE UG_HELP
  CASCADE_BUTTON "FLAT_PATTERN_MENU"
  LABEL "Flat Pattern"
    MENU "FLAT_PATTERN_MENU"
      BUTTON "FLAT_PATTERN_HIGHLIGHT"
      LABEL "Highlight Exterior Curves"
      ACTIONS "FLAT_PATTERN_HIGHLIGHT"
    END_OF_MENU
END_OF_BEFORE
```

O menu aparece como **Flat Pattern → Highlight Exterior Curves** antes do menu Help.

### Arquivo `.csproj` — Configuração do Projeto

| Propriedade     | Valor          |
|-----------------|----------------|
| TargetFramework | `net48`        |
| Platform        | `x64`          |
| References      | NXOpen.dll, NXOpenUI.dll, NXOpen.Utilities.dll |
| EmbeddedResource| NXSigningResource.res (cópia local) |

---

## Dependências NX

| Recurso                  | Caminho (típico)                        | Uso                          |
|--------------------------|-----------------------------------------|------------------------------|
| `NXOpen.dll`             | `%UGII_BASE_DIR%\NXBIN\managed\`        | API gerenciada principal     |
| `NXOpenUI.dll`           | `%UGII_BASE_DIR%\NXBIN\managed\`        | UI, MessageBox, Menu         |
| `NXOpen.Utilities.dll`   | `%UGII_BASE_DIR%\NXBIN\managed\`        | JAM (UF call wrappers)       |
| `libufun.dll`            | `%UGII_BASE_DIR%\NXBIN\`                | P/Invoke nativo (loop query) |
| `NXSigningResource.res`  | `%UGII_BASE_DIR%\UGOPEN\`               | Assinatura do assembly       |

---

## Build

### Pré-requisitos

- .NET SDK ≥ 6.0 (para buildar projects net48)
- NX 2512 instalado em `D:\NX2512` (ou caminho personalizado)
- PowerShell 5.1+

### Comando

```powershell
.\build.ps1 -NxDir D:\NX2512 -Configuration Debug
```

Parâmetros:

| Parâmetro      | Default                              | Descrição                      |
|----------------|--------------------------------------|--------------------------------|
| `-NxDir`       | Auto-detecta (D:\NX2512, C:\Program Files\Siemens\NX2512) | Caminho da instalação NX |
| `-Configuration` | Release                           | Debug ou Release               |
| `-DeployDir`   | `$env:UGII_USER_DIR\startup`         | Pasta de deploy (auto-load)   |

### O Script Faz

1. **Verifica** pré-requisitos (dotnet, DLLs NX, signing resource)
2. **Copia** `NXSigningResource.res` da instalação NX para a pasta do projeto
3. **Patenteia** os HintPaths no `.csproj` com o caminho NX detectado
4. **Builda** com `dotnet build`
5. **Assina** com `SignDotNet.exe` (falha silenciosa se não houver licença `DotNet Author License`)
6. **Exibe** instruções de instalação

### Saída

```
DLL:      D:\Projetos\FlatPatternHighlight\bin\Debug\FlatPatternHighlight.dll
Size:     16 KB
```

---

## Execução

### Via Ctrl+U (Recomendado — sempre funciona)

1. Abra um part com Sheet Metal e flat pattern já criado
2. **File → Execute → NX Open** (ou `Ctrl+U`)
3. Selecione `FlatPatternHighlight.dll`
4. Os 3 steps executam em sequência, com highlights visuais e saída no Log File

### Via Menu (requer assinatura)

1. Copie `FlatPatternHighlight.dll` e `FlatPatternHighlight.men` para uma pasta `startup` do NX:
   ```powershell
   Copy-Item "bin\Debug\FlatPatternHighlight.dll" "$env:UGII_USER_DIR\startup\"
   Copy-Item "FlatPatternHighlight.men" "$env:UGII_USER_DIR\startup\"
   ```
2. Reinicie o NX
3. O menu **Flat Pattern → Highlight Exterior Curves** aparece antes do Help
4. A assinatura requer a licença `DotNet Author License` no servidor NX

### Para Remover Cotas PMI Geradas

As cotas e pontos auxiliares são marcados com o atributo de usuário `"FlatPatternHighlight"`. Para remover:

1. **PMI → Drafting → Annotation → Delete Annotations by User Attribute**
2. Filtro: `"FlatPatternHighlight" = "true"`
3. Delete

---

## Algoritmos em Detalhe

### Step 1 — Outer Perimeter Filtering

**Problema:** `GetExteriorCurves()` retorna ~74 curvas que incluem contornos de notches, alívios, furos oblongos etc.

**Solução:** Acessar a topologia de loops do solid body via `UF_MODL_ask_face_loops` (P/Invoke para `libufun.dll`).

```csharp
UF_MODL_ask_face_loops(faceTag, out loopList)
  → loop type = 1 (outer), 2 (inner)
  → outer loop edges = contorno externo verdadeiro
```

**Busca do Flat Solid Body** (`FindFlatSolidBody`):

| # | Estratégia                          | Confiabilidade |
|---|-------------------------------------|----------------|
| 1 | `flatPattern.GetBodies()`           | Média (alguns parts retornam null) |
| 2 | `flatPattern.GetEntities()` → cast Body | Média |
| 3 | `workPart.Bodies` + `body.GetFeatures()` match `FlatPattern` ou `FlatSolid` | Alta (cobre todos os casos) |

Se o body não for encontrado (ocorre em certos templates de part), **todas as 74 curvas** são usadas como perímetro (fallback).

### Step 2 — Bend Center Lines

Enumera as linhas de centro:

```csharp
flatPattern.GetBendUpCenterLines()   → bend up (dobra para cima)
flatPattern.GetBendDownCenterLines() → bend down (dobra para baixo)
```

Cada linha é logada com Tag, comprimento e direção UP/DOWN.

### Step 3 — Bend-to-Perimeter Proximity

Para cada linha de dobra:

1. **DetectNormalAxis** — Descobre o plano do flat pattern medindo o spread das coordenadas X/Y/Z de todos os endpoints. O eixo com menor range é a normal do plano. Isso funciona para flat patterns em XY, XZ ou YZ.

2. **Bounding box** 2D no plano UV (os dois eixos com spread > 0).

3. **Para cada bend line:**
   - Direção unitária + normal perpendicular ($-dy, dx$)
   - Midpoint no plano UV
   - Varre as curvas de perímetro:
     - **Filtro paralelo**: dot product > 0.95 (rejeita arcos e curvas perpendiculares)
     - **Filtro de overlap**: projeta os endpoints da curva de perímetro na direção da bend line e verifica se há sobreposição
   - Projeta o midpoint de cada curva candidata na normal → lado positivo (A) ou negativo (B)
   - Seleciona a curva **mais próxima** (nearest) e a **mais distante** (farIdx) em cada lado
   - **Heurística de qualidade**: se a curva mais distante for muito curta (<50% da curva mais longa no mesmo lado), troca para a mais longa — evita que notches pequenos sejam identificados como borda externa

4. **Distância ao BBox**: ray-cast do midpoint em cada direção normal até encontrar a borda do bounding box.

**Saída por bend:**

```
Bend[0] Tag=66084  Mid=(832,1,76,8)  Dir=(1,000,0,000)  Len=1662,2
  Side A (nml+): nearest=    2,12  bboxDist=   39,67  perimTag=66110
  Side B (nml-): nearest=    0,12  bboxDist= 1145,29  perimTag=66105
```

### Lane Clustering

**Problema:** Várias dobras paralelas podem pertencer a flanges diferentes no mesmo lado da peça. Por exemplo, uma aba lateral tem 3 dobras paralelas, enquanto a aba oposta tem outras 3 — dimensionar tudo como uma única cadeia misturaria geometrias disjuntas.

**Solução:**

1. **Agrupa por direção** — dobras aproximadamente paralelas (dot ≥ 0.95) são colocadas no mesmo grupo direcional.
2. **Dentro do grupo, separa em "lanes"** — verifica se as projeções das dobras na direção do grupo se sobrepõem E têm comprimentos similares (≥ 70% uma da outra).
3. **Consolidação** — lanes que acabaram se sobrepondo após atribuições são mescladas iterativamente.

### Chain PMI Dimensioning

**Objetivo:** Criar cotas PMI em cadeia em vez de cotas independentes para cada dobra.

**Algoritmo por lane:**

1. **Particiona em low/high side** — projeta cada dobra na normal de referência e classifica como "lado baixo" (mais perto do bbox mínimo) ou "lado alto" (mais perto do bbox máximo).
2. **Ordena** cada lado por offset (crescente no low side, decrescente no high side).
3. **Cria cotas:**
   - 1ª cota: da curva de perímetro **mais distante** (farIdx) projetada na direção da normal → midpoint da 1ª dobra
   - Cotas seguintes: entre midpoints consecutivos

**Criação PMI:**

- Usa `Points.CreatePoint()` com pontos auxiliares blanked (atributo `"FlatPatternHighlightHelper"` para limpeza futura)
- Associatividade via `Annotations.NewAssociativity()`
- Plano de anotação: XY, XZ ou YZ do WCS (detectado automaticamente)
- Direção da cota: **horizontal** se a dobra for predominantemente vertical, **vertical** se a dobra for horizontal
- Todas as cotas recebem o atributo `"FlatPatternHighlight" = "true"`

---

## Formato do Log File

O Log File do NX (**Help → Log File**) contém toda a saída diagnóstica:

```
=== FlatPatternHighlight Diagnostic Log ===
Part: 9300018488_A

--- Outer Perimeter (True External Boundary) ---
  Total exterior curves (raw): 74
  [diag] flatPattern.GetBodies() count: 0
  [diag] flatPattern.GetEntities() count: 1
  [diag]   entity type: Arc
  ... (body search diagnostics)
  Flat body faces: 42
  Outer loop edge tags: 83
  Outer perimeter curves: 18
  Inner (excluded): 56

--- Bend Center Lines ---
  [Bend Up]    13 lines
  [Bend Down]   1 lines
  Total: 14 (13 up, 1 down)

--- Bend Line → Nearest Perimeter (Parallel) ---
  [diag] Flat pattern plane: normal=Z  u=X  v=Y
  [diag] Non-Line/Arc perimeter curves: 3 / 18
  Overall bbox: (-20,3,-1068,5)-(1684,5,116,5)

  Bend[0] Tag=66084  Mid=(832,1,76,8)  Dir=(1,000,0,000)  Len=1662,2
    Side A (nml+): nearest=    2,12  bboxDist=   39,67  perimTag=66110
    Side B (nml-): nearest=    0,12  bboxDist= 1145,29  perimTag=66105
  ...

  PMI dimensions created: 12

=== End of Diagnostic Log ===
```

---

## Interpretação dos Resultados

### Tabela de Proximidade

Para cada bend line, duas métricas por lado:

| Métrica         | Descrição                                        | Interpretação                        |
|-----------------|--------------------------------------------------|--------------------------------------|
| `nearest`       | Distância da bend até a curva de perímetro paralela mais próxima | Distância real da dobra até o contorno mais próximo |
| `bboxDist`      | Distância da bend até a borda do bounding box    | Distância máxima possível nessa direção |

**Relação `nearest` vs `bboxDist`:**

| Caso                   | Significado                                                  |
|------------------------|--------------------------------------------------------------|
| `nearest ≈ bboxDist`   | A curva de perímetro mais próxima **é** a borda externa máxima (bounding box). A dobra confronta diretamente a borda da peça. |
| `nearest < bboxDist`   | Há uma feature intermediária (aba, recorte, flange parcial) entre a dobra e a borda externa máxima. A distância extra (`bboxDist - nearest`) é o tamanho dessa feature. |

### Exemplo Real

```
Bend[6] Tag=68268  Dir=(1,000,0,000)
  Side A (nml+): nearest=   18,22  bboxDist= 1170,24  → face interna (muito longe da borda)
  Side B (nml-): nearest=   14,72  bboxDist=   14,72  → nearest == bboxDist → BORDA EXTERNA

Bend[10] Tag=68282  Dir=(-1,000,0,000)
  Side A (nml+): nearest=    2,12  bboxDist= 1171,74  → face interna
  Side B (nml-): nearest=   13,22  bboxDist=   13,22  → nearest == bboxDist → BORDA EXTERNA
```

Estas dobras têm um lado que encosta diretamente na borda do bounding box — são as dobras que definem o contorno externo da peça naquela direção.

---

## Limitações Conhecidas

1. **Flat solid body não localizado** — Em certos templates de part, o flat solid não é exposto via `GetBodies()`/`GetEntities()`, e a busca exaustiva em `workPart.Bodies` também falha. Neste caso, todas as 74 curvas são usadas como perímetro (Step 1 impreciso).

2. **Arcos no perímetro** — Arcos são ignorados na comparação de paralelismo (Step 3) porque não têm uma direção única. O log indica `"(some perimeter arcs were skipped)"`.

3. **PMI em partes sem anotações habilitadas** — A criação de cotas PMI pode falhar em parts que não têm o ambiente PMI ativo. O erro é logado e o plugin continua sem as cotas.

4. **Plano de anotação** — O plano de anotação é inferido da normal do flat pattern. Se o flat pattern não estiver alinhado com os planos principais do WCS, as cotas podem ficar orientadas incorretamente.

5. **Arquivos .prt grandes** — O plugin não foi testado com parts > 100 MB. A busca exaustiva em `workPart.Bodies` pode ser lenta.

---

## Próximos Passos

- [ ] **Geometric outer perimeter fallback** — quando o flat solid body não é encontrado, usar Convex Hull + angle threshold para identificar o contorno externo verdadeiro entre as 74 curvas
- [ ] **Suporte a arcos no Step 3** — detectar arcos paralelos a bend lines (ex: bordas arredondadas)
- [ ] **Highlight visual das conexões** — desenhar linhas/setas entre bend lines e suas curvas de perímetro associadas
- [ ] **Export CSV** — salvar a tabela de proximidade em arquivo CSV
- [ ] **Ordem de dobra** — detectar sequência de dobra (bend sequence) a partir da geometria
- [ ] **Limpeza automática de PMI** — script para remover todas as cotas criadas pelo plugin

---

## Licenciamento

A assinatura do assembly (necessária para auto-load com menu) requer a licença **`DotNet Author License`** no servidor de licenças NX. Sem ela, o plugin funciona exclusivamente via **Ctrl+U** (File → Execute → NX Open).
