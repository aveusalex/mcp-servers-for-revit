# Registro de MCPs do escritório

Preencha esta tabela antes de liberar um agente para operar modelos. Ela evita
que MCPs distintos pareçam uma única ferramenta e deixa claro quem pode parar
ou atualizar cada integração.

| Nome exibido no cliente | Repositório/pacote | Computador ou VM | Finalidade | Pode escrever em RVT? | Dono técnico | Como desativar | Situação |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `revit-mcp` | `aveusalex/mcp-servers-for-revit` |  | Ponte local para Revit e inventário de vínculos | Sim, sob guardrails |  | Remover MCP + add-in |  |
| `oc-mapa-ia` | MAPA para Revit 0.2.7 (Oficina Criativa, proprietário; build `37a44fc1d`) | DESKTOP-ROM29GI (Alex) | Ferramentas de interiores/marcenaria do MAPA; conector .NET 8 via WebSocket próprio (porta padrão 8765), independente do broker 8090 | Sim (licença MAPA IA) | Oficina Criativa / Alex | Instalador do MAPA → Desinstalar; remover a entrada `oc-mapa-ia` do `claude_desktop_config.json` | Instalado em 17/10/2026; aguardando aceite do EULA e login |
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
