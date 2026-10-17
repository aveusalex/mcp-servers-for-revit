# Parâmetros obrigatórios e compartilhados

> **EXEMPLO / PLACEHOLDER.** Conteúdo fictício para mostrar o formato. Substitua pelo padrão real do
> escritório antes de usar em projeto.

## Arquivo de parâmetros compartilhados

- Caminho de exemplo: `<pasta do escritório>\BIM\Parametros-Compartilhados.txt` (substitua pelo real).
- Nunca crie parâmetro de projeto com o mesmo nome de um parâmetro compartilhado existente.

## Parâmetros obrigatórios por categoria

| Categoria | Parâmetro | Tipo | Instância/Tipo | Exemplo de valor |
|---|---|---|---|---|
| Paredes | `ESC_Codigo-Servico` | Texto | Tipo | `ALV-01` |
| Portas | `ESC_Codigo-Esquadria` | Texto | Tipo | `P1` |
| Ambientes | `ESC_Acabamento-Piso` | Texto | Instância | `Porcelanato 60x60` |
| Folhas | `ESC_Fase-Emissao` | Texto | Instância | `Executivo` |

## Regras de preenchimento

- Valores numéricos em **mm**, **m²** ou **m³**, sem a unidade no texto.
- Códigos em maiúsculas, sem acento.
- Não altere parâmetros de sistema críticos (nível, fase, workset) sem pedir confirmação.
