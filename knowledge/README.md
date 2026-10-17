# Conhecimento do escritório (`knowledge/`)

Esta pasta guarda as **convenções do escritório** que a IA deve seguir ao trabalhar no Revit:
nomenclatura, padrões de folhas, parâmetros obrigatórios, tipos padrão, etc. A ferramenta de servidor
`get_office_knowledge` lê estes arquivos e os entrega ao cliente MCP.

## Convenção

- **Um arquivo markdown por tópico.** O nome do arquivo (sem `.md`) é o nome do tópico:
  `nomenclatura.md` → tópico `nomenclatura`.
- Nomes de tópico usam apenas letras, números, `-` e `_`. Subpastas são permitidas
  (`workflows/familia.md` → tópico `workflows/familia`), com até 4 níveis.
- A primeira linha `# Título` vira o título mostrado na lista de tópicos.
- Este `README.md` não é um tópico e não aparece na lista.
- Limite de 256 KB por arquivo. Se crescer além disso, divida em tópicos menores.
- Escreva regras objetivas e verificáveis, com exemplos (certo/errado). A IA segue melhor tabelas e listas
  curtas do que texto corrido.

## Quem mantém

O conteúdo é **do escritório**, não do código. Quem cuida dos padrões BIM edita os arquivos diretamente;
não é preciso reiniciar nada, porque a ferramenta relê os arquivos a cada chamada.

- Não coloque dados de cliente, senhas, tokens ou caminhos de rede sensíveis aqui.
- Para manter o conhecimento fora do repositório (por exemplo numa pasta compartilhada do escritório),
  defina a variável de ambiente `REVIT_MCP_KNOWLEDGE_DIR` com o caminho da pasta na configuração do
  servidor MCP. Sem ela, o servidor usa esta pasta `knowledge/` do repositório.

## Como a IA usa

1. `get_office_knowledge` sem argumentos → lista de tópicos (nome, título, tamanho, data de modificação).
2. `get_office_knowledge` com `topic: "folhas"` → conteúdo completo do tópico.

O acesso é somente leitura e restrito a esta pasta: nomes com `..`, caminhos absolutos ou links simbólicos
que apontem para fora dela são recusados.

## Exemplos incluídos

`nomenclatura.md`, `folhas.md` e `parametros.md` são **modelos com conteúdo fictício** (marcados como
`EXEMPLO`). Substitua pelo padrão real do escritório antes de usar em projeto.
