# FlatPatternHighlight

Plugin NXOpen (C#/.NET) para análise de **flat pattern** de Sheet Metal no Siemens NX 2512. Identifica e destaca o contorno externo real, linhas de centro de dobra, e mede a relação entre dobras e o perímetro externo.

## Objetivo

Substituir a contagem manual (~12 linhas de dobra) por uma análise automatizada que:
1. Filtra as **74 curvas externas brutas** (`GetExteriorCurves`) para obter **somente o perímetro externo verdadeiro** (excluindo recortes internos)
2. Separa **bend center lines** (up / down) — 14 linhas totais
3. Para cada linha de dobra, encontra as curvas de perímetro externo **paralelas mais próximas** e mede a distância até a **borda do bounding box**

## Arquitetura

### Fluxo (3 Steps)

```
Main()
  ├─ FindFlatPattern()       → localiza a feature FlatPattern no part
  ├─ HighlightOuterPerimeter()
  │   ├─ GetExteriorCurves() → 74 curvas brutas
  │   ├─ FindFlatSolidBody() → busca o body 3D do flat pattern (3 estratégias)
  │   ├─ UF_MODL_ask_face_loops() → P/Invoke para filtrar outer loops
  │   └─ Highlight + MsgBox  → Step 1
  ├─ HighlightBendCenterLines()
  │   ├─ GetBendUpCenterLines()   → linhas de centro (up)
  │   ├─ GetBendDownCenterLines() → linhas de centro (down)
  │   └─ Highlight + MsgBox      → Step 2
  └─ AnalyzeBendToPerimeter()
       ├─ Bounding box das curvas de perímetro
       ├─ Para cada bend line:
       │    ├─ Direção + normal perpendicular
       │    ├─ Projeta curvas paralelas (dot > 0.95) nos dois lados
       │    ├─ nearest = curva de perímetro mais próxima em cada lado
       │    └─ bboxDist = distância do midpoint até a borda do bbox
       └─ ListingWindow com tabela comparativa
```

### Estrutura de Arquivos

```
FlatPatternHighlight/
├── FlatPatternHighlight.cs      # Código fonte principal (444 linhas)
├── FlatPatternHighlight.csproj  # Projeto .NET 4.8 / x64
├── FlatPatternHighlight.men     # Menu de registro no NX
├── build.ps1                    # Script de build + sign
└── README.md                    # Este documento
```

### Dependências NX

| DLL | Caminho |
|-----|---------|
| `NXOpen.dll` | `%UGII_BASE_DIR%\NXBIN\managed\` |
| `NXOpenUI.dll` | `%UGII_BASE_DIR%\NXBIN\managed\` |
| `NXOpen.Utilities.dll` | `%UGII_BASE_DIR%\NXBIN\managed\` |
| `libufun.dll` | `%UGII_BASE_DIR%\NXBIN\` (P/Invoke) |
| `NXSigningResource.res` | `%UGII_BASE_DIR%\UGOPEN\` |

## Como Buildar

```powershell
.\build.ps1 -NxDir D:\NX2512
```

O script:
1. Verifica pré-requisitos
2. Copia `NXSigningResource.res`
3. Patenteia os HintPaths no .csproj
4. Builda com `dotnet build`
5. Assina com `SignDotNet.exe` (opcional — sem licença funciona via Ctrl+U)

## Como Executar

### Ctrl+U (recomendado)
`File → Execute → NX Open → selecionar FlatPatternHighlight.dll`

### Auto-load com Menu
Copiar `FlatPatternHighlight.dll` + `FlatPatternHighlight.men` para a pasta `startup` do `UGII_USER_DIR`. Um menu **Flat Pattern → Highlight Exterior Curves** aparece antes do Help.

## Algoritmos

### Step 1 — Outer Perimeter Filtering

Usa 3 estratégias em cascata para localizar o **flat solid body**:
1. `flatPattern.GetBodies()`
2. `flatPattern.GetEntities()` (busca por `Body`)
3. Itera `workPart.Bodies` e checa `body.GetFeatures()` por um `FlatPattern`

Se encontrar o body, usa `UF_MODL_ask_face_loops` (P/Invoke nativo) em cada face planar para obter **somente outer loops** (type=1), cruzando os Tags com `FlatSolidObject.Tag` das curvas de `GetExteriorCurves`.

**Fallback:** Se o body não for encontrado (ocorre em alguns parts), todas as 74 curvas são usadas como perímetro.

### Step 2 — Bend Center Lines

Chama `GetBendUpCenterLines()` e `GetBendDownCenterLines()`, classifica por UP/DOWN, e loga Tag + comprimento.

### Step 3 — Bend → Perimeter Proximity

Para cada bend line:
1. **Midpoint** e **direção** unitária
2. **Normal** perpendicular (`-dy, dx`)
3. Filtra curvas de perímetro **paralelas** (dot product > 0.95)
4. Projeta o midpoint de cada curva na normal → determina lado A (+) ou B (-)
5. Seleciona a curva **mais próxima** em cada lado
6. Calcula a **distância até o bounding box** (ray-cast em 4 direções: ±X, ±Y)

**Interpretação:**
- `nearest == bboxDist` → a curva de perímetro mais próxima nesse lado **é** a borda do bounding box (lado externo)
- `nearest < bboxDist` → há uma curva de perímetro intermediária (aba, recorte) entre a dobra e a borda máxima

## Próximos Passos

- [ ] Identificar o flat solid body via `FindObject("BODY_NAME")` ou escaneamento por tipo no `NXObjectManager`
- [ ] Implementar fallback geométrico para Step 1 (convex hull + remoção de curvas internas por bounding box)
- [ ] Highlight visual das conexões (setas das bend lines até as curvas de perímetro)
- [ ] Exportar CSV dos resultados

## Licenciamento

A assinatura do assembly requer a licença `DotNet Author License` no servidor de licenças NX. Sem ela, o plugin funciona apenas via **Ctrl+U** (não carrega automaticamente na inicialização).
