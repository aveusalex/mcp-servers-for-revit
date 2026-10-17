# Apêndice: catálogo consolidado de capacidades

Gerado de `research/capabilities-all.json` (298 linhas; 8 duplicatas entre grupos unificadas → 290 capacidades). Detalhes de desenho, fontes e armadilhas de cada ID: `research/capabilities/<grupo>.md`.

Duplicatas unificadas: VIS-01 → CON-06, MAT-03 → CON-08, FAM-08 → CON-24, TAB-08 → EXP-06, FAM-13 → ARQ-06, VIS-16 → MAT-01, ANO-11 → FOL-16, INF-12 → UND-01.

## Fase 1 (153)

### Consulta, seleção e histórico (28)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| CON-01 | `get_project_info` | 5 | 7 | P | RES PAGE | parcial (list_open_documents, analyze_model_statistics) |
| CON-02 | `get_element_details` | 5 | 5 | P | RES PAGE | parcial (get_selected_elements) |
| CON-03 | `find_elements` | 5 | 7 | M | RES PAGE | parcial (ai_element_filter, get_current_view_elements) |
| CON-12 | `list_spatial_elements` | 5 | 3 | P | RES PAGE | parcial (export_room_data) |
| UND-01 | `batch_execute` | 5 | 2 | M | TX DRY BATCH CHG APR | não |
| UND-02 | `propose_change_plan` | 5 | 1 | M | APR DRY CHG FILE | não |
| UND-03 | `approve_or_reject_plan (somente humano)` | 5 | 1 | M | APR | não |
| UND-04 | `execute_plan` | 5 | 1 | M | APR TX BATCH CHG LONG | não |
| CON-05 | `list_levels` | 4 | 3 | P | RES | não |
| CON-06 | `list_views` | 4 | 2 | P | RES PAGE | parcial (get_current_view_info) |
| CON-09 | `list_phases` | 4 | 4 | P |  | não |
| CON-10 | `list_worksets` | 4 | 3 | P | PAGE | não |
| CON-13 | `get_room_boundaries` | 4 | 2 | P | RES | não |
| CON-14 | `get_room_openings` | 4 | 3 | P | RES PAGE | não |
| CON-15 | `get_element_rooms` | 4 | 1 | P |  | não |
| CON-16 | `find_elements_in_volume` | 4 | 3 | P | RES PAGE | parcial (ai_element_filter bbox) |
| CON-17 | `get_element_relationships` | 4 | 1 | P | PAGE | não |
| CON-21 | `get_link_elements` | 4 | 3 | P | RES PAGE | parcial (list_revit_links) |
| CON-23 | `get_project_coordinates` | 4 | 2 | P |  | parcial (list_revit_links dá transforms) |
| CON-24 | `list_families` | 4 | 1 | P | RES PAGE | parcial (get_available_family_types) |
| SEL-01 | `select_elements` | 4 | 5 | P | UI RES | parcial (operate_element Select) |
| SEL-03 | `saved_selection` | 4 | 3 | P | TX DRY CHG PAGE | não |
| SEL-04 | `activate_view` | 4 | 5 | P | UI RES | não |
| SEL-05 | `show_elements` | 4 | 2 | P | UI | parcial (operate_element select) |
| SEL-06 | `set_view_temporary_visibility` | 4 | 5 | P | UI TX DRY | parcial (operate_element) |
| UND-05 | `get_plan` | 4 | 1 | P | FILE PAGE | não |
| UND-07 | `undo_history` | 4 | 1 | M | UI CHG | não |
| UND-09 | `get_session_changes` | 4 | 2 | P | CHG PAGE | não |

### Exportação e QA (27)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| EXP-01 | `export_pdf` | 5 | 7 | M | FILE LONG RES | não |
| EXP-02 | `export_cad` | 5 | 4 | P | FILE LONG RES | não |
| EXP-03 | `get_view_image` | 5 | 6 | P | IMG FILE | não |
| EXP-06 | `export_schedule` | 5 | 2 | P | FILE RES | não |
| EXP-07 | `export_elements_data` | 5 | 5 | M | PAGE FILE RES | parcial (export_room_data só ambientes) |
| QA-01 | `get_model_warnings` | 5 | 4 | P | PAGE RES | não |
| QA-02 | `warnings_delta` | 5 | 1 | P | PAGE | não |
| QA-03 | `get_model_health` | 5 | 7 | M | PAGE KNOW | parcial (analyze_model_statistics) |
| QA-05 | `purge_unused` | 5 | 2 | M | TX DRY CHG APR BATCH KNOW | não |
| QA-06 | `find_untagged_elements` | 5 | 2 | P | PAGE RES | não |
| QA-08 | `check_clashes` | 5 | 7 | M | PAGE RES LONG | não |
| QA-12 | `check_required_parameters` | 5 | 3 | M | PAGE RES KNOW | não |
| QA-13 | `audit_sheets_views` | 5 | 2 | M | TX DRY CHG APR PAGE KNOW | não |
| EXP-04 | `export_ifc` | 4 | 4 | M | FILE LONG TX | não |
| EXP-08 | `manage_links` | 4 | 5 | P | RES APR | parcial (list_revit_links só lista RVT) |
| EXP-09 | `link_file` | 4 | 3 | M | TX CHG FILE LONG | não |
| QA-04 | `audit_families` | 4 | 4 | M | PAGE RES LONG | não |
| QA-07 | `find_undimensioned_elements` | 4 | 2 | P | PAGE RES | não |
| QA-09 | `check_clearance` | 4 | 2 | M | RES PAGE | não |
| QA-11 | `check_naming` | 4 | 5 | M | PAGE KNOW | não |
| QA-16 | `cad_cleanup` | 4 | 1 | P | TX DRY CHG APR | não |
| QA-17 | `audit_links_worksets` | 4 | 2 | M | PAGE KNOW | parcial (list_revit_links) |
| QA-18 | `audit_openings` | 4 | 1 | M | PAGE RES KNOW | não |
| QA-19 | `audit_walls` | 4 | 2 | M | PAGE RES | não |
| QA-20 | `check_levels_grids` | 4 | 1 | P | PAGE | não |
| QA-22 | `model_diff` | 4 | 1 | M | FILE PAGE LONG | não |
| QA-23 | `compute_room_finishes` | 4 | 1 | M | PAGE RES FILE | não |

### Criação (arquitetura, estrutura, MEP) (12)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| ARQ-01 | `create_walls` | 5 | 13 | M | TX DRY BATCH CHG RES | parcial (create_line_based_element) |
| ARQ-02 | `create_floor` | 5 | 10 | M | TX DRY BATCH CHG RES | parcial (create_surface_based_element) |
| ARQ-03 | `create_ceiling` | 5 | 3 | P | TX DRY BATCH CHG RES | parcial (create_surface_based_element) |
| ARQ-06 | `place_family_instance` | 5 | 11 | M | TX DRY BATCH CHG RES | parcial (create_point_based_element) |
| ARQ-07 | `place_door_window` | 5 | 4 | M | TX DRY BATCH CHG RES | parcial (create_point_based_element) |
| ARQ-08 | `create_rooms` | 5 | 7 | P | TX DRY BATCH CHG RES | parcial (create_room) |
| ARQ-12 | `setup_project_levels` | 5 | 2 | M | TX DRY BATCH CHG RES APR KNOW | não |
| ARQ-05 | `create_room_perimeter_profile` | 4 | 2 | M | TX DRY BATCH CHG RES KNOW | não |
| ARQ-09 | `create_room_separation_lines` | 4 | 3 | P | TX DRY BATCH CHG RES | não |
| ARQ-10 | `create_area` | 4 | 1 | M | TX DRY BATCH CHG RES | não |
| ARQ-11 | `create_or_update_levels` | 4 | 7 | P | TX DRY BATCH CHG RES | parcial (create_level) |
| ARQ-26 | `create_countertop` | 4 | 1 | M | TX DRY BATCH CHG RES KNOW | não |

### Edição e parâmetros (16)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| EDT-01 | `transform_elements` | 5 | 7 | P | TX DRY BATCH CHG RES | parcial (operate_element não transforma; modify_element vazi |
| EDT-06 | `renumber_elements` | 5 | 1 | P | TX DRY BATCH CHG RES | não |
| EDT-07 | `rename_elements` | 5 | 5 | P | TX DRY BATCH CHG RES KNOW | não |
| PAR-01 | `get_element_parameters` | 5 | 7 | P | RES PAGE | parcial (ai_element_filter devolve só alguns campos) |
| PAR-03 | `set_parameters` | 5 | 8 | M | TX DRY BATCH CHG RES APR | parcial (modify_element — `.ts` vazio) |
| PAR-04 | `bulk_update_parameters` | 5 | 4 | M | TX DRY BATCH CHG RES APR PAGE | não |
| PAR-07 | `import_parameters_table` | 5 | 3 | M | FILE TX DRY BATCH CHG RES APR LONG | não |
| PAR-11 | `bind_shared_parameter` | 5 | 5 | M | TX DRY CHG RES FILE KNOW | não |
| EDT-03 | `modify_element` | 4 | 1 | P | TX DRY BATCH CHG RES | parcial (modify_element — `.ts` vazio; preencher este stub) |
| EDT-10 | `set_wall_base_top` | 4 | 1 | P | TX DRY BATCH CHG RES | parcial (sem ferramenta; só via send_code) |
| PAR-02 | `list_category_parameters` | 4 | 2 | P | RES PAGE | não |
| PAR-05 | `copy_parameters` | 4 | 2 | P | TX DRY BATCH CHG RES | não |
| PAR-06 | `export_parameters_table` | 4 | 1 | P | FILE RES PAGE LONG | parcial (export_room_data, só ambientes) |
| PAR-09 | `list_parameter_bindings` | 4 | 4 | P | RES PAGE | não |
| PAR-15 | `set_project_info` | 4 | 1 | P | TX DRY CHG RES | parcial (store_project_data grava só no nosso BD) |
| PAR-16 | `set_element_phase` | 4 | 2 | P | TX DRY BATCH CHG RES | não |

### Vistas, folhas e tabelas (36)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| FOL-01 | `list_sheets` | 5 | 3 | P | PAGE RES | não |
| FOL-03 | `create_sheets` | 5 | 7 | P | TX DRY BATCH CHG RES | não |
| FOL-04 | `place_on_sheet` | 5 | 6 | P | TX DRY BATCH CHG RES | não |
| FOL-06 | `align_viewports` | 5 | 2 | M | TX DRY BATCH CHG RES | não |
| FOL-08 | `duplicate_sheet` | 5 | 3 | M | TX DRY BATCH CHG RES LONG | não |
| FOL-09 | `renumber_sheets` | 5 | 2 | M | TX DRY BATCH CHG RES | não |
| FOL-11 | `set_titleblock_parameters` | 5 | 2 | P | TX DRY BATCH CHG RES KNOW | não |
| FOL-14 | `create_revision` | 5 | 4 | P | TX DRY BATCH CHG RES | não |
| FOL-15 | `assign_revision_to_sheets` | 5 | 1 | P | TX DRY BATCH CHG RES | não |
| TAB-01 | `list_schedules` | 5 | 1 | P | PAGE RES | não |
| TAB-02 | `get_schedule_data` | 5 | 5 | P | PAGE RES FILE | não |
| TAB-03 | `create_schedule` | 5 | 8 | M | TX DRY BATCH CHG RES KNOW | parcial (get_material_quantities só lê) |
| TAB-04 | `get_schedulable_fields` | 5 | 2 | P | RES | não |
| TAB-07 | `set_schedule_filters_sorting` | 5 | 5 | M | TX DRY CHG RES | não |
| VIS-02 | `create_view` | 5 | 8 | M | TX DRY BATCH CHG RES | não |
| VIS-06 | `create_room_views` | 5 | 2 | M | TX DRY BATCH CHG RES LONG | não |
| VIS-08 | `duplicate_view` | 5 | 3 | P | TX DRY BATCH CHG RES | não |
| VIS-09 | `set_view_properties` | 5 | 4 | P | TX DRY BATCH CHG RES | não |
| VIS-13 | `list_view_templates` | 5 | 3 | P | PAGE RES | não |
| VIS-14 | `apply_view_template` | 5 | 5 | P | TX DRY BATCH CHG RES | não |
| VIS-23 | `capture_view_image` | 5 | 3 | M | IMG FILE RES | não |
| FOL-02 | `get_sheet_layout` | 4 | 2 | P | PAGE RES | não |
| FOL-10 | `get_titleblock_parameters` | 4 | 1 | P | PAGE RES | não |
| FOL-12 | `list_titleblocks` | 4 | 2 | P | PAGE RES | não |
| FOL-13 | `list_revisions` | 4 | 2 | P | PAGE | não |
| TAB-05 | `get_schedule_definition` | 4 | 1 | P | RES | não |
| TAB-06 | `modify_schedule_fields` | 4 | 3 | M | TX DRY BATCH CHG RES | não |
| VIS-03 | `create_section_view` | 4 | 3 | M | TX DRY BATCH CHG RES | não |
| VIS-04 | `create_elevation_view` | 4 | 1 | M | TX DRY BATCH CHG RES | não |
| VIS-05 | `create_callout_view` | 4 | 2 | P | TX DRY BATCH CHG RES | não |
| VIS-10 | `set_view_crop` | 4 | 3 | P | TX DRY BATCH CHG RES | não |
| VIS-11 | `set_view_range` | 4 | 2 | P | TX DRY BATCH CHG RES | não |
| VIS-15 | `manage_view_templates` | 4 | 4 | M | TX DRY BATCH CHG RES APR | não |
| VIS-17 | `apply_view_filter` | 4 | 1 | P | TX DRY BATCH CHG RES | não |
| VIS-18 | `get_view_visibility` | 4 | 1 | P | PAGE RES | não |
| VIS-19 | `set_category_visibility` | 4 | 1 | P | TX DRY BATCH CHG RES | não |

### Famílias, materiais e anotação (21)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| ANO-01 | `tag_elements` | 5 | 6 | P | TX DRY BATCH CHG RES | parcial (tag_all_rooms, tag_all_walls) |
| ANO-02 | `tag_all_in_view` | 5 | 0 | P | TX DRY BATCH CHG RES | parcial (tag_all_rooms, tag_all_walls) |
| FAM-01 | `change_element_type` | 5 | 0 | P | TX DRY BATCH CHG RES | não (modify_element vazio) |
| FAM-02 | `replace_type_instances` | 5 | 0 | P | TX DRY BATCH CHG RES APR | não |
| FAM-03 | `duplicate_type` | 5 | 0 | P | TX DRY BATCH CHG RES | não |
| FAM-05 | `get_compound_structure` | 5 | 1 | P | RES | não |
| FAM-06 | `set_compound_structure` | 5 | 0 | M | TX DRY BATCH CHG RES | não |
| FAM-07 | `load_family` | 5 | 7 | P | TX CHG FILE LONG | não |
| MAT-01 | `create_view_filter` | 5 | 2 | M | TX DRY CHG RES | não |
| MAT-02 | `set_view_filter` | 5 | 0 | P | TX DRY BATCH CHG RES | não |
| MAT-13 | `assign_material` | 5 | 0 | M | TX DRY BATCH CHG RES | não |
| ANO-03 | `create_text_note` | 4 | 6 | P | TX DRY BATCH CHG RES | não |
| ANO-04 | `create_dimensions (estender)` | 4 | 7 | M | TX DRY BATCH CHG RES | parcial (create_dimensions) |
| FAM-04 | `rename_types` | 4 | 3 | P | TX DRY BATCH CHG RES | não |
| FAM-09 | `get_available_family_types (estender)` | 4 | 0 | P | RES PAGE | parcial (get_available_family_types — só FamilySymbol) |
| FAM-10 | `get_family_instances` | 4 | 1 | P | RES PAGE | parcial (ai_element_filter) |
| MAT-04 | `set_graphic_overrides` | 4 | 6 | P | TX DRY BATCH CHG RES | parcial (color_elements, operate_element SetColor/SetTranspa |
| MAT-06 | `color_elements (estender)` | 4 | 0 | M | TX BATCH CHG RES | parcial (color_elements, sem legenda nem colorMap) |
| MAT-07 | `list_materials` | 4 | 3 | P | RES PAGE | parcial (get_material_quantities) |
| MAT-09 | `create_material` | 4 | 0 | P | TX DRY BATCH CHG RES KNOW | não |
| MAT-10 | `set_material_graphics` | 4 | 3 | M | TX DRY BATCH CHG RES | não |

### Infra e meta (13)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| INF-01 | `get_session_context` | 5 | 6 | P | RES | parcial (`list_open_documents`, `say_hello`) |
| INF-02 | `send_code_to_revit (endurecida)` | 5 | 5 | M | TX DRY CHG PAGE APR LONG | sim (`send_code_to_revit`, já com `transactionMode auto/none |
| INF-03 | `(sem tool) correções do compilador Roslyn` | 5 | 1 | P |  | não (`ExecuteCodeEventHandler.cs` tem código antigo) |
| INF-04 | `revit_api_lookup` | 5 | 1 | M | PAGE FILE | não |
| INF-05 | `get_office_knowledge` | 5 | 1 | P | KNOW FILE | não |
| INF-06 | `MCP prompts workflow_* (ex.: workflow_family, workflow_conversion)` | 4 | 1 | P | KNOW | não |
| INF-07 | `get_job_result (+ flag runAsync em toda tool)` | 4 | 1 | M | LONG PAGE | não |
| INF-09 | `list_commands` | 4 | 1 | P | APR UI | parcial (`command.json` tem `scope`; sem tool) |
| INF-14 | `open_document` | 4 | 2 | M | FILE LONG UI | parcial (`allowAutoActivate` experimental) |
| INF-15 | `list_recent_models` | 4 | 1 | P | FILE | não |
| INF-17 | `save_document` | 4 | 3 | P | FILE APR | não |
| INF-23 | `(sem tool) suporte Revit 2027 / .NET 10` | 4 | 1 | P |  | não (até R26) |
| OUT-01 | `(sem tool) pré-processador de warnings na transação` | 4 | 1 | P | TX | parcial (`DeleteWarningSuperUtils` só em CreateRoom/TagRooms |

## Fase 2 (76)

### Consulta, seleção e histórico (10)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| UND-08 | `get_change_history` | 4 | 1 | G | FILE PAGE | parcial (audit JSONL do broker) |
| CON-04 | `list_categories` | 3 | 2 | P | RES | parcial (analyze_model_statistics) |
| CON-07 | `get_view_info` | 3 | 4 | P | RES | parcial (get_current_view_info) |
| CON-08 | `list_view_filters` | 3 | 1 | P | RES PAGE | não |
| CON-18 | `list_groups` | 3 | 3 | P | PAGE | não |
| CON-19 | `get_element_geometry` | 3 | 4 | P | PAGE | não |
| CON-20 | `measure_between_elements` | 3 | 1 | P |  | não |
| CON-25 | `take_model_snapshot` | 3 | 1 | M | FILE LONG PAGE CHG | parcial (analyze_model_statistics) |
| SEL-02 | `get_selected_elements` | 3 | 7 | P | UI PAGE | sim (get_selected_elements) |
| UND-06 | `revert_plan` | 3 | 1 | M | APR CHG | não |

### Exportação e QA (11)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| QA-14 | `survey_change_impact` | 5 | 1 | G | PAGE LONG RES | não |
| QA-15 | `run_qa_checks` | 4 | 10 | G | PAGE FILE KNOW LONG | não |
| EXP-05 | `export_nwc` | 3 | 2 | P | FILE LONG | não |
| EXP-10 | `list_export_settings` | 3 | 2 | P | PAGE | não |
| EXP-11 | `create_view_sheet_set` | 3 | 1 | P | TX DRY CHG | não |
| EXP-12 | `export_families` | 3 | 1 | P | FILE LONG RES | não |
| EXP-17 | `translate_element_ids` | 3 | 1 | P |  | não |
| EXP-18 | `bcf_exchange` | 3 | 2 | M | FILE TX | não |
| QA-10 | `clash_review` | 3 | 3 | M | TX DRY CHG BATCH | não |
| QA-21 | `audit_roofs` | 3 | 1 | M | PAGE RES | não |
| QA-24 | `check_lighting_ventilation` | 3 | 1 | M | PAGE RES KNOW | não |

### Criação (arquitetura, estrutura, MEP) (13)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| ARQ-04 | `create_room_envelope` | 5 | 1 | G | TX DRY BATCH CHG RES APR KNOW | não |
| ARQ-24 | `create_millwork_module` | 4 | 4 | G | TX DRY BATCH CHG RES KNOW | não |
| ARQ-13 | `set_true_north` | 3 | 1 | P | TX DRY CHG | não |
| ARQ-14 | `create_grids` | 3 | 7 | P | TX DRY BATCH CHG RES | parcial (create_grid) |
| ARQ-15 | `create_opening` | 3 | 2 | M | TX DRY BATCH CHG RES | não |
| ARQ-16 | `create_roof` | 3 | 2 | M | TX DRY BATCH CHG RES | não |
| ARQ-17 | `create_reference_plane` | 3 | 3 | P | TX DRY BATCH CHG RES | não |
| ARQ-19 | `create_direct_shape` | 3 | 1 | M | TX DRY BATCH CHG RES | não |
| ARQ-20 | `place_group_instance` | 3 | 1 | P | TX DRY BATCH CHG RES | não |
| ARQ-21 | `create_similar` | 3 | 1 | M | TX DRY BATCH CHG RES | não |
| ARQ-25 | `configure_millwork_module` | 3 | 2 | G | TX DRY BATCH CHG RES KNOW | não |
| EST-01 | `create_structural_column` | 3 | 4 | P | TX DRY BATCH CHG RES | parcial (create_point_based_element) |
| MEP-11 | `find_mep_crossings` | 3 | 1 | G | PAGE RES LONG | não |

### Edição e parâmetros (13)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| EDT-02 | `array_elements` | 3 | 3 | P | TX DRY BATCH CHG | não |
| EDT-05 | `delete_elements` | 3 | 6 | P | TX DRY BATCH CHG APR | sim (delete_element; sem dryRun/prévia) |
| EDT-08 | `manage_groups` | 3 | 5 | M | TX DRY BATCH CHG APR | não |
| EDT-11 | `align_distribute_elements` | 3 | 1 | P | TX DRY BATCH CHG | não |
| EDT-12 | `join_cut_geometry` | 3 | 1 | P | TX DRY BATCH CHG | não |
| EDT-13 | `edit_element_curve` | 3 | 2 | M | TX DRY BATCH CHG | não |
| EDT-14 | `set_project_base_point` | 3 | 1 | P | TX DRY CHG APR | não |
| EDT-15 | `sync_link_coordinates` | 3 | 1 | P | TX CHG APR | não |
| EDT-16 | `assign_workset` | 3 | 1 | P | TX DRY BATCH CHG RES | não |
| PAR-10 | `read_shared_parameter_file` | 3 | 3 | P | FILE RES PAGE | não |
| PAR-12 | `create_shared_parameter_definition` | 3 | 2 | P | FILE KNOW | não |
| PAR-13 | `remove_parameter_binding` | 3 | 2 | P | TX DRY CHG RES APR | não |
| PAR-14 | `manage_family_parameters` | 3 | 2 | M | TX DRY CHG RES UI | não |

### Vistas, folhas e tabelas (11)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| VIS-27 | `workflow_room_documentation` | 4 | 2 | G | TX DRY BATCH CHG LONG KNOW | não |
| FOL-05 | `arrange_viewports` | 3 | 1 | M | TX DRY CHG RES | não |
| FOL-07 | `modify_viewport` | 3 | 1 | P | TX DRY BATCH CHG RES | não |
| FOL-16 | `create_revision_cloud` | 3 | 1 | P | TX DRY CHG RES | não |
| TAB-09 | `find_schedule_elements` | 3 | 1 | P | PAGE RES | não |
| TAB-10 | `material_takeoff_report` | 3 | 1 | M | PAGE FILE RES | parcial (get_material_quantities) |
| VIS-07 | `create_perspective_view` | 3 | 1 | M | TX DRY CHG RES | não |
| VIS-12 | `set_section_box` | 3 | 2 | P | TX DRY CHG RES | parcial (operate_element SelectionBox) |
| VIS-21 | `rename_views` | 3 | 1 | P | TX DRY BATCH CHG RES | não |
| VIS-22 | `purge_unplaced_views` | 3 | 1 | P | TX DRY CHG APR | não |
| VIS-24 | `open_view` | 3 | 1 | P | UI RES | não |

### Famílias, materiais e anotação (13)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| ANO-05 | `create_detail_lines` | 3 | 0 | P | TX DRY BATCH CHG RES | parcial (create_line_based_element) |
| ANO-06 | `create_filled_region` | 3 | 5 | P | TX DRY BATCH CHG RES | não |
| ANO-07 | `list_keynotes` | 3 | 1 | P | PAGE | não |
| ANO-08 | `set_keynote` | 3 | 1 | P | TX DRY BATCH CHG | não |
| ANO-09 | `list_tags_in_view` | 3 | 1 | P | RES PAGE | não |
| ANO-10 | `delete_empty_tags` | 3 | 2 | P | TX DRY CHG APR | não |
| FAM-11 | `unload_family` | 3 | 1 | P | TX DRY CHG APR | parcial (delete_element) |
| FAM-12 | `export_family` | 3 | 1 | P | FILE | não |
| FAM-14 | `inspect_family` | 3 | 1 | M | RES LONG | não |
| FAM-15 | `edit_family` | 3 | 4 | M | TX DRY CHG RES UI | não |
| MAT-08 | `get_material` | 3 | 2 | P | RES | não |
| MAT-11 | `set_material_identity` | 3 | 2 | P | TX DRY BATCH CHG RES | não |
| MAT-14 | `paint_faces` | 3 | 1 | M | TX DRY CHG RES | não |

### Infra e meta (5)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| INF-24 | `(sem tool) instalador Windows` | 4 | 1 | G |  | não (`scripts/release.ps1` + guia manual) |
| INF-10 | `search_modules` | 3 | 2 | M | PAGE FILE KNOW | parcial (`search_modules` — arquivo VAZIO, conta como não) |
| INF-11 | `use_module` | 3 | 3 | M | TX DRY CHG APR BATCH FILE | parcial (`use_module` — VAZIO, conta como não) |
| INF-20 | `manage_worksets` | 3 | 1 | P | TX DRY CHG APR RES | não |
| INF-21 | `set_element_workset` | 3 | 1 | P | TX DRY BATCH CHG RES | não |

## Fase 3 (28)

### Consulta, seleção e histórico (5)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| CON-11 | `list_design_options` | 2 | 1 | P |  | não |
| CON-22 | `list_revit_links` | 2 | 2 | P |  | sim (list_revit_links) |
| CON-26 | `query_model_snapshot` | 2 | 1 | M | FILE PAGE | parcial (query_stored_data) |
| CON-27 | `list_reference_planes` | 2 | 1 | P |  | não |
| UND-10 | `compare_model_snapshots` | 2 | 1 | M | FILE PAGE | não |

### Exportação e QA (4)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| EXP-14 | `import_image` | 2 | 1 | P | TX CHG FILE | não |
| EXP-19 | `export_room_data` | 2 | 2 | P | PAGE FILE | sim (export_room_data) |
| QA-25 | `compute_element_geometry` | 2 | 2 | P | RES | parcial (get_material_quantities) |
| QA-26 | `analyze_model_statistics` | 2 | 3 | P | PAGE | sim (analyze_model_statistics) |

### Criação (arquitetura, estrutura, MEP) (8)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| ARQ-18 | `create_model_curves` | 2 | 6 | P | TX DRY BATCH CHG RES | não |
| ARQ-22 | `create_railing` | 2 | 1 | M | TX DRY BATCH CHG RES | não |
| EST-02 | `create_beams` | 2 | 4 | P | TX DRY BATCH CHG RES | parcial (create_structural_framing_system) |
| EST-04 | `create_foundations` | 2 | 3 | P | TX DRY BATCH CHG RES | não |
| MEP-01 | `create_mep_curves` | 2 | 13 | M | TX DRY BATCH CHG RES | não |
| MEP-03 | `list_spaces` | 2 | 1 | P | PAGE RES | não |
| MEP-04 | `get_mep_systems` | 2 | 6 | M | PAGE RES LONG | não |
| MEP-12 | `place_penetration_openings` | 2 | 1 | G | TX DRY BATCH CHG RES APR | não |

### Edição e parâmetros (2)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| EDT-04 | `set_elements_pinned` | 2 | 2 | P | TX DRY BATCH CHG | não |
| PAR-08 | `parameter_snapshot` | 2 | 1 | M | FILE PAGE RES | não |

### Vistas, folhas e tabelas (1)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| VIS-20 | `operate_element (extensão viewId)` | 2 | 1 | P | TX CHG | parcial (operate_element Hide/Unhide, só vista ativa) |

### Famílias, materiais e anotação (4)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| MAT-15 | `create_finish_material` | 3 | 0 | G | TX CHG FILE KNOW | não |
| ANO-12 | `create_spot_elevation` | 2 | 1 | M | TX CHG RES | não |
| MAT-12 | `set_material_physical_assets` | 2 | 0 | M | TX DRY CHG RES | não |
| MAT-16 | `manage_line_styles` | 2 | 2 | P | TX DRY CHG RES | não |

### Infra e meta (4)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| INF-08 | `cancel_job` | 2 | 1 | P | LONG | não |
| INF-13 | `suggest_modules` | 2 | 1 | G | FILE | não |
| INF-16 | `create_document` | 2 | 2 | P | FILE KNOW | não |
| INF-22 | `invoke_command (dev)` | 2 | 1 | P | APR | não |

## Backlog (33)

### Consulta, seleção e histórico (3)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| CON-28 | `get_element_references` | 2 | 1 | P | PAGE | não |
| CON-29 | `geometry_probe` | 2 | 1 | P |  | não |
| SEL-07 | `show_task_dialog` | 1 | 2 | M | UI | parcial (say_hello) |

### Exportação e QA (7)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| EXP-15 | `import_mesh_as_family` | 2 | 1 | G | TX CHG FILE LONG | não |
| EXP-16 | `import_table_as_drafting` | 2 | 1 | M | TX CHG FILE | não |
| EXP-20 | `verify_ifc_export` | 2 | 1 | G | FILE LONG PAGE | não |
| EXP-13 | `export_model_misc` | 1 | 3 | P | FILE LONG TX | não |
| QA-27 | `get_material_quantities` | 1 | 2 | P | RES | sim (get_material_quantities) |
| QA-28 | `find_mep_disconnects` | 1 | 1 | P | PAGE | não |
| QA-29 | `audit_structural_connections` | 1 | 1 | P | PAGE | não |

### Criação (arquitetura, estrutura, MEP) (11)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| ARQ-23 | `create_stairs` | 2 | 1 | G | TX DRY CHG RES | não |
| EST-03 | `create_structural_framing_system` | 1 | 1 | P | TX BATCH CHG RES | sim (create_structural_framing_system) |
| EST-05 | `rebar_tools` | 1 | 3 | M | TX DRY CHG RES PAGE | não |
| EST-06 | `structural_loads` | 1 | 2 | P | TX CHG PAGE | não |
| MEP-02 | `create_spaces` | 1 | 2 | P | TX DRY BATCH CHG RES | não |
| MEP-05 | `get_mep_connectors` | 1 | 2 | P | PAGE | não |
| MEP-06 | `connect_mep` | 1 | 4 | M | TX DRY BATCH CHG | não |
| MEP-07 | `assign_mep_system` | 1 | 3 | M | TX DRY CHG RES | não |
| MEP-08 | `route_mep` | 1 | 1 | G | TX DRY BATCH CHG RES LONG | não |
| MEP-09 | `create_electrical_circuits` | 1 | 1 | M | TX DRY BATCH CHG RES | não |
| MEP-10 | `get_panel_schedule` | 1 | 1 | P | PAGE | não |

### Edição e parâmetros (1)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| EDT-09 | `manage_assembly` | 2 | 1 | P | TX DRY CHG | não |

### Vistas, folhas e tabelas (3)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| TAB-11 | `style_schedule` | 2 | 1 | P | TX CHG RES KNOW | não |
| VIS-25 | `create_assembly_views` | 2 | 1 | M | TX CHG RES | não |
| VIS-26 | `set_work_plane` | 2 | 1 | P | TX RES | não |

### Famílias, materiais e anotação (5)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| MAT-05 | `clear_graphic_overrides` | 4 | 2 | ? |  | parcial (operate_element ResetIsolate não limpa) |
| ANO-13 | `create_locked_alignment` | 2 | 1 | M | TX CHG | não |
| FAM-16 | `create_family_from_template` | 2 | 0 | G | FILE LONG KNOW | não |
| FAM-17 | `create_void_cut` | 2 | 1 | M | TX CHG KNOW | não |
| FAM-18 | `load_rvt_as_group` | 2 | 1 | M | TX CHG FILE | não |

### Infra e meta (3)

| ID | Ferramenta | Int. | Fontes | Esf. | Infra | Já temos? |
|---|---|---|---|---|---|---|
| INF-19 | `sync_with_central` | 2 | 1 | M | APR LONG | não |
| INF-18 | `close_document` | 1 | 1 | P | APR | não |
| OUT-02 | `inspect_curve` | 1 | 1 | P | RES | não |
