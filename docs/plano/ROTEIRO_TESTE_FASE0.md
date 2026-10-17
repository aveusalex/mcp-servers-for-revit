# Roteiro de teste manual da Fase 0 (Revit 2026)

Objetivo: validar dentro do Revit o que os testes automáticos não alcançam (transação, prévia, lote, aprovação,
rastreio de mudanças e um único Ctrl+Z), seguindo a definição de pronto de `FASE0_SPEC.md`.

Regras (de `AGENTS.md`): **só num modelo descartável**, nunca num projeto real; não habilite `AllowBackgroundWrites`
nem `AllowAutoActivate`; não sincronize com central; não copie o conteúdo do `broker-token`.

## 0. Preparação

1. Compile a `fase0` (com as 5 frentes):
   - `dotnet build commandset -c "Debug R26"` e `dotnet build plugin -c "Debug R26"` (o Debug já copia o add-in para
     `%APPDATA%\Autodesk\Revit\Addins\2026`).
   - `npm run build` em `server/` e `npm install` em `broker/`.
2. Crie o modelo descartável: Revit 2026 → Novo projeto a partir do template padrão → salve como
   `C:\temp\mcp-fase0\teste-fase0.rvt` (fora de pastas de projeto e fora da rede).
3. Desenhe 4 paredes retangulares (Nível 1), 1 porta numa delas e 2 tipos de parede diferentes. Salve.
4. Abra um segundo projeto descartável (`teste-fase0-B.rvt`, qualquer conteúdo) e **volte** para o primeiro como ativo.
5. Abra o cliente MCP (Claude Desktop ou Claude Code) com o servidor deste repositório.

Anote em cada passo: **OK / FALHOU**, o texto de erro e o horário (para cruzar com a auditoria em
`%APPDATA%\revit-mcp\audit\`).

## 1. Sessão e registro automático

| # | Ação | Esperado |
|---|---|---|
| 1.1 | Chame `get_session_context` | `addin.capabilityHash` igual ao `capabilityHash` de `server/src/framework/tools.manifest.json` (sem aviso `ADDIN_OUTDATED`); `documents` lista os 2 projetos com `isFamilyDocument=false`, `isWorkshared=false`, um só `isActive=true`; `revit.versionNumber = "2026"`; `queue.pending = 0`; `addin.contractProblems` ausente |
| 1.2 | Abra uma família qualquer (Editar família) e repita 1.1 | o documento de família aparece com `isFamilyDocument=true` |
| 1.3 | Feche a família sem salvar | some da lista em até ~15 s |
| 1.4 | Confira que `set_elements_pinned`, `execute_batch` e `get_session_context` aparecem no cliente sem nenhuma entrada no `command.json` | ferramentas registradas sozinhas |

## 2. Escrita simples, prévia e rastreio (`set_elements_pinned`, risco Low)

Pegue os ids das 4 paredes (ex.: com `get_current_view_elements` ou `find_elements`).

| # | Ação | Esperado |
|---|---|---|
| 2.1 | `set_elements_pinned {elementIds:[p1,p2], pinned:true, dryRun:true}` | `ok=true`, `dryRun=true`, `data.changed=[p1,p2]`, `changes.modified` contém p1 e p2; **no Revit as paredes continuam sem alfinete** e o menu Desfazer não ganhou item |
| 2.2 | Mesma chamada sem `dryRun` | executa direto (Low não pede aprovação); as paredes ficam fixadas; Desfazer mostra **"MCP: set_elements_pinned"** |
| 2.3 | Repita 2.2 | `data.changed=[]`, `data.unchanged=[p1,p2]` (idempotente) |
| 2.4 | `set_elements_pinned {elementIds:[999999999], pinned:true}` | `ok=false`, `error.code=NOT_FOUND`, com `hint` |
| 2.5 | `set_elements_pinned {elementIds:[p1], pinned:true, cor:"azul"}` | `ok=false`, `error.code=INVALID_ARGUMENTS`, mensagem cita `Unknown argument 'cor'` |
| 2.6 | `set_elements_pinned {elementIds:[]}` | `INVALID_ARGUMENTS` (`pinned` obrigatório / pelo menos 1 id) |
| 2.7 | Ctrl+Z no Revit | desfaz 2.2 (as paredes voltam a ficar soltas) |

## 3. Documento alvo e política de UI

| # | Ação | Esperado |
|---|---|---|
| 3.1 | Com o projeto A ativo, chame `get_session_context` com `document` = docId do B | `targetDocId` = B, `activeDocId` = A |
| 3.2 | Chame uma ferramenta de leitura no B (ex.: `get_element_parameters`) | funciona sem ativar o B |
| 3.3 | Se houver ferramenta `Kind = UI` (ex.: seleção), chame-a no B | `ok=false`, `REQUIRES_ACTIVE_DOCUMENT`; nada muda no A |
| 3.4 | Feche o B e chame algo com o docId dele | `DOCUMENT_NOT_FOUND` (nunca cai no documento ativo) |

## 4. Revit ocupado (fila com cancelamento)

| # | Ação | Esperado |
|---|---|---|
| 4.1 | Abra um diálogo modal no Revit (ex.: Gerenciar → Unidades do projeto) e deixe aberto; chame `set_elements_pinned` | depois de ~45 s: `ok=false`, `REVIT_BUSY`, "nothing was changed" (ou `status:"running"` do broker, se a F4 estiver ativa) |
| 4.2 | Feche o diálogo e espere 10 s | **a chamada de 4.1 não é aplicada depois** (as paredes não mudam, Desfazer não ganha item) |

## 5. Lote atômico (`execute_batch`)

| # | Ação | Esperado |
|---|---|---|
| 5.1 | `execute_batch {steps:[{tool:"set_elements_pinned", args:{elementIds:[p1], pinned:true}}, {tool:"set_elements_pinned", args:{elementIds:[p2,p3], pinned:true}}]}` | `ok=true`, `data.committed=true`, `data.risk="low"`, 2 passos ok; `changes.modified` com p1..p3; Desfazer mostra **um único** item "MCP: Batch (2)" |
| 5.2 | Ctrl+Z **uma vez** | as 3 paredes voltam a ficar soltas |
| 5.3 | Mesmo lote com `dryRun:true` | `dryRun=true`, `data.committed=false`, `changes` mostra o que mudaria; nada muda no modelo |
| 5.4 | Lote com 2º passo inválido (`elementIds:[999999999]`), `stopOnError` padrão | `ok=false`, `error.code=BATCH_STEP_FAILED` citando o passo 1; o passo 0 **também** foi desfeito; Desfazer sem item novo |
| 5.5 | Mesmo lote com `stopOnError:false` | `ok=true`, `data.failed=1`, aviso "Step 1 ... failed and was skipped"; p1 fixada; um item de Desfazer |
| 5.6 | Referência entre passos: passo 0 = ferramenta que cria algo (ex.: `create_sheets` da F2), passo 1 = `set_elements_pinned {elementIds:["$steps[0].changes.created[0]"], pinned:true}` | o elemento criado no passo 0 sai fixado; um Ctrl+Z remove tudo |
| 5.7 | Lote com `{tool:"execute_batch"}` dentro | `STEP_NOT_BATCHABLE`, nada executa |
| 5.8 | Lote com referência a passo futuro (`"$steps[3].data.x"` no passo 0) | `BATCH_REFERENCE_INVALID` antes de executar qualquer passo |

## 6. Roteiro da definição de pronto (com F2 e F4)

| # | Ação | Esperado |
|---|---|---|
| 6.1 | `get_element_parameters` de uma parede | valores em mm/m²/m³/graus |
| 6.2 | `set_parameters` (ex.: Comentários de 1 parede) com `dryRun:true` | prévia com `before/after`; modelo intacto |
| 6.3 | Mesmo `set_parameters` sem `dryRun` (risco Low) | aplica direto; um item de Desfazer |
| 6.4 | `set_parameters` em lote/tipo (risco Medium, ex.: Comentários de todas as paredes ou parâmetro de tipo) | sem plano aprovado: `APPROVAL_REQUIRED` com dica para `propose_change_plan` |
| 6.5 | `propose_change_plan` com os passos de 6.4 | abre a **janela não-modal** no Revit com resumo e tabela; "Destacar no modelo" seleciona os elementos |
| 6.6 | Clique **Aprovar**; a IA chama `wait_for_plan_decision` e `execute_plan` | executa como **um** `execute_batch`; comprovante em `%APPDATA%\revit-mcp\plans\<planId>.proof.json` com aprovador = seu usuário do Windows |
| 6.7 | Ctrl+Z **uma vez** | desfaz todo o plano |
| 6.8 | Repita 6.5 e clique **Rejeitar** | `execute_plan` recusa; nada muda |
| 6.9 | Tente reexecutar o plano aprovado em 6.6 | recusado (at-most-once) |

## 7. Avisos e falhas do Revit (sem diálogo)

| # | Ação | Esperado |
|---|---|---|
| 7.1 | Provoque um aviso (ex.: `set_parameters` que gera elementos sobrepostos, ou uma criação duplicada da F2) | nenhum diálogo aparece; o texto do aviso vem em `warnings[]` |
| 7.2 | Provoque um erro de Revit numa escrita (ex.: valor inválido que o Revit rejeita no commit) | nenhum diálogo; `ok=false`, `TRANSACTION_ROLLED_BACK` com a mensagem do Revit; Desfazer sem item novo |

## 8. Encerramento

1. Feche os modelos **sem salvar** (ou apague a pasta `C:\temp\mcp-fase0`).
2. Registre o resultado (OK/FALHOU por item, versão/build do Revit, `capabilityHash`) no PR da `fase0`.
