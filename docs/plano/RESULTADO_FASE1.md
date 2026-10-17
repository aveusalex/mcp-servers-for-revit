# Fase 1: o que foi entregue e testado

Data: 17/10/2026 · Branch `fase0` · Revit 2026.5 (pt-BR), modelos descartáveis em `C:\temp\mcp-fase0\`

## Entregue

**138 ferramentas no framework** (uma classe C# cada, registro automático, schema gerado), mais as 24 antigas e as
ferramentas de servidor (aprovação, jobs, conhecimento do escritório) — **173 no total** no `tools/list`.

| Grupo (toolset) | Nº | Exemplos |
|---|---|---|
| query | 17 | `get_project_info`, `get_element_details`, `list_spatial_elements` (status placed/notPlaced/notEnclosed), `get_room_openings` (por fase), `get_room_boundaries`, `find_elements_in_volume`, `get_link_elements`, `get_session_changes` |
| selection | 7 | `select_elements`, `activate_view`, `show_elements`, `set_view_temporary_visibility`, seleções salvas |
| parameters | 12 | `get/set_parameters`, `set_type_parameters`, `bulk_update_parameters`, `copy_parameters`, exportar/importar tabela CSV (pt-BR), `bind_shared_parameter`, `set_project_info`, `set_element_phase` |
| editing | 6 | `transform_elements`, `modify_element`, `renumber_elements`, `rename_elements`, `set_wall_base_top`, `set_elements_pinned` |
| families | 8 | `change_element_type`, `replace_type_instances`, `duplicate_type`, `rename_types`, camadas (`get/set_compound_structure`), `load_family` |
| materials | 4 | `list_materials`, `create_material`, `set_material_graphics`, `assign_material` ("trocar acabamento" à MAPA) |
| architecture | 13 | `create_walls`, `create_floor`, `create_ceiling`, `place_door_window` (encaixe em mm), `place_family_instance`, `create_rooms`, `setup_project_levels` (AC/ES/FO declarativo à MAPA), `create_room_perimeter_profile` (rodapé/tabica), `create_room_envelope`, `create_countertop` (tampo com cuba, saia e rodabanca) |
| views | 16 | `create_view`, `create_room_views` (planta recortada + elevações por ambiente à MAPA), cortes, elevações, callouts, templates, faixa de vista, `capture_view_image` (a IA vê a vista) |
| sheets | 13 | `create_sheets`, `place_on_sheet`, `align_viewports`, `duplicate_sheet`, `renumber_sheets`, carimbo, revisões |
| schedules | 8 | `create_schedule`, `get_schedule_data`, campos, filtros/ordenação, `export_schedule` |
| annotation / graphics | 9 | `tag_all_in_view`, `tag_elements`, `create_text_note`, `dimension_elements`, filtros de vista, sobreposições, cor por parâmetro |
| qa | 13 | `get_model_health` (0–100, A–F), `get_model_warnings`, `warnings_delta`, `purge_unused`, `check_clashes`, `check_naming`, `audit_*`, `compute_room_finishes` |
| export / documents | 9 | `export_pdf`, `export_cad`, `export_ifc`, `export_elements_data`, `get_view_image`, vínculos, `save_document`, `list_recent_models` |

Infra acrescentada nesta fase: `DryRunDefault` (ferramentas em massa só mostram a prévia sem `dryRun:false`),
`PathPolicy` (arquivos só em `Documents\RevitMCP` ou `REVIT_MCP_ALLOWED_DIRS`), normalização de enums no servidor,
nomes não encontrados listam os disponíveis, números canônicos iguais em C# e JS no manifest.

## Testado ao vivo (Revit 2026)

OK: `get_session_context`, `list_levels`, `create_walls` (após correção: contorno = face interna, ambiente 24 m²),
`list_families`, `find_elements` (categorias em pt-BR), `place_door_window` (porta; janela com peitoril; sobreposição
recusada com números em mm), `create_rooms` (todos os circuitos), `list_spatial_elements`, `create_floor` e
`create_ceiling` (pelo ambiente), `get_room_openings`, `get_room_boundaries`, `create_view` (planta e 3D),
`create_room_views` (prévia), `create_schedule` + `get_schedule_data`, `capture_view_image` (imagem chega à IA),
`create_sheets` + `place_on_sheet`, `tag_all_in_view` (prévia por padrão), `create_text_note`, `export_pdf`,
`get_compound_structure`, `list_materials`, `duplicate_type`, `change_element_type`, `assign_material` (recusa clara
sem camada de acabamento), `transform_elements`, `rename_elements` (prévia por padrão), `set_project_info`,
`get_element_details`, `get_model_health`, `get_model_warnings`, `check_naming`, `find_untagged_elements`,
`check_clashes`, `purge_unused` (lista sem aprovação), `compute_room_finishes`, `setup_project_levels` (plano completo
AC/ES/FO), `create_countertop`, `create_room_envelope`.

## Bugs encontrados ao vivo e corrigidos (10–16)

10. `create_walls` ignorava a linha de localização (paredes centradas no contorno) — agora recria a parede na linha
    de centro deslocada; mover a parede no meio da transação fazia o Revit lançar `InternalException`.
11. `purge_unused` pedia aprovação até para listar — prévia por padrão.
12. Nome de ambiente vinha com o número ("Sala 1") — usa `ROOM_NAME`.
13. Prévia em documento não ativo era bloqueada — permitida para ferramentas do framework (sempre revertida).
14. "Não encontrado" não dizia o que existe — lista os disponíveis.
15. Enums com estilos misturados — o servidor aceita variações de maiúsculas, espaço, `_` e `-`.
16. `exteriorDirection` vinha como ponto em mm — vetor unitário.

## Ainda não testado ao vivo

Exportação IFC/DWG, `bind_shared_parameter`, `import_parameters_table`, `renumber_*` aplicados, `duplicate_sheet`,
`manage_links`, `save_document`, `set_compound_structure`, `set_material_graphics`, filtros de vista, cotas,
elevações/cortes aplicados (não só prévia), ferramentas `UI` (seleção/vista ativa) e qualquer coisa em modelo
workshared. As ferramentas de risco médio aplicadas de verdade passam pela janela de aprovação (testada na Fase 0).

## MAPA

O catálogo ao vivo do MAPA (≈221 ferramentas registradas, 72 travadas por licença) só sai depois do aceite do EULA e
login na aba MAPA — o log do add-in mostra "WebSocket server held pending EULA consent". As capacidades interessantes
inspiradas nele (envelope do ambiente, níveis AC/ES/FO, rodapé/tabica, tampo, vistas por ambiente, trocar
acabamento) estão implementadas e testadas acima, com código próprio.

## Rodadas de teste 2 e 3 + funções do MAPA da Fase 2 (17/10/2026, tarde)

**Agora: 161 ferramentas no framework** (+11 de edição/grupos/geometria, +12 de marcenaria e documentação).

Funções inspiradas no MAPA, testadas ao vivo:
- **Marcenaria** — `create_millwork_module` (balcão de pia 2 portas, aéreo, gaveteiro 3 gavetas; peças de MDF como
  DirectShapes de gabinete, agrupadas, com material por peça), `configure_millwork_module` (redimensiona/reconstrói
  no mesmo lugar, sem deixar tipo de grupo órfão), `millwork_cutting_list` (lista de corte por material, com chapas).
- Grupos (`manage_groups`, `list_groups`, `place_group_instance`), `array_elements`, `create_similar`,
  `align_distribute_elements`, `join_cut_geometry`, `edit_element_curve` (dividir parede: ok sem recriar),
  `create_reference_plane`, `set_true_north`, `delete_elements` (prévia lista dependentes: parede → porta),
  `modify_viewport`, `open_view`, `get_view_info`, `list_view_filters`, `get_selection`, linhas de detalhe
  (retângulo, círculo), região preenchida, parâmetros de família, editar família.

Também testados ao vivo nesta rodada (além da lista acima da Fase 1): exportação DWG e IFC, CSV de dados e de
parâmetros + reimportação (prévia), imagem com `saveTo` de arquivo, seleção/vista ativa/mostrar elementos (UI),
seleções salvas, faixa de vista, recorte, propriedades, duplicar vista, visibilidade de categoria, aplicar template,
filtro de vista (criar/aplicar), corte, elevações, callout, etiqueta, parâmetros do carimbo, revisão atribuída à
folha, duplicar e renumerar folhas (troca A↔B), fase, base/topo de parede, edição composta, copiar parâmetros,
atualização em massa, troca de tipo em massa, renomear tipos, renumerar portas/janelas, material novo e gráficos,
rodapé por perímetro (wall sweep), linhas separadoras, níveis, família hospedada, baseline de avisos, salvar (prévia).

Bugs desta rodada, corrigidos e revalidados (17–22): origem interna contada como conteúdo do ambiente; `saveTo` de
arquivo virava pasta; revisão "não atribuída" (lista do Revit desatualizada antes de regenerar); cor por parâmetro
de tipo vazia; ids texto × número entre ferramentas (o servidor agora aceita os dois); tipo de grupo órfão ao
reconfigurar marcenaria.

As ferramentas de risco médio foram exercitadas em modo prévia (o código roda inteiro no Revit e a transação é
desfeita no fim); aplicá-las de verdade exige aprovação humana na janela do Revit, por desenho — a janela e o fluxo
de aprovação foram testados com aprovação real na Fase 0.

## Rodadas R1/R2 + fechamento da cobertura do MAPA (17/10/2026, noite)

**Agora: 177 ferramentas no framework** e 6 roteiros (prompts MCP). Cobertura do MAPA: das 182 ferramentas não
proprietárias, **182 têm equivalente** (ver `COBERTURA_MAPA.md`); 34 descartadas por serem proprietárias/específicas.

Novas e testadas ao vivo nesta rodada:
- `create_finish_material` — MDF com imagem de textura, revestimento 600×600 com junta de 2 mm (padrão de
  superfície gerado), tinta por cor.
- `import_image` — imagem na planta, em escala, embutida no modelo.
- `manage_assembly` (criar, desmontar) + `create_assembly_views` (3D, elevações, lista de peças e folha).
- `create_locked_alignment` — trava cadeado entre plano de referência e face de parede; recusa com a distância
  exata quando as referências não coincidem.
- `set_work_plane`, `create_reference_plane`, `create_family_from_template` (prévia: família montada e flexionada
  em memória; largura/profundidade/altura travadas).
- `import_mesh` (novo, equivalente ao "import_mesh_as_family" do MAPA) — OBJ/STL (ASCII e binário) vira um tipo
  DirectShape reutilizável na categoria escolhida: unidades, eixo vertical, âncora, nível, rotação, material por
  `usemtl`; sólido quando a malha é fechada. Testado com banco OBJ (Y para cima, em m) e pirâmide STL binária.
- Roteiros novos: `roteiro_familia` e `roteiro_conversao`.

Bugs desta rodada, corrigidos e revalidados (23–26):
23. Prévias (dryRun) apareciam no histórico de desfazer e em `get_session_changes` — o Revit não avisa quando um
    TransactionGroup é revertido; o log agora descarta os eventos da prévia.
24. Caminho relativo à raiz (`textures/x.png`) virava `textures\textures\x.png` — leitura cai para a raiz quando o
    arquivo está lá.
25. `manage_assembly` disassemble era revertido pelo Revit (as vistas do conjunto não sobrevivem a ele) — apaga as
    vistas do conjunto antes e informa quais.
26. `create_family_from_template` avisava "nome já em uso" ao renomear o tipo padrão para o mesmo nome.

Não testados ao vivo: `create_void_cut` (precisa de uma família com vazio não anexado) e `load_rvt_as_group` (precisa
de um .rvt salvo nas pastas permitidas; salvar é risco médio e passa por aprovação).
