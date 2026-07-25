# Instruções para agentes

Este repositório instala e opera uma ponte local entre clientes MCP e Autodesk
Revit no Windows. Antes de alterar, instalar, atualizar ou testar qualquer
coisa, leia integralmente [docs/GUIA_OPERACIONAL.md](docs/GUIA_OPERACIONAL.md).

Regras inegociáveis:

- O broker escuta somente em `127.0.0.1:8090`. Nunca exponha essa porta pela
  rede, Tailscale, túnel, proxy ou regra de firewall.
- Nunca copie, publique ou peça o conteúdo de `%APPDATA%\\revit-mcp\\broker-token`.
- Comece por testes de leitura em um modelo descartável. Não habilite
  `AllowBackgroundWrites` nem `AllowAutoActivate` sem autorização explícita do
  responsável técnico.
- Um vínculo Revit é somente leitura. Para editar o arquivo vinculado, abra o
  RVT de origem como documento normal e direcione a operação a ele.
- Não faça commit, push, release, instalação em produção, exclusão de arquivos
  ou alteração de modelo sem confirmação explícita do usuário.
- Quando houver vários MCPs no escritório, mantenha este MCP como a única ponte
  responsável por comandos Revit e registre os demais em
  [docs/REGISTRO_MCPS.md](docs/REGISTRO_MCPS.md).
