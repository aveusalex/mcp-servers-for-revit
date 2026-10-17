# Fase 0: especificação de implementação

Contexto: `docs/plano/PLANO_COMPLETAO.md` (seção 3). Branch de integração: `fase0`.

Decisões do responsável (17/10/2026):
- **Aprovação humana só para `Risk >= Medium`.** `None`/`Low` executam direto.
- **Suporte a partir do Revit 2022.** Remover R20/R21 de csproj/sln/CI. Adicionar R27 (.NET 10).

Regras gerais (valem para todas as frentes):
- Leia `AGENTS.md`, `README.md` e `DIVERGENCE.md` antes de começar.
- O broker continua sendo o único listener (`127.0.0.1:8090`). Nada de abrir portas novas.
- Toda resolução de documento passa por `RevitDocumentContext.ResolveDocument(app)`. Nunca use `ActiveUIDocument`, a não ser em ferramentas `Kind = UI`.
- Unidades no fio: **mm, m², m³, graus**. Datas em ISO-8601.
- Mensagens para a IA em **inglês**. Comentários de código em inglês, seguindo o estilo do repo.
- Não copie código de sam-aec (GPL) nem do MAPA (proprietário). Código de bimwright (Apache-2.0): reescreva.
  KenLP, LuDattilo, Erarchitech e antonhofstader (MIT): pode adaptar, com atribuição num comentário.
  Os repositórios estão em `C:\dev\research-repos\*`, e o desenho de cada capacidade em `research/capabilities/*.md`.
- Compile os 5 alvos (`Debug R22`..`Debug R26`) dos projetos que tocar (`commandset/RevitMCPCommandSet.csproj`,
  `plugin/*.csproj`), rode `npm test` em `server/` e em `broker/`, e só entregue com tudo verde.
- Não faça push. Faça commits pequenos e descritivos na sua branch, terminando com a linha
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

---

## Contrato A: metadados de ferramenta (C#, `commandset/Framework/`)

**O esqueleto já está commitado** (`McpToolAttribute.cs`, `McpTool.cs`, `ToolContext.cs`, `ToolResult.cs`, `ToolException.cs`, `Services/ToolServices.cs`). Ninguém muda essas assinaturas sem combinar. A F1 só acrescenta coisas, e a F2 é dona de `Services/`.

```csharp
namespace RevitMCPCommandSet.Framework
{
    public enum ToolKind { Read, Write, UI }          // UI = needs the target to be the active doc; never in a transaction
    public enum Risk { None, Low, Medium, High }      // Medium+ goes through human approval in the broker

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class McpToolAttribute : Attribute
    {
        public McpToolAttribute(string name);         // snake_case, unique
        public ToolKind Kind { get; set; } = ToolKind.Read;
        public Risk Risk { get; set; } = Risk.None;   // Write defaults: set explicitly (Low for additive edits, Medium for bulk/type/delete, High for irreversible/file)
        public string Toolset { get; set; } = "core"; // e.g. core, query, parameters, editing, views, sheets, schedules, families, graphics, annotation, architecture, qa, export, approval
        public bool Batchable { get; set; } = true;   // can run as a step of execute_batch (Write/Read only)
        public bool Idempotent { get; set; }
        public int TimeoutSeconds { get; set; } = 50; // after this the broker answers "running" (Contract E)
        public string Description { get; set; }       // what it does, when to use it, units, how to undo
    }

    public abstract class McpTool<TArgs> : IMcpTool where TArgs : class, new()
    {
        protected abstract object Run(ToolContext ctx, TArgs args);
        protected virtual void Validate(TArgs args) { }   // throw ToolException(code, message, hint) on bad input
    }
}
```

- **Argumentos:** POCO com `[JsonProperty]`, `[Description]`, `[Required]`, `[Unit("mm"|"m2"|"m3"|"deg")]`,
  `[Range]`, `[EnumValues]` e `[DefaultValue]`. O schema JSON é gerado deles. **Campo numérico de medida sem `[Unit]`
  é erro de build** (teste de contrato).
- **`ToolException(string code, string message, string hint = null, string recommendedTool = null)`** é o único jeito
  de falhar "esperado". Qualquer outra exceção vira `INTERNAL_ERROR`.
- **Parâmetros injetados pelo framework**, que a ferramenta **não** declara: `dryRun` (só Write) e `document`
  (lado TS). O `planId` não entra nos argumentos; a aprovação é resolvida inteira no broker (Contrato E).

## Contrato B: contexto e despachante (C#)

```csharp
public sealed class ToolContext
{
    public UIApplication UiApp { get; }
    public Document Doc { get; }                  // resolved by broker docId (RevitDocumentContext)
    public bool DryRun { get; }
    public string RequestId { get; }
    public string BatchId { get; }                // null outside batch
    public ChangeSet Changes { get; }             // filled by DocumentChanged tracking; tool may add notes
    public IList<ToolWarning> Warnings { get; }   // Revit failure messages swallowed by the preprocessor + tool warnings
    public Services Services { get; }             // Contract C
}
```

- **Fila única:** um `IExternalEventHandler` (`ToolDispatcher`) processa uma fila (o padrão do KenLP). Cada pedido usa
  a semântica do `RequestGate`: se não começou até o timeout, é cancelado; se começou, vai até o fim.
- **`Read`:** executa sem transação.
- **`Write`:** `SafeTransaction` (nome `"MCP: <tool>"`), com:
  - `IFailuresPreprocessor`: apaga os warnings e copia o texto deles para `ctx.Warnings`; erro → `ProceedWithRollBack`;
    `SetForcedModalHandling(false)`, `SetClearAfterRollback(true)`. **Nunca abre diálogo.**
  - Rastreio de `Application.DocumentChanged` só durante a chamada → `ChangeSet {created, modified, deleted}` (ids long),
    com teto de 500 ids por lista e `truncated`.
  - `DryRun` → `RollBack()` no fim, devolvendo o `ChangeSet` "que teria acontecido".
- **`UI`:** recusa com `REQUIRES_ACTIVE_DOCUMENT` se o doc alvo não for o ativo; executa sem transação.
- **Lote:** `execute_batch {steps:[{tool, args}], stopOnError=true, dryRun=false}`:
  - Resolve e valida **todos** os passos antes de executar.
  - Recusa lote que mistura `UI` com escrita e recusa passos `Batchable=false`.
  - Roda num `TransactionGroup("MCP: Batch (n)")`; cada passo é uma Transaction própria.
  - Resultado: `Assimilate()` = um Ctrl+Z; `RollBack()` em falha com `stopOnError` ou em `dryRun`.
  - Referências entre passos: uma string `"$steps[i].<caminho>"` dentro de `args` é substituída pelo valor do resultado
    anterior (ex.: `$steps[0].changes.created[0]`, `$steps[1].data.viewId`).
  - O risco do lote é o **maior** risco entre os passos.
- **Envelope de resultado** (o que volta ao broker como `result`):
  `{ ok, data, changes?, warnings[], dryRun, error?{code, message, hint?, recommendedTool?} }`.
- **Registro:** `ToolRegistry.Discover(Assembly)` encontra as classes `[McpTool]`. O método estático
  `ToolRegistry.CreateCommands(UIApplication)` devolve um `IRevitCommand` (do RevitMCPSDK) por ferramenta, com
  `CommandName = name`. O plugin chama esse método **por reflexão** no `CommandManager`, sem tipo compartilhado em
  tempo de compilação, e registra tudo **sem precisar de `command.json` nem habilitação**. Os comandos legados
  continuam pelo caminho antigo.
- **Metadados em runtime:** o método estático `ToolRegistry.GetManifestJson()` devolve o manifest (Contrato D). O plugin
  envia esse JSON ao broker no `register`, no campo `tools`.

## Contrato C: serviços comuns (`ctx.Services`, C#, `commandset/Framework/Services/`)

Funções puras, testáveis sem Revit sempre que possível (a lógica fica separada da API).
- **`Categories.Resolve(string)`:** aceita `OST_Walls`, `Walls`, `Paredes` ou o nome localizado do doc. Erro
  `CATEGORY_NOT_FOUND` com sugestões (distância de edição).
- **`Units`:** `ToInternal(value, spec)` / `FromInternal(raw, spec)` por `ForgeTypeId`, com os mapeamentos length→mm,
  area→m², volume→m³, angle→deg, sem unidade → igual. **Spec desconhecido → erro `UNIT_UNSUPPORTED`** (fail-closed).
  A decisão de conversão fica numa classe pura testável (inspiração: KenLP `UnitConversionPolicy`).
- **`Lookup`:**
  - Elemento por `long id` ou `UniqueId`, e também em vínculo: `"linkInstanceId:elementId"`.
  - Por nome: `Level`, `Phase`, `View`, `ViewTemplate`, `Workset`, `FamilySymbol` (`"Família: Tipo"`) e `ElementType`.
  - Nome ambíguo → `AMBIGUOUS_NAME` com a lista de candidatos; inexistente → `NOT_FOUND` com sugestões.
- **`Names`:** valida os caracteres proibidos pelo Revit e a unicidade por classe de elemento → `INVALID_NAME` /
  `NAME_COLLISION`. Nunca usar `try { x.Name = n } catch {}`.
- **`Parameters` (`ParameterService`):** implementa o **contrato de 8 regras** da seção "Contrato comum" de
  `research/capabilities/D-edicao-parametros.md`:
  - `ParameterRef` por GUID → BuiltIn → nome;
  - instância/tipo;
  - conversão por `StorageType`, com ElementId resolvido por nome;
  - parâmetros críticos bloqueados sem `allowCritical`;
  - read-back obrigatório e `revert[]`;
  - `Plan(...)` (valida tudo, sem escrever) e `Apply(plan)`.
- **`Paging`:** helper `Page<T>(IEnumerable<T>, limit, offset)` → `{items, total, limit, offset, truncated}`.

## Contrato D: `tools.manifest.json`

Gerado no build (`tools/ManifestGen`, net8, `System.Reflection.MetadataLoadContext`, sem carregar o Revit) para
`server/src/framework/tools.manifest.json`. Um teste falha se o arquivo commitado estiver desatualizado.

```json
{ "version": 1, "capabilityHash": "<sha256 of sorted names+schemas>",
  "tools": [ { "name": "set_parameters", "description": "...", "kind": "write", "risk": "medium",
               "toolset": "parameters", "batchable": true, "idempotent": false, "timeoutSeconds": 50,
               "inputSchema": { "type": "object", "properties": { ... }, "required": [ ... ] } } ] }
```

- **No servidor TS:** registra cada ferramenta do manifest com o schema **fiel**, incluindo as descrições.
  - Injeta `document` (todas) e `dryRun` (só `write`).
  - Anotações MCP: `readOnlyHint = kind==read`, `destructiveHint = risk>=medium`, `idempotentHint`.
  - Filtro de toolsets: env `REVIT_MCP_TOOLSETS=core,parameters,...`, com padrão "todos".
  - Modo `REVIT_MCP_READ_ONLY=1` (só `read`).
- **Checagem de versão:** o `get_session_context` compara o `capabilityHash` do manifest do servidor com o enviado pelo
  plugin e avisa `ADDIN_OUTDATED` quando divergem.

## Contrato E: protocolo do broker (adições)

Mensagens novas (JSON por WebSocket, como hoje):

| Direção | type | Campos |
|---|---|---|
| plugin→broker | `register`/`heartbeat` (existente) | + `tools` (manifest do Contrato D, só no `register`) |
| mcp→broker | `command` (existente) | + `planId?` |
| broker→mcp | `response` (existente) | o `result` pode ser `{status:"running", jobId}` |
| mcp→broker | `get_job` | `jobId`, `waitSeconds?` (≤ 50) |
| mcp→broker | `plan_create` | `docId`, `steps:[{tool,args}]`, `summary`, `reasoning?` → `{planId, planHash, status:"pending", expiresAt}` |
| mcp→broker | `plan_get` / `plan_wait` | `planId`, `waitSeconds?` |
| broker→plugin | `plan_pending` | o plano completo + `planHash` |
| plugin→broker | `plan_decision` | `planId`, `planHash`, `decision: approved\|rejected`, `approver` (=`Environment.UserName`), `note?` |

- **Classificação:** `kind`/`risk` vêm do manifest **da sessão dona do documento**. Comando ausente do manifest
  (legado) usa uma tabela estática de legados no broker; o que não estiver nela conta como `write/medium` (fail-closed).
  O `IsLikelyWrite` do plugin é removido em favor dessa informação, que o plugin também tem.
- **Gate:**
  - Modo `REVIT_MCP_APPROVAL=ask_first` (padrão) | `auto` | `look_only`.
  - Em `ask_first`, um comando com `risk >= medium` exige `planId` de um plano `approved`, do **mesmo `docId`**,
    não expirado (TTL de 30 min), e que contenha exatamente esse `{tool, args canônicos}` ainda não executado
    (at-most-once). Sem isso: `APPROVAL_REQUIRED`, com `hint` mandando chamar `propose_change_plan`.
  - `look_only` recusa qualquer `write`.
  - **Só a conexão `role=plugin` dona do `docId` pode enviar `plan_decision`.**
  - Planos ficam em `%APPDATA%\revit-mcp\plans\<planId>.json`. O hash é SHA-256 do JSON canônico
    `{docId, steps, summary}`, com chaves ordenadas e números normalizados.
  - Execução: um plano com vários passos é enviado ao plugin como **um** `execute_batch` (um Ctrl+Z). O comprovante
    vai para `plans/<planId>.proof.json`: aprovador, hora, `changes` e resultado.
- **Jobs:**
  - Se o plugin não responde em `timeoutSeconds` da ferramenta (padrão 50), o broker devolve
    `{status:"running", jobId=correlationId}` ao MCP e **continua esperando** até o teto (`COMMAND_TIMEOUT_MS`, que
    passa para 15 min).
  - `get_job` devolve o resultado quando chegar.
  - Idempotência: um comando idêntico (mesmo docId, tool e args canônicos) com um job em andamento não é reenviado;
    devolve o mesmo `jobId`.
- **Limites:** mensagem ≤ 2 MB, lote ≤ 200 passos, ≤ 8 comandos em voo por sessão MCP (`OVERLOADED`).
- **Auditoria:** acrescentar `risk`, `planId`, `batchId` e `jobId`. Os `params` são gravados como hash por padrão
  (`REVIT_MCP_AUDIT_PARAMS=hash|full`).

## Contrato F: resposta no servidor TS (`server/src/framework/`)

- **`toToolResult(envelope)`:** converte o envelope em conteúdo MCP. Texto JSON compacto, `isError = !ok`.
  `error.hint` e `recommendedTool` aparecem em texto legível.
- **Guarda de tamanho:** acima de 100 KB, grava em `%LOCALAPPDATA%\revit-mcp\out\<tool>-<timestamp>.json` e devolve
  `{spilled:true, path, bytes, preview}`. Arrays acima de 200 itens são cortados com `_truncated`/`_total`.
- **Imagem:** se `data.image = {base64, mimeType}`, emite um bloco `{type:"image"}`, mais um texto com os metadados.
- **Helper `defineServerTool()`** para ferramentas que existem só no servidor (planos, jobs, contexto, knowledge,
  prompts), com o mesmo envelope.

---

## Frentes (uma por agente, cada uma no seu worktree)

| # | Frente | Escopo | Não tocar |
|---|---|---|---|
| F1 | Núcleo C# | Contratos A e B: dispatcher, SafeTransaction, failures preprocessor, ChangeSet, `execute_batch`, ToolRegistry, IRevitCommand adapter; integração em `plugin/Core/CommandManager.cs` (+ envio de `tools` no register em `BrokerClient.cs`, **só** essa linha); testes xUnit da lógica pura (novo projeto `tests/framework`, net8, sem Revit); ferramentas-piloto `get_session_context` (parte C#), `execute_batch` e `set_elements_pinned` (EDT-04, escrita simples para validar transação/dryRun/lote) | broker/, server/ |
| F2 | Serviços C# | Contrato C completo + testes xUnit puros; ferramentas-piloto `get_element_parameters` (PAR-01), `set_parameters` (PAR-03), `find_elements` (CON-03) e `create_sheets` (FOL-03) sobre o framework. Só implemente `Run`; não toque nos arquivos do Contrato A/B (são da F1) | plugin/, broker/, server/ |
| F3 | Manifest + servidor TS | Contratos D e F: `tools/ManifestGen`; registro a partir do manifest; anotações; toolsets/read-only; envelope; guarda de tamanho; imagem; testes de contrato (vitest/node:test); migrar `register.ts` para conviver com os legados | commandset/, plugin/, broker/ |
| F4 | Broker + aprovação | Contrato E: broker (classificação, gate, planos, jobs, limites, auditoria) + testes; cliente TS (`BrokerConnection`: `getJob`, `planCreate`, `planGet`, `planWait`) + ferramentas de servidor `propose_change_plan`, `wait_for_plan_decision`, `get_plan`, `execute_plan`, `get_job_result`; plugin: tratar `plan_pending` com janela WPF não-modal (resumo, tabela de passos, "Destacar no modelo", Aprovar/Rejeitar) e enviar `plan_decision`; remover `IsLikelyWrite` e usar `kind/risk` | commandset/Framework (só lê o contrato) |
| F5 | Correções de base | Remover R20/R21 (csproj, sln, scripts, docs); adicionar R27 (.NET 10, `#if REVIT2027_OR_GREATER` no `GeometryUtils.FindIntersection`, PR #56); Roslyn: dedup de referências (PR #26) e `AssemblyResolve` com nome exato (ideia do PR #46, reescrita); `send_code_to_revit` com `transactionMode: none\|auto\|rollback` (`rollback` = modo leitura) e dicas de erro; `ajv` como dependência direta; `knowledge/` com README e 3 exemplos (nomenclatura, folhas, parâmetros) + ferramenta de servidor `get_office_knowledge(topic)` | commandset/Framework, broker/ |

**Definição de pronto da Fase 0:**
- Merge das 5 frentes na `fase0`.
- Build R22–R26 limpo (R27 compila se o SDK .NET 10 estiver disponível).
- Todos os testes verdes.
- Roteiro manual `docs/plano/ROTEIRO_TESTE_FASE0.md` (escrito pela F1) executado no Revit 2026 num modelo descartável:
  ler parâmetros → `set_parameters` com dryRun → aplicar (risco Low) → `bulk`/tipo (Medium) pedindo aprovação na
  janela → executar → um Ctrl+Z desfaz tudo.
