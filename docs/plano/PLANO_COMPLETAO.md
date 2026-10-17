# Plano do "completão": novas funcionalidades do nosso Revit MCP

Data: 17/10/2026 · Branch base: `port-pr60` (PR #60 já portado) · Status: **proposta para aprovação**

## 1. Resumo

Analisamos 8 fontes: 6 MCPs de Revit abertos (LuDattilo, bimwright, KenLP, Sam-AEC, Erarchitech, antonhofstader),
os PRs abertos do upstream e o MAPA (observado com o REA, sem copiar). Elas somam **1.187 funções**. Todas foram
catalogadas e consolidadas em **290 capacidades canônicas**. Um script confirmou que cada uma das 1.187 funções está
mapeada para uma capacidade ou foi descartada com motivo: **nada ficou de fora**.

| Fase | Capacidades | O que entra |
|---|---|---|
| **0: Fundação** | infraestrutura | Torna cada função nova um arquivo pequeno e padronizado (seção 3) |
| **1: Essenciais** | 153 | Interesse ≥ 4 e esforço P/M: parâmetros, consulta, vistas, folhas, tabelas, tipos, materiais, QA, exportação, criação básica, aprovação humana |
| **2: Importantes** | 76 | Interesse ≥ 3 |
| **3: Depois** | 28 | Úteis, mas de nicho |
| **Backlog** | 33 | Guardadas com motivo |

A decisão central do plano: **construir primeiro a fundação e só depois as funções**. Todas as fontes mostram o
mesmo padrão. Quem tem mais de 100 ferramentas e funciona bem (bimwright, KenLP) tem um "contrato de comando"
em que o despachante cuida da transação, dos avisos, da prévia, do lote, das unidades e do formato da resposta.
Cada ferramenta só descreve *o que* faz. Hoje o nosso repo tem o contrário: cada comando repete tudo isso, em duas
classes C#, um arquivo TS, uma entrada no `command.json` e uma habilitação manual.

## 2. O que já sabemos sobre o nosso repo (achados da análise)

- `modify_element.ts`, `search_modules.ts` e `use_module.ts` são **arquivos vazios desde o primeiro commit do upstream**.
  Hoje não temos **nenhuma** ferramenta de escrita de parâmetros. Essa é a lacuna nº 1, apontada por todas as fontes.
- O broker classifica escrita pelo **prefixo do nome** (`IsLikelyWrite`: `create_`, `delete_`…). Uma exportação ou um
  `set_parameters` escapariam. Isso precisa virar metadado declarado por comando.
- Nenhum comando de escrita suprime os diálogos de aviso do Revit (o `DeleteWarningSuperUtils` só é usado em 2 handlers).
  Um aviso modal ("paredes sobrepostas") **trava a fila do broker para todos os documentos** até o timeout de 120 s.
- O timeout é fixo (120 s) e não existe "ainda rodando": uma operação longa vira erro, a IA pode repetir a escrita e o
  Revit termina a primeira depois.
- O `send_code_to_revit` falha em máquinas com BIM 360/ACC (dois PRs do upstream corrigem: #26 e #46).
- Para o Revit 2027 falta só uma troca de API em `GeometryUtils` (PR #56).

## 3. Fase 0: Fundação (o que torna o resto simples)

Objetivo mensurável: **uma ferramenta nova = uma classe C# de 30 a 80 linhas, sem nenhuma alteração em TS, JSON
ou registro**, e mesmo assim com prévia, lote, aprovação, rastreio de mudanças, unidades e resposta padronizada.

### 3.1 Contrato de ferramenta v2 (C#, `commandset/Framework/`)

```csharp
[McpTool("set_parameters", Kind = ToolKind.Write, Risk = Risk.Medium,
         Scope = Scope.DocAgnostic, Toolset = "parameters", Batchable = true)]
[Description("Grava valores de parâmetros ... (unidades em mm/m²/m³/graus)")]
public sealed class SetParameters : McpTool<SetParametersArgs>
{
    protected override object Run(ToolContext ctx, SetParametersArgs args)
    {
        // Só a lógica. Sem Transaction, sem try/catch de diálogo, sem conversão manual de unidade.
        var plan = ctx.Parameters.Plan(args.Items);  // ParameterService (seção 3.4)
        return ctx.Parameters.Apply(plan);           // read-back e diff ficam no service
    }
}
```

- **Os argumentos são uma classe C#** com atributos (`[Required]`, `[Unit("mm")]`, `[Description]`). O JSON Schema
  é gerado dela (já temos `JsonSchemaGenerator`).
- **O `ToolContext`** entrega o `Document` resolvido pelo broker (nunca o `ActiveUIDocument`), as flags `DryRun`/`BatchId`,
  `Changes`, `Warnings` e os resolvedores (seção 3.4).
- **Quem cuida do ciclo de vida é o despachante, não a ferramenta:**
  - `Read`: sem transação.
  - `Write`: `SafeTransaction` com `IFailuresPreprocessor` (apaga avisos e os devolve na resposta, rollback silencioso
    em erro, nunca abre diálogo modal) e rastreio de `DocumentChanged` (devolve created/modified/deleted).
  - `UI`: precisa ser o documento ativo; nunca roda dentro de transação.
  - `DryRun`: executa e faz `RollBack()`. A prévia é real, com os IDs que seriam criados.
- **Lote atômico:** `execute_batch` roda N passos num `TransactionGroup` + `Assimilate()`, o que dá **um único Ctrl+Z**.
  Tem referência entre passos (`"$steps[0].created[0]"`) e `stopOnError`/`dryRun`. Como as ferramentas novas não abrem
  transação própria, qualquer ferramenta `Batchable` entra no lote de graça. Desenho: KenLP (contrato) + bimwright (grupo).
- **Os comandos legados** continuam funcionando, adaptados por um `LegacyCommandAdapter`. Migram aos poucos ou nunca.
- **Testes sem Revit:** a lógica pura (planos, conversão de unidade, regras de nome, política de lote) fica separada
  da API e é testada com xUnit. Isso resolve a nossa limitação de "C# não compila sem Revit" (DIVERGENCE.md).

### 3.2 Registro automático e fonte única de verdade

- No startup, o plugin **descobre as classes `[McpTool]` por reflexão** e as registra. Acaba a necessidade de habilitar
  comando por comando e de manter o `command.json` à mão (ele passa a ser gerado).
- No build, um passo gera **`tools.manifest.json`** com nome, descrição, schema, kind, risk, scope, toolset e batchable.
  - **O broker** lê o manifest para classificar escrita e aprovação, substituindo o `IsLikelyWrite`. Comando desconhecido
    conta como escrita (fail-closed).
  - **O servidor TS** registra as ferramentas a partir do manifest: schema, parâmetro `document`, `dryRun`, anotações MCP
    (`readOnlyHint`, `destructiveHint`, `idempotentHint`) e toolsets. **Zero arquivo TS por ferramenta.** Ferramentas só
    de servidor (receitas, imagem, knowledge) continuam como `.ts` usando o mesmo helper.
  - **Um teste de contrato** (vitest) garante que o manifest, o C# e as descrições estão sincronizados e que toda
    ferramenta tem unidade declarada nos campos numéricos.

### 3.3 Resposta padronizada (servidor TS, `server/src/framework/`)

- **Envelope único:**
  `{ ok, data, changes?{created,modified,deleted}, warnings[], dryRun, truncated?, nextSteps?[], error?{code, message, hint, recommendedTool} }`.
  `isError` é sempre coerente com `ok`.
- **Proteção de contexto:** listas cortadas com paginação (`limit/offset`) e `_totalCount`. Acima de ~100 KB, o conteúdo
  vai para um arquivo (JSON/NDJSON/CSV) e a resposta traz o caminho, o schema e uma amostra. Há também o modo `compact`
  e um log de tamanho por ferramenta (ideias do bimwright e do LuDattilo).
- **Erros que ensinam:** código estável, frase pronta para o usuário e a próxima ferramenta recomendada
  (MAPA, bimwright e Erarchitech convergem nisso).
- **Imagem como conteúdo MCP nativo** (`{type:"image"}`), para a IA "ver" vistas e folhas.

### 3.4 Serviços comuns (C#, `commandset/Framework/Services/`)

| Serviço | Para quê | Base |
|---|---|---|
| `CategoryResolver` | Aceita "Paredes", "Walls", `OST_Walls` e o enum; essencial no Revit em pt-BR | LuDattilo |
| `Units` | mm/m²/m³/graus ↔ unidades internas pelo `SpecTypeId`; recusa quando não há conversão conhecida (fail-closed) | KenLP `UnitConversionPolicy` |
| `ElementRef` / `Lookup` | Elemento por Id/UniqueId (inclusive em vínculo); tipo/família/nível/vista/fase por nome, com erro de ambiguidade listando candidatos | vários |
| `NameRules` | Caracteres proibidos e nomes duplicados → erro claro, sem nomes-placeholder | KenLP |
| `ParameterService` | O contrato comum de 8 regras do grupo D: referência por GUID → BuiltIn → nome; instância × tipo; conversão por `StorageType`; parâmetros críticos bloqueados; read-back obrigatório; `revert[]` | grupo D |
| `ExportScope` | Exportações que exigem documento modificável: transação de exportação com rollback | bimwright |

### 3.5 Broker: aprovação humana, jobs longos e limites

- **Gate de aprovação** (desenho do Sam-AEC, melhorado; GPL, então só a ideia):
  - **Modos:** `look_only`, `ask_first` e `auto`, com padrão a definir (seção 7).
  - **O plano:** a IA chama `propose_change_plan`. O broker grava o plano com `docId` dentro do hash SHA-256 e com TTL.
  - **O aviso no Revit:** o broker envia `plan_pending` ao plugin, que abre uma **janela WPF não-modal no Revit** com
    resumo, tabela antes/depois e o botão "destacar no modelo". O Sam-AEC não tem esse aviso ativo.
  - **Aprovação só pelo humano:** só a conexão do *plugin* pode responder `plan_decision`; a IA não consegue se autoaprovar.
  - **A espera:** a IA chama `wait_for_plan_decision` (long-poll).
  - **A execução:** `execute_plan` roda o plano inteiro como **um lote = um Ctrl+Z**.
  - **O registro:** um "comprovante" JSON por plano (quem aprovou, antes/depois, resultado).
- **Jobs longos:** depois de ~50 s, a chamada responde `{status:"running", jobId}`, e `get_job_result` busca o resultado
  depois. A mesma chamada repetida não enfileira cópia (idempotência por hash), o que acaba com o risco de escrita dupla.
  Respeita o limite de ~60 s por chamada do Claude Desktop (padrão do MAPA).
- **Limites:** passos por lote, requisições em voo por sessão, tamanho de mensagem.
- **Auditoria:** a JSONL ganha `batchId`, `planId` e `risk`, com parâmetros em hash por padrão (KenLP).

### 3.6 Contexto, conhecimento e prompts

- **`get_session_context`** ("chame primeiro"): versão do add-in × servidor (detecta DLL velha), documentos, alvo,
  modo de aprovação e fila. Inclui `isFamilyDocument` e `isWorkshared` no `list_open_documents`.
- **`get_office_knowledge(topic)`:** lê `knowledge/*.md` do escritório (nomenclatura, padrões de folha, tabelas,
  parâmetros), relido a cada chamada, sem precisar reinstalar. Ideia do MAPA, mas com **conteúdo nosso**.
- **MCP prompts `workflow_*`:** levantar → propor → confirmar → aplicar → conferir (bimwright).
- **MCP resources por documento:** `revit://{docId}/project-info`, `.../levels` e `.../views`.
- **Toolsets + `--read-only`:** expor só os grupos necessários. Com 300 ferramentas, a IA se perde se vir todas de uma vez.

### 3.7 Correções de base (pequenas, entram junto)

PR #26 + #46 reescrito (Roslyn/BIM 360) · PR #56 (Revit 2027) · `send_code_to_revit` com modo `read` (transação sempre
revertida), dicas de erro e saída grande em arquivo · `ajv` como dependência direta (PR #49).

**Esforço estimado da Fase 0:** G. São cerca de 2 a 3 semanas de trabalho de agente, divididas em 5 frentes paralelizáveis:
C# framework, gerador de manifest + servidor TS, serviços, broker, testes. A Fase 0 termina quando 3 ferramentas-piloto
(`get_element_parameters`, `set_parameters`, `create_sheets`) passarem nos testes e num roteiro manual dentro do
Revit, num modelo descartável.

## 4. Fase 1: Essenciais (153 capacidades), em ondas

As ondas respeitam dependências e entregam valor cedo. Dentro de cada onda, as ferramentas são independentes e podem ser
implementadas **em paralelo por vários agentes**, porque cada uma é um arquivo novo no framework. Cada agente recebe a linha
do catálogo (desenho, armadilhas, melhor referência) e entrega a classe + testes + uma entrada no roteiro de teste manual.

| Onda | Tema | Destaques (IDs no apêndice) |
|---|---|---|
| **1A** | Consulta e parâmetros (a base de tudo) | `find_elements` (CON-03), `get_element_details` (CON-02), `get_element_parameters` (PAR-01), `set_parameters` (PAR-03), `bulk_update_parameters` (PAR-04), `copy_parameters`, `list_category_parameters`, `list_levels/phases/worksets`, `get_project_info`, `set_project_info`, `set_element_phase` |
| **1B** | Edição, tipos e materiais | `transform_elements` (EDT-01), `modify_element` (EDT-03, preenche o stub vazio), `rename_elements`, `renumber_elements`, `change_element_type` (FAM-01), `replace_type_instances`, `duplicate_type`, `get/set_compound_structure`, `assign_material` ("trocar acabamento", MAT-13), `load_family` (FAM-07, com allowlist de pastas) |
| **1C** | Documentação | `create_view` (VIS-02), `create_room_views` (VIS-06), `apply_view_template`, `create_sheets` (FOL-03), `place_on_sheet` (FOL-04), `duplicate_sheet`, `renumber_sheets`, `create_schedule` (TAB-03), `get_schedule_data` (TAB-02), filtros de vista + sobreposições gráficas (MAT-01/02/04), `tag_all_in_view` (ANO-02) |
| **1D** | Criação de arquitetura | `create_walls` (ARQ-01), `create_floor`/`create_ceiling` (ARQ-02/03), `place_family_instance`, `place_door_window`, `create_rooms`, níveis declarativos (ARQ-11/12) |
| **1E** | QA e exportação | `get_model_warnings` + delta (QA-01/02), `get_model_health` (QA-03), `purge_unused` com prévia (QA-05), `check_clashes` (QA-08), `export_pdf` (EXP-01), `get_view_image` (EXP-03), `export_elements_data` / importação de tabela de parâmetros (EXP-07, PAR-06/07) |
| **1F** | Aprovação e histórico | `propose_change_plan` → `execute_plan` (UND-02..05), `get_session_changes` (UND-09), `undo_history` (UND-07, limitado: a API do Revit não expõe a pilha de undo) |

## 5. Fases 2, 3 e Backlog

- **Fase 2 (76):** grupos, arrays, alinhar/distribuir, editar curvas, coordenadas, worksets, parâmetros compartilhados
  (criar definição, remover vínculo), parâmetros de família, envelope do ambiente (laje, piso, rodapé, forro; ARQ-04), módulos de marcenaria (ARQ-24/25, com famílias **nossas**),
  pilares, cruzamentos MEP × parede/laje para compatibilização, revisões, instalador Windows, `revert_plan`.
- **Fase 3 (28) e Backlog (33):** snapshots e comparação de modelo, `sync_with_central` (contraria a política atual do
  fork, que fica em backlog até decisão explícita), integrações externas, geometria de massa, MEP avançado.
- **Descartadas com motivo:** integrações com Rhino, Navisworks, Speckle, ACC e Power BI (Sam-AEC); ferramentas quebradas
  do PR #51; ferramentas proprietárias do MAPA (identidade visual, marcenaria específica, logo); funções que a API do
  Revit não permite (rampa, edição de membro de grupo com várias instâncias).
  A lista completa está nas seções "Descartadas" de `research/capabilities/*.md`.

## 6. Regras para não errar

1. **Observar e se inspirar, não copiar.** Sam-AEC é GPL e MAPA é proprietário: deles, só ideias. bimwright é Apache-2.0:
   reescrever. KenLP, LuDattilo, Erarchitech e antonhofstader são MIT: podemos adaptar, com atribuição.
2. **Nunca confiar em "sucesso" das fontes.** Vários handlers do PR #51 e do antonhofstader relatam sucesso sem fazer
   nada. Toda escrita nossa faz read-back.
3. **Unidades sempre explícitas no fio** (mm, m², m³, graus) e **idioma neutro** (resolvedores, nunca nome em inglês fixo).
4. **Toda ferramenta respeita o roteamento por documento do broker.** Nada de `ActiveUIDocument` fora de ferramentas `UI`.
5. **Validação no Revit real em modelo descartável** antes de cada merge (regra do `AGENTS.md`), com um roteiro de teste
   por onda.

## 7. Decisões que precisam de você

1. **Modo de aprovação padrão:** `ask_first` (toda escrita passa pelo plano) ou aprovação só para `Risk ≥ Medium`
   (exclusões, alterações em massa, tipos)?
2. **Ordem das ondas:** a proposta é 1A → 1B → 1C → 1D → 1E, com 1F junto da Fase 0. Quer priorizar documentação (1C)?
3. **Pasta `knowledge/`:** quem escreve os padrões do escritório (nomenclatura, carimbos, tabelas)?
4. **Versões do Revit:** manter 2020–2021 (sem PDF nativo) ou cortar o suporte em 2022+?
5. **MAPA ao vivo:** com o Revit aberto e logado, capturamos o `tools/list` real (272 ferramentas, com schemas) e
   refinamos o catálogo antes da Fase 1.

## Arquivos de apoio

- `docs/plano/APENDICE_CAPACIDADES.md`: as 290 capacidades por fase e grupo.
- `research/capabilities/*.md`: desenho, fontes, melhor referência e armadilhas de cada capacidade (pasta ignorada pelo git).
- `research/<fonte>.md`: catálogos completos das 8 fontes; `research/catalog-all.csv`: as 1.187 funções.
