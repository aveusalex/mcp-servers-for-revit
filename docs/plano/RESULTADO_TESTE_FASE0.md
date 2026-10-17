# Resultado do teste da Fase 0 no Revit 2026

Data: 17/10/2026 · Revit 2026.5 (26.5.0.55, pt-BR) · add-in `RevitMCPCommandSet 1.0.0+9c1e432` ·
`capabilityHash 4543b52b…` (igual ao manifest do servidor) · execução automatizada pela sessão principal
(UI Automation para teclas/diálogos), com o Alex só aprovando planos.

Modelos descartáveis: `C:\temp\mcp-fase0\seed.rte` (A, 4 paredes 220128–220131 + porta 220132) e
`C:\temp\mcp-fase0\docB.rte` (B, vazio), ambos abertos a partir do template padrão PT-BR.

## Veredito

**Fase 0 aprovada**, depois das correções abaixo (todas já commitadas na `fase0`).
33 passos OK, 0 falhando, 4 pulados (ver tabela).

## Passos

| # | Resultado | Observação |
|---|---|---|
| 1.1 | OK | 2 documentos com `docId` distintos (após correção do bug 6), hash igual ao manifest, `queue.pending=0` |
| 1.2–1.3 | PULADO | documento de família não testado nesta rodada |
| 1.4 | OK | 8 ferramentas registradas sozinhas (log: "Registered 8 framework tool(s)") |
| 2.1 | OK | prévia: `changes.modified=[p1,p2]`, modelo intacto (confirmado por 2.2) |
| 2.2 | OK | aplica direto (risco baixo) |
| 2.3 | OK | idempotente, `unchanged=[p1,p2]`, **sem** item vazio no Desfazer |
| 2.4 | OK | `NOT_FOUND` com dica |
| 2.5 | OK (por schema) | argumento desconhecido é barrado pelo schema (`additionalProperties:false`) antes de chegar ao Revit |
| 2.6 | OK | `INVALID_ARGUMENTS` "needs at least 1 item" |
| 2.7 | OK | um Ctrl+Z desfaz |
| 3.1 | OK | `targetDocId`=B, `activeDocId`=A |
| 3.2 | OK | leitura no B sem ativá-lo |
| 3.3 | PULADO | ainda não há ferramenta `Kind=UI` no framework |
| (extra) | OK | escrita no B não ativo → `BACKGROUND_WRITE_BLOCKED` |
| 3.4 | PULADO | fechamento do B não testado |
| 4.1 | OK | diálogo modal aberto → `REVIT_BUSY` em 45 s, "nothing was changed" |
| 4.2 | OK | depois de fechar o diálogo o pedido cancelado **não** foi aplicado |
| 5.1–5.2 | OK | lote de 2 passos, um Ctrl+Z desfaz as 3 paredes |
| 5.3 | OK | `committed=false`, modelo intacto |
| 5.4 | OK | `BATCH_STEP_FAILED`, passo 0 também desfeito |
| 5.5 | OK | `stopOnError:false`: `failed=1`, aviso "skipped", passo 0 mantido |
| 5.6 | OK | `create_sheets` + `$steps[0].changes.created[0]`: folha T-001 criada e fixada |
| 5.7 | OK | lote aninhado recusado (`BAD_REQUEST` no broker, antes do Revit) |
| 5.8 | OK | `BATCH_REFERENCE_INVALID` antes de executar |
| 6.1 | OK | comprimento 6000 mm, área 16,65 m², volume 2,50 m³, largura de tipo 150 mm |
| 6.2–6.3 | OK | prévia e escrita com `revert[]` |
| 6.4 | OK | `set_type_parameters` → `APPROVAL_REQUIRED` com o passo exato |
| 6.5 | OK | janela em pt-BR; "Destacar no modelo" → "1 elemento(s) selecionado(s)" (após correção do bug 7) |
| 6.6 | OK | aprovado pelo Alex; executado; `instancesAffected=2`; comprovante `plans/plan_074cff50d44e.proof.json`, aprovador `alex` |
| 6.7 | OK | um Ctrl+Z desfaz o plano |
| 6.8 | PULADO | rejeitar exige clique humano; coberto pelos testes automáticos do broker |
| 6.9 | OK | `PLAN_ALREADY_EXECUTED` |
| 7.1 | OK | aviso "Os elementos têm valores 'Marca' duplicados." em `warnings[]`, sem diálogo |
| 7.2 | OK* | erro do Revit (altura −500) → `TRANSACTION_ROLLED_BACK`, sem diálogo, nada alterado. *O motivo não veio (bug 8, corrigido; revalidar no próximo reinício do Revit) |

## Bugs encontrados e corrigidos

1. **Registro de comandos vazio numa instalação nova** — nenhum dos 24 comandos antigos carregava
   ("Method not found: 'send_code_to_revit'"). O plugin agora semeia o registro a partir do `command.json`.
2. **Janela de aprovação em inglês** (pedido do Alex) — traduzida; horário de expiração local.
3. **Janela de aprovação abria atrás das outras** — agora vem para a frente e pisca na barra de tarefas.
4. **`wait_for_plan_decision` repetia o plano inteiro (com código) a cada consulta** — resposta compacta enquanto pendente.
5. **Testes do servidor colidiam com o broker real na porta 8090** — usam porta livre (`REVIT_MCP_BROKER_PORT`).
6. **Dois documentos com o mesmo `docId`** (cópias do mesmo arquivo / mesmo template) — o broker via só um.
   Com colisão, cada `docId` ganha o sufixo `~<chave do caminho>`.
7. **"Destacar no modelo" desabilitado** para ferramentas que recebem ids como texto — o broker passou a reconhecer ids numéricos em texto.
8. **Erro do Revit sem motivo** — falhas que não passam pelo pré-processador agora são capturadas no `FailuresProcessing`.
9. **Plano de documento que não existe mais** mostrava "NOT_DOCUMENT_OWNER" cru, e a janela continuava aprovável —
   mensagens em pt-BR, botões desabilitados de vez, e janelas de planos expirados se desabilitam sozinhas.

## Decisões

- `send_code_to_revit` com `transactionMode:"rollback"` **continua risco alto**: desfazer a transação protege o
  modelo, mas o código C# ainda pode mexer em arquivos, abrir documentos ou acessar a rede.

## Ainda a revalidar no próximo reinício do Revit

- Bug 8 (motivo do erro) e bug 9 (mensagens/expiração da janela) — o plugin com essas correções foi compilado,
  mas o Revit em uso ainda roda a versão anterior.
