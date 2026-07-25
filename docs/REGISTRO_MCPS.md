# Registro de MCPs do escritório

Preencha esta tabela antes de liberar um agente para operar modelos. Ela evita
que MCPs distintos pareçam uma única ferramenta e deixa claro quem pode parar
ou atualizar cada integração.

| Nome exibido no cliente | Repositório/pacote | Computador ou VM | Finalidade | Pode escrever em RVT? | Dono técnico | Como desativar | Situação |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `revit-mcp` | `aveusalex/mcp-servers-for-revit` |  | Ponte local para Revit e inventário de vínculos | Sim, sob guardrails |  | Remover MCP + add-in |  |
| `pyrevit` |  |  |  |  |  |  |  |
| `dynamo` |  |  |  |  |  |  |  |
| `mcp-original` |  |  |  |  |  |  | Desativar antes de usar este fork |
| Outro |  |  |  |  |  |  |  |

## Critérios de convivência

- Um único add-in deve ser a ponte MCP principal para comandos Revit em cada
  perfil/versão do Revit.
- Não exponha `revit-mcp` pela rede: ele é local ao Windows do Revit.
- Registre qualquer MCP que execute scripts, Dynamo graphs ou comandos PyRevit
  com permissão de escrita.
- Cada computador deve ter seu próprio inventário, token e auditoria local.
- Antes de atualizar ou remover, registre a data, responsável e o resultado do
  teste de leitura pós-mudança.
