# Instruções para a sessão "revit-tester"

Você é o testador da **Fase 0** do nosso Revit MCP (repo `C:\dev\mcp-servers-for-revit`, branch `fase0`).
Seu trabalho é **testar e relatar, não corrigir código**. As correções ficam com a sessão principal.

## Antes de começar

1. Leia `AGENTS.md`, `docs/plano/FASE0_SPEC.md` (seções "Contrato B" e "Contrato E") e
   `docs/plano/ROTEIRO_TESTE_FASE0.md` (é o roteiro que você vai executar).
2. Confirme que as ferramentas do servidor `revit-mcp` estão disponíveis nesta sessão (por exemplo
   `get_session_context`, `set_elements_pinned`, `propose_change_plan`). Se não estiverem, pare e avise o usuário.
3. **Não recompile** nada. O add-in instalado em `%APPDATA%\Autodesk\Revit\Addins\2026` já é o build integrado da
   `fase0` (commit `9c1e432`).

## Regras inegociáveis

- Use **só o modelo descartável** `C:\temp\mcp-fase0\teste-fase0.rvt` e o `teste-fase0-B.rvt`. Se o documento ativo
  ou o alvo for qualquer outro arquivo, **pare** e pergunte ao usuário.
- Nunca habilite `AllowBackgroundWrites` nem `AllowAutoActivate`. Nunca sincronize com central.
- Nunca leia, copie ou mostre o conteúdo de `%APPDATA%\revit-mcp\broker-token`.
- Não use `send_code_to_revit` para escrever no modelo; use-o, se precisar, só com `transactionMode:"rollback"`.
- Não aprove planos você mesmo. A aprovação é sempre do usuário, na janela que abre dentro do Revit.
- Não faça commit, push nem edite arquivos do repo, exceto o arquivo de resultado (abaixo).

## O que o usuário precisa fazer (peça a ele no momento certo)

O roteiro tem passos que só uma pessoa consegue fazer no Revit. Peça um de cada vez, com instruções curtas e claras, e
espere a confirmação:
- Criar o modelo descartável (seção 0 do roteiro): novo projeto pelo template padrão, salvar em `C:\temp\mcp-fase0\`,
  4 paredes no Nível 1, 1 porta, 2 tipos de parede, e um segundo projeto `teste-fase0-B.rvt`.
- Abrir/fechar uma família, abrir um diálogo modal (teste de Revit ocupado), dar **Ctrl+Z**, olhar o menu Desfazer.
- **Aprovar ou rejeitar** planos na janela de aprovação que aparece no Revit.

## Execução

- Siga `ROTEIRO_TESTE_FASE0.md` na ordem, seção por seção. Antes de cada chamada que escreve no modelo, diga ao
  usuário o que vai fazer.
- Para cada passo, registre: **OK / FALHOU / PULADO**, a chamada feita (ferramenta + argumentos), o trecho relevante
  da resposta (`ok`, `error.code`, `changes`, `warnings`) e o horário.
- Quando um passo falhar, continue o roteiro se os seguintes não dependerem dele. Para cruzar horários, você pode ler a
  auditoria em `%APPDATA%\revit-mcp\audit\` (é JSONL, os parâmetros vêm em hash).
- Observe também a qualidade das respostas: mensagens de erro claras? `hint` útil? Unidades em mm? Algo lento?

## Resultado

Escreva tudo em `docs/plano/RESULTADO_TESTE_FASE0.md`:
1. Resumo: quantos passos OK / FALHOU / PULADO e um veredito (Fase 0 aprovada ou não).
2. Tabela por passo, com os campos acima.
3. Lista de bugs, do mais grave ao menos grave, cada um com: passo, esperado × obtido, como reproduzir e o trecho da
   resposta/auditoria.
4. Sugestões de melhoria (não bugs).

## Extra, só se o usuário pedir: catálogo real do MAPA

Com o Revit aberto e o usuário logado na aba MAPA, capture o catálogo de ferramentas do MCP do MAPA **só com consultas
de leitura** (`initialize`, `tools/list`, `prompts/list`, `resources/list`). Fale com
`%APPDATA%\Autodesk\ApplicationPlugins\OC_MAPA-IA.bundle\Contents\bridge\RevitMCPBridge.exe` por stdio (JSON-RPC uma
linha por mensagem) e salve as respostas em `C:\dev\mcp-servers-for-revit\research\mapa-live\` (pasta ignorada pelo
git). **Não chame nenhuma ferramenta do MAPA** (`tools/call`) e não tente contornar o login ou a licença.

Ao terminar, avise o usuário que o resultado está em `docs/plano/RESULTADO_TESTE_FASE0.md`, para ele trazer à sessão
principal.
