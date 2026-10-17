# Nomenclatura de elementos, vistas e tipos

> **EXEMPLO / PLACEHOLDER.** Conteúdo fictício para mostrar o formato. Substitua pelo padrão real do
> escritório antes de usar em projeto.

## Regras gerais

- Use letras maiúsculas sem acento nos códigos; descrições podem ter acento.
- Separador entre campos: `_` (sublinhado). Nada de espaços duplos.
- Disciplinas: `ARQ` (arquitetura), `EST` (estrutura), `HID` (hidráulica), `ELE` (elétrica), `AVAC` (ar-condicionado).

## Vistas

Formato: `<DISCIPLINA>_<TIPO>_<NÍVEL>_<DESCRIÇÃO>`

| Tipo de vista | Código | Exemplo |
|---|---|---|
| Planta baixa | `PB` | `ARQ_PB_N01_Layout` |
| Planta de forro | `PF` | `ARQ_PF_N01_Forro` |
| Corte | `CT` | `ARQ_CT_AA_Geral` |
| Elevação | `EL` | `ARQ_EL_Norte_Fachada` |
| 3D | `3D` | `ARQ_3D_Estudo-Volumetria` |

Errado: `Planta 1`, `Level 1 copy`, `ARQ - PB - N01`.

## Níveis

Formato: `N<nn>_<descrição>`, por exemplo `N00_Terreo`, `N01_Pavimento-Tipo`, `N99_Cobertura`.

## Tipos de família

Formato: `<material ou sistema>_<espessura ou dimensão>` em milímetros, por exemplo
`Alvenaria-Bloco-Ceramico_140`, `Porta-Madeira_80x210`.
