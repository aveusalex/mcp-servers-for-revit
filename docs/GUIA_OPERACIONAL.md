# Guia operacional — MCP para Revit

Este guia instala e opera a versão mantida em
[aveusalex/mcp-servers-for-revit](https://github.com/aveusalex/mcp-servers-for-revit).
Ele serve à equipe e aos agentes que precisem configurar o Windows/Revit.

> Instale Revit, plugin, broker e servidor MCP no mesmo Windows. O plugin se
> conecta sozinho ao broker local; o cliente MCP inicia o servidor por STDIO.
> Nunca exponha a porta 8090 na rede.

## 1. O que mudou

| Tema | MCP original | Esta versão |
| --- | --- | --- |
| Conexão | Plugin recebe conexão e costuma exigir ativação manual | Plugin conecta automaticamente a um broker local |
| Documentos | Um documento ativo | RVTs abertos podem ser selecionados pelo agente |
| Clientes MCP | Uma conversa por vez | Vários clientes, roteados por documento |
| Segurança | Sem token/auditoria nessa ponte | Token local, loopback e auditoria JSONL |
| Vínculos | Sem inventário dedicado | list_revit_links lê árvore, instâncias, estado e transformações |

~~~mermaid
flowchart LR
    C["Codex desktop, CLI ou cliente local"] -->|"STDIO"| S["Servidor MCP\nserver/"]
    S -->|"WebSocket + token\n127.0.0.1:8090"| B["Broker\nbroker/"]
    R1["Revit: RVT A"] --> B
    R2["Revit: RVT B"] --> B
    R3["Outra instância Revit"] --> B
~~~

Os documentos podem ser endereçados separadamente, mas os comandos continuam
passando pela fila da API do Revit. Isso não cria execução simultânea real na
mesma interface.

## 2. Windows local versus Codex na nuvem

Revit, plugin, broker e servidor MCP precisam ficar no Windows que abre os RVTs.
O aplicativo desktop, a CLI e a extensão de IDE do Codex compartilham a
configuração local. Codex/ChatGPT exclusivamente na web não lê o arquivo de
configuração do Windows e não ganha acesso ao Revit apenas porque este
repositório está no GitHub.

Para operar o Revit físico, use uma sessão local do Codex no computador do
Revit ou em uma VM Windows que tenha Revit. Um agente em nuvem pode ler este
repositório e orientar o trabalho, mas exigiria outra ponte, autenticada e
aprovada, para executar comandos locais. **Não transforme o broker em ponte
remota:** ele foi projetado para loopback.

## 3. Antes de instalar: inventário e responsabilidade

Num escritório pode haver MCPs do Revit original, PyRevit, Dynamo e outros
plugins. Eles só podem coexistir com responsabilidade bem definida.

1. Preencha [REGISTRO_MCPS.md](REGISTRO_MCPS.md) para cada computador.
2. Liste MCP, dono, finalidade, permissões de escrita e como desligá-lo.
3. Remova ou desabilite o add-in do MCP original antes de usar esta versão.
   Não mantenha duas instalações chamadas mcp-servers-for-revit na pasta
   Addins da mesma versão do Revit.
4. Defina fronteiras: por exemplo, PyRevit para rotinas de usuário, Dynamo para
   grafos aprovados e este MCP para conversa/agente. Os controles de segurança
   e o documento-alvo de um não se transferem aos outros.

## 4. Pré-requisitos

- Autodesk Revit 2020 a 2026 instalado e licenciado. Os exemplos usam 2025.
- Node.js **20 ou superior** (node --version).
- Git para Windows.
- Visual Studio 2022 com desenvolvimento desktop .NET, ou SDKs equivalentes.
- Revit 2020–2024: .NET Framework 4.8 Developer Pack.
- Revit 2025–2026: .NET 8 SDK.
- Permissão para gravar em %APPDATA%\Autodesk\Revit\Addins\<ano>\.

Use uma pasta estável, fora de OneDrive/Dropbox, como
C:\RevitMCP\mcp-servers-for-revit. Se PowerShell bloquear npm.ps1, use npm.cmd.

## 5. Instalação a partir do código-fonte

Feche o Revit antes de compilar ou substituir DLLs. No PowerShell:

~~~powershell
git clone https://github.com/aveusalex/mcp-servers-for-revit.git C:\RevitMCP\mcp-servers-for-revit
cd C:\RevitMCP\mcp-servers-for-revit

cd broker
npm.cmd ci

cd ..\server
npm.cmd ci
npm.cmd run build

cd ..
dotnet build .\plugin\RevitMCPPlugin.csproj -c "Debug R25"
dotnet build .\commandset\RevitMCPCommandSet.csproj -c "Debug R25"
~~~

Troque R25 por R20 até R26 conforme a versão instalada. O build Debug copia o
manifesto, plugin e command set para:

~~~text
%APPDATA%\Autodesk\Revit\Addins\<ano>\
├── mcp-servers-for-revit.addin
└── revit_mcp_plugin\
    └── Commands\RevitMCPCommandSet\<ano>\
~~~

Se o build não encontrar referências do Revit, confira a versão instalada e o
SDK/Developer Pack correspondente.

### Primeira abertura

1. Abra o Revit e aceite **Always Load / Sempre carregar** se solicitado.
2. Na aba mcp-servers-for-revit, abra **Settings**.
3. Habilite somente comandos necessários; comece pelos de leitura.
4. O plugin conecta automaticamente. Não é necessário ativá-lo manualmente; o
   botão da faixa serve para desligar/reconectar a sessão.

O token local fica em %APPDATA%\revit-mcp\broker-token. Nunca o coloque em Git,
chat, ticket ou configuração remota.

## 6. Declarar o MCP no Codex do Windows

O servidor MCP é STDIO local. No mesmo Windows do Revit, use:

~~~powershell
codex mcp add revit-mcp --env REVIT_MCP_BROKER_CMD=C:\RevitMCP\mcp-servers-for-revit\broker\src\cli.js -- node C:\RevitMCP\mcp-servers-for-revit\server\build\index.js
codex mcp list
~~~

O servidor inicia o broker automaticamente quando necessário. Para diagnóstico,
também é possível iniciá-lo manualmente:

~~~powershell
cd C:\RevitMCP\mcp-servers-for-revit\broker
npm.cmd start
~~~

No aplicativo desktop, vá em **Settings → MCP servers → Add server**, escolha
**STDIO**, use node como comando e o arquivo abaixo como argumento:

~~~text
C:\RevitMCP\mcp-servers-for-revit\server\build\index.js
~~~

Inclua a variável abaixo e reinicie o aplicativo:

~~~text
REVIT_MCP_BROKER_CMD=C:\RevitMCP\mcp-servers-for-revit\broker\src\cli.js
~~~

### Alternativa: config.toml

Em %USERPROFILE%\.codex\config.toml (ou em .codex\config.toml de um projeto
confiável), acrescente:

~~~toml
[mcp_servers.revit-mcp]
command = "node"
args = ["C:\\RevitMCP\\mcp-servers-for-revit\\server\\build\\index.js"]
default_tools_approval_mode = "writes"

[mcp_servers.revit-mcp.env]
REVIT_MCP_BROKER_CMD = "C:\\RevitMCP\\mcp-servers-for-revit\\broker\\src\\cli.js"
~~~

O modo writes pede aprovação para ferramentas que não são somente leitura. No
Codex, /mcp mostra os servidores ativos.

### Outros clientes MCP locais

Qualquer cliente que aceite STDIO pode iniciar o mesmo arquivo:

~~~json
{
  "mcpServers": {
    "revit-mcp": {
      "command": "node",
      "args": [
        "C:\\RevitMCP\\mcp-servers-for-revit\\server\\build\\index.js"
      ],
      "env": {
        "REVIT_MCP_BROKER_CMD": "C:\\RevitMCP\\mcp-servers-for-revit\\broker\\src\\cli.js"
      }
    }
  }
}
~~~

Cada conversa pode ter seu processo server/, todos usando o mesmo broker local.
Dê nomes distintos aos MCPs para não os confundir com PyRevit, Dynamo ou o
servidor original.

## 7. Primeiro teste seguro

Use uma cópia do RVT e mantenha o Revit sem diálogos pendentes. Peça ao agente:

1. list_open_documents — confirma os RVTs registrados.
2. set_target_document — escolhe explicitamente título ou docId.
3. analyze_model_statistics — leitura no documento selecionado.
4. list_revit_links — inventário somente leitura dos vínculos do host.
5. say_hello — opcional, confirma que o Revit recebeu um comando.

Com mais de um documento aberto, não deixe o agente adivinhar o alvo. Defina-o
antes de qualquer operação. Com um único RVT, as ferramentas existentes
continuam funcionando sem esse passo adicional.

## 8. Vários documentos e vínculos

### Vários documentos / instâncias

- list_open_documents mostra documentos de todas as sessões conectadas.
- set_target_document fixa o alvo da conversa; várias ferramentas também aceitam
  o argumento opcional document.
- Ferramentas doc-agnostic operam no documento-alvo, obedecendo as proteções de
  escrita.
- Ferramentas ui-bound exigem a janela ativa. Caso contrário retornam
  REQUIRES_ACTIVE_DOCUMENT; ative o RVT manualmente e repita.

### Vínculos Revit

list_revit_links lista vínculos carregados ou descarregados, instâncias, origem
quando disponível, Attachment/Overlay, filhos aninhados e transformações no
sistema do host. Isso resolve a **visibilidade e inventário** de um
condomínio/projeto composto; não permite editar um vínculo dentro do host.

Para editar uma casa vinculada ou outro RVT de origem:

1. Abra o RVT de origem normalmente no Revit.
2. Rode list_open_documents.
3. Use set_target_document para apontar para o arquivo de origem.
4. Faça a alteração como documento normal, segundo as regras de escrita.

## 9. Proteções que devem permanecer ligadas

- Broker limitado a 127.0.0.1:8090 e token compartilhado localmente.
- Auditoria em %APPDATA%\revit-mcp\audit\<data>.jsonl.
- AllowBackgroundWrites = false: não altera documentos não ativos.
- AllowAutoActivate = false: o agente não troca a janela ativa.
- Sincronização com o modelo central é bloqueada pelo plugin.
- Use modelo de teste antes de criar, editar ou excluir.

Não habilite escrita em segundo plano ou ativação automática apenas para fazer o
agente funcionar. Elas mudam o perfil de risco e exigem autorização do
responsável técnico pelo modelo.

## 10. Atualizar

1. Feche todas as instâncias do Revit.
2. Pare um broker iniciado manualmente, se houver.
3. Atualize e reconstrua:

~~~powershell
cd C:\RevitMCP\mcp-servers-for-revit
git pull --ff-only origin main

cd broker
npm.cmd ci

cd ..\server
npm.cmd ci
npm.cmd run build

cd ..
dotnet build .\plugin\RevitMCPPlugin.csproj -c "Debug R25"
dotnet build .\commandset\RevitMCPCommandSet.csproj -c "Debug R25"
~~~

4. Abra o Revit e repita os testes seguros da seção 7.

Nunca use git reset --hard como atualização. Se git pull falhar por alterações
locais, preserve-as e peça orientação.

## 11. Desinstalar ou desativar

Remova/desative revit-mcp na tela de MCP servers do Codex, ou exclua os blocos
[mcp_servers.revit-mcp] e [mcp_servers.revit-mcp.env] de
%USERPROFILE%\.codex\config.toml.

Depois:

1. Feche Revit e clientes MCP.
2. Encerre o broker caso ele tenha sido iniciado manualmente.
3. Remova **somente** estes itens da versão de Revit correspondente:

~~~text
%APPDATA%\Autodesk\Revit\Addins\<ano>\mcp-servers-for-revit.addin
%APPDATA%\Autodesk\Revit\Addins\<ano>\revit_mcp_plugin\
~~~

4. Abra o Revit e confirme que a aba do add-in não aparece mais.

Opcionalmente, após a equipe confirmar que não precisa de histórico nem chave
local, remova %APPDATA%\revit-mcp\. Isso apaga token e auditoria; não é etapa
obrigatória.

## 12. Diagnóstico rápido

| Sintoma | Verificação e ação segura |
| --- | --- |
| npm.ps1 bloqueado | Use npm.cmd ci, npm.cmd run build e npm.cmd start. |
| Porta 8090 em uso | Pode ser o broker já ativo. Não inicie outro; confira/reinicie um processo. |
| MCP sem documentos | Abra Revit, confirme add-in e aguarde; rode list_open_documents. |
| Timeout | Feche diálogos modais, sobretudo prompts CAD/DWF, e repita uma leitura. |
| AMBIGUOUS_TARGET | Há mais de um RVT: use list_open_documents e set_target_document. |
| REQUIRES_ACTIVE_DOCUMENT | É ui-bound; ative manualmente o RVT e tente novamente. |
| Vínculo não altera | Esperado: abra o RVT-fonte como documento normal e selecione-o. |
| Add-in não aparece | Confirme ano, pasta Addins, DLLs e aceite o carregamento do add-in. |

## 13. Instrução pronta para um agente local

~~~text
Você opera a instalação local do repositório aveusalex/mcp-servers-for-revit.
Leia AGENTS.md e docs/GUIA_OPERACIONAL.md integralmente antes de agir.

Objetivo: verificar e operar o MCP local para Revit sem expor a porta 8090 e
sem modificar modelos de produção por padrão.

Regras: mantenha o broker em 127.0.0.1, não revele o broker-token, não habilite
AllowBackgroundWrites nem AllowAutoActivate, e não faça commit/push/instalação
em produção sem autorização. Comece por list_open_documents,
set_target_document, analyze_model_statistics e list_revit_links em uma cópia
do modelo. Para editar um vínculo, abra o RVT de origem e selecione-o como
documento normal; nunca edite o vínculo diretamente pelo host.

Informe comandos, resultados e erros exatos. Pare antes de escrita, exclusão,
sincronização ou publicação e peça confirmação.
~~~

## Referências internas

- [README.md](../README.md): visão geral e ferramentas disponíveis.
- [DIVERGENCE.md](../DIVERGENCE.md): diferenças técnicas em relação ao upstream.
- [REGISTRO_MCPS.md](REGISTRO_MCPS.md): inventário de MCPs do escritório.
- [AGENTS.md](../AGENTS.md): regras que o Codex lê neste repositório.
- [Documentação oficial de MCP no Codex](https://learn.chatgpt.com/docs/extend/mcp):
  configuração por CLI, aplicativo desktop e config.toml.
