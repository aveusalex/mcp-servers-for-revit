# Cobertura do MAPA pelo nosso MCP

Gerado de research/capabilities/*.map.csv × tools.manifest.json. MAPA é proprietário: nada foi copiado; equivalentes escritos do zero.

Resumo: 216 ferramentas do MAPA → **156 com equivalente implementado**, 26 pendentes, 34 descartadas.

| Ferramenta MAPA | Capacidade | Nossa ferramenta | Situação |
|---|---|---|---|
| (inferida) aplicar acabamento em piso/parede/forro | MAT-13 | assign_material | **implementada** |
| (inferida) auditar categorização | QA-12 | check_required_parameters | **implementada** |
| (inferida) categorizar tipos | PAR-04 | bulk_update_parameters | **implementada** |
| (inferida) checar exportador Twinmotion | — | — | descartada (proprietária/específica do escritório deles) |
| (inferida) cotas de terreno/calçada | ARQ-12 | setup_project_levels | **implementada** |
| (inferida) criar acabamento MDF por imagem/link | MAT-15 | create_finish_material | pendente (fase 3) |
| (inferida) criar revestimento | MAT-15 | create_finish_material | pendente (fase 3) |
| (inferida) criar tinta/textura/monolítico | MAT-15 | create_finish_material | pendente (fase 3) |
| (inferida) criar vista 3D | VIS-02 | create_view | **implementada** |
| (inferida) documentar paisagismo | — | — | descartada (proprietária/específica do escritório deles) |
| (inferida) enviar para Twinmotion | — | — | descartada (proprietária/específica do escritório deles) |
| (inferida) espessura por peça do módulo | — | — | descartada (proprietária/específica do escritório deles) |
| (inferida) laje do ambiente/pavimento | ARQ-02 | create_floor | **implementada** |
| (inferida) norte verdadeiro | ARQ-13 | set_true_north | **implementada** |
| (inferida) opções de acabamento do módulo | — | — | descartada (proprietária/específica do escritório deles) |
| (inferida) plano de integração | — | — | descartada (proprietária/específica do escritório deles) |
| (inferida) revisões | FOL-14 | create_revision | **implementada** |
| (inferida) taxonomia de disciplinas | INF-05 | get_office_knowledge | **implementada** |
| add_filter_to_view | VIS-17 | apply_view_filter | **implementada** |
| add_filter_to_view_templates | VIS-17 | apply_view_filter | **implementada** |
| add_to_assembly | EDT-09 | manage_assembly | pendente (fase B) |
| add_to_group | EDT-08 | manage_groups | **implementada** |
| adicionar_divisorias_internas | ARQ-25 | configure_millwork_module | **implementada** |
| adicionar_portas | ARQ-25 | configure_millwork_module | **implementada** |
| agrupar_modulo | — | — | descartada (proprietária/específica do escritório deles) |
| ajustar_modulo | — | — | descartada (proprietária/específica do escritório deles) |
| ajustar_vegetacao | — | — | descartada (proprietária/específica do escritório deles) |
| align_elements | EDT-11 | align_distribute_elements | **implementada** |
| align_with_lock | ANO-13 | create_locked_alignment | pendente (fase B) |
| apply_view_template | VIS-14 | apply_view_template | **implementada** |
| array_linear | EDT-02 | array_elements | **implementada** |
| array_radial | EDT-02 | array_elements | **implementada** |
| assign_scope_box_to_elements | VIS-10 | set_view_crop | **implementada** |
| bulk_tag_in_view | ANO-02 | tag_all_in_view | **implementada** |
| capture_revit_view | VIS-23 | capture_view_image | **implementada** |
| change_corte_type | FAM-01 | change_element_type | **implementada** |
| change_element_type | FAM-01 | change_element_type | **implementada** |
| change_identificador_type | FAM-01 | change_element_type | **implementada** |
| change_level_elevation | ARQ-11 | create_or_update_levels | **implementada** |
| change_section_type_color | — | — | descartada (proprietária/específica do escritório deles) |
| change_tags_type | FAM-01 | change_element_type | **implementada** |
| change_viewport_type | FOL-07 | modify_viewport | **implementada** |
| colorize_schedule_header | TAB-11 | style_schedule | pendente (fase B) |
| colorize_schedule_stripe | TAB-11 | style_schedule | pendente (fase B) |
| consultar_familia | CON-24 | list_families | **implementada** |
| copy_elements | EDT-01 | transform_elements | **implementada** |
| create_assembly | EDT-09 | manage_assembly | pendente (fase B) |
| create_assembly_schedule | VIS-25 | create_assembly_views | pendente (fase B) |
| create_assembly_views | VIS-25 | create_assembly_views | pendente (fase B) |
| create_ceiling_by_walls | ARQ-03 | create_ceiling | **implementada** |
| create_dimension | ANO-04 | create_dimensions | **implementada** |
| create_drafting_view | VIS-02 | create_view | **implementada** |
| create_filled_region | ANO-06 | create_filled_region | **implementada** |
| create_floor | ARQ-02 | create_floor | **implementada** |
| create_floor_by_walls | ARQ-02 | create_floor | **implementada** |
| create_group | EDT-08 | manage_groups | **implementada** |
| create_level | ARQ-11 | create_or_update_levels | **implementada** |
| create_line_style | MAT-16 | manage_line_styles | pendente (fase 3) |
| create_material | MAT-09 | create_material | **implementada** |
| create_material_takeoff | TAB-03 | create_schedule | **implementada** |
| create_reference_plane | ARQ-17 | create_reference_plane | **implementada** |
| create_room | ARQ-08 | create_rooms | **implementada** |
| create_room_separation | ARQ-09 | create_room_separation_lines | **implementada** |
| create_room_views | VIS-06 | create_room_views | **implementada** |
| create_schedule | TAB-03 | create_schedule | **implementada** |
| create_sheet | FOL-03 | create_sheets | **implementada** |
| create_sheets_batch | FOL-03 | create_sheets | **implementada** |
| create_similar | ARQ-21 | create_similar | **implementada** |
| create_text_note | ANO-03 | create_text_note | **implementada** |
| create_view_filter | VIS-16 | create_view_filter | **implementada** |
| create_wall | ARQ-01 | create_walls | **implementada** |
| create_wall_by_line | ARQ-01 | create_walls | **implementada** |
| create_walls_from_vertices | ARQ-01 | create_walls | **implementada** |
| create_walls_polygon | ARQ-01 | create_walls | **implementada** |
| create_walls_rectangle | ARQ-01 | create_walls | **implementada** |
| criar_envelope_ambiente | ARQ-04 | create_room_envelope | **implementada** |
| criar_extrusao_travada | FAM-16 | create_family_from_template | pendente (fase B) |
| criar_familia | FAM-16 | create_family_from_template | pendente (fase B) |
| criar_modulo | ARQ-24 | create_millwork_module | **implementada** |
| criar_vazio_de_corte | FAM-17 | create_void_cut | pendente (fase B) |
| criar_vegetacao | — | — | descartada (proprietária/específica do escritório deles) |
| cut_solids | EDT-12 | join_cut_geometry | **implementada** |
| definir_medidas_familia | PAR-14 | manage_family_parameters | **implementada** |
| delete_elements | EDT-05 | delete_elements | **implementada** |
| delete_volume_with_level_fix | — | — | descartada (proprietária/específica do escritório deles) |
| distribute_elements | EDT-11 | align_distribute_elements | **implementada** |
| draw_detail_line | ANO-05 | create_detail_lines | **implementada** |
| draw_detail_shapes | ANO-05 | create_detail_lines | **implementada** |
| duplicate_family_type | FAM-03 | duplicate_type | **implementada** |
| duplicate_group_type | FAM-03 | duplicate_type | **implementada** |
| duplicate_system_family_type | FAM-03 | duplicate_type | **implementada** |
| edit_floor_layer_structure | FAM-06 | set_compound_structure | **implementada** |
| edit_group_member | EDT-08 | manage_groups | **implementada** |
| edit_material_graphics | MAT-10 | set_material_graphics | **implementada** |
| edit_schedule_columns | TAB-06 | modify_schedule_fields | **implementada** |
| edit_schedule_sort_group | TAB-07 | set_schedule_filters_sorting | **implementada** |
| edit_view_template_properties | VIS-15 | manage_view_templates | **implementada** |
| edit_wall_layer_structure | FAM-06 | set_compound_structure | **implementada** |
| enter_group_edit | — | — | descartada (proprietária/específica do escritório deles) |
| exit_group_edit | — | — | descartada (proprietária/específica do escritório deles) |
| find_families | CON-24 | list_families | **implementada** |
| find_groups | CON-18 | list_groups | **implementada** |
| furar_com_a_peca | EDT-12 | join_cut_geometry | **implementada** |
| gerar_plano_corte | — | — | descartada (proprietária/específica do escritório deles) |
| get_active_view | CON-07 | get_view_info | **implementada** |
| get_assemblies | CON-18 | list_groups | **implementada** |
| get_connection_info | INF-01 | get_session_context | **implementada** |
| get_element_location | CON-02 | get_element_details | **implementada** |
| get_element_parameters | PAR-01 | get_element_parameters | **implementada** |
| get_elements | CON-03 | find_elements | **implementada** |
| get_elements_by_family | CON-03 | find_elements | **implementada** |
| get_elements_in_view | CON-03 | find_elements | **implementada** |
| get_families | CON-24 | list_families | **implementada** |
| get_filters | CON-08 | list_view_filters | **implementada** |
| get_group_members | CON-18 | list_groups | **implementada** |
| get_legends | CON-06 | list_views | **implementada** |
| get_mapa_help | INF-05 | get_office_knowledge | **implementada** |
| get_model_metadata | CON-01 | get_project_info | **implementada** |
| get_oc_knowledge | INF-05 | get_office_knowledge | **implementada** |
| get_padroes_atuais | INF-05 | get_office_knowledge | **implementada** |
| get_pecas_plano_corte | — | — | descartada (proprietária/específica do escritório deles) |
| get_schedules | CON-06 | list_views | **implementada** |
| get_selected_elements | SEL-02 | get_selected_elements | pendente (fase 2) |
| get_undo_history | UND-07 | undo_history | pendente (fase 1) |
| get_view_family_types | CON-06 | list_views | **implementada** |
| get_view_templates | CON-06 | list_views | **implementada** |
| get_views | CON-06 | list_views | **implementada** |
| get_volume_dimensions | — | — | descartada (proprietária/específica do escritório deles) |
| hide_in_view | SEL-06 | set_view_temporary_visibility | **implementada** |
| import_image | EXP-14 | import_image | pendente (fase 3) |
| import_mesh_as_family | EXP-15 | import_mesh_as_family | pendente (fase B) |
| inserir_vegetacao | ARQ-06 | place_family_instance | **implementada** |
| insert_door | ARQ-07 | place_door_window | **implementada** |
| insert_rodapes_room | ARQ-05 | create_room_perimeter_profile | **implementada** |
| insert_tabicas_room | ARQ-05 | create_room_perimeter_profile | **implementada** |
| insert_window | ARQ-07 | place_door_window | **implementada** |
| inspecionar_modelo_3d | — | — | descartada (proprietária/específica do escritório deles) |
| isolate_in_view | SEL-06 | set_view_temporary_visibility | **implementada** |
| join_geometry | EDT-12 | join_cut_geometry | **implementada** |
| list_attached_detail_groups | CON-18 | list_groups | **implementada** |
| list_group_types | CON-18 | list_groups | **implementada** |
| list_modulo_templates | — | — | descartada (proprietária/específica do escritório deles) |
| load_as_group | FAM-18 | load_rvt_as_group | pendente (fase B) |
| load_family | FAM-07 | load_family | **implementada** |
| lock_floor_boundary_to_walls | — | — | descartada (proprietária/específica do escritório deles) |
| mapear_pasta_modelos_3d | — | — | descartada (proprietária/específica do escritório deles) |
| mark_layer_variable | FAM-06 | set_compound_structure | **implementada** |
| match_type | FAM-01 | change_element_type | **implementada** |
| mirror_elements_by_axis | EDT-01 | transform_elements | **implementada** |
| mirror_elements_by_line | EDT-01 | transform_elements | **implementada** |
| modify_parameter | PAR-03 | set_parameters | **implementada** |
| modify_parameters_batch | PAR-03 | set_parameters | **implementada** |
| modify_selected_elements_parameter | PAR-03 | set_parameters | **implementada** |
| modulo_aereo | ARQ-24 | create_millwork_module | **implementada** |
| modulo_aereo_catalogo | ARQ-24 | create_millwork_module | **implementada** |
| modulo_palhinha | — | — | descartada (proprietária/específica do escritório deles) |
| move_elements | EDT-01 | transform_elements | **implementada** |
| nomear_modulo | — | — | descartada (proprietária/específica do escritório deles) |
| offset_elements | EDT-13 | edit_element_curve | **implementada** |
| open_family | FAM-15 | edit_family | **implementada** |
| open_filter_overrides_dialog | — | — | descartada (proprietária/específica do escritório deles) |
| open_view | VIS-24 | open_view | **implementada** |
| override_category_graphics_in_view | MAT-04 | set_graphic_overrides | **implementada** |
| personalize_template | — | — | descartada (proprietária/específica do escritório deles) |
| pin_elements | EDT-04 | set_elements_pinned | **implementada** |
| place_family_instance | ARQ-06 | place_family_instance | **implementada** |
| place_group_instance | ARQ-20 | place_group_instance | **implementada** |
| place_oc_volume_estudo | ARQ-24 | create_millwork_module | **implementada** |
| place_tag_on_element | ANO-01 | tag_elements | **implementada** |
| place_view_on_sheet | FOL-04 | place_on_sheet | **implementada** |
| preencher_identidade_familia | PAR-14 | manage_family_parameters | **implementada** |
| propagate_group_edit | — | — | descartada (proprietária/específica do escritório deles) |
| redo | UND-07 | undo_history | pendente (fase 1) |
| regroup_from_selection | EDT-08 | manage_groups | **implementada** |
| reload_mapa_padroes | INF-05 | get_office_knowledge | **implementada** |
| remove_filter_from_view_template | VIS-17 | apply_view_filter | **implementada** |
| remove_from_assembly | EDT-09 | manage_assembly | pendente (fase B) |
| remove_from_group | EDT-08 | manage_groups | **implementada** |
| rename_group_type | FAM-04 | rename_types | **implementada** |
| replace_office_logo | — | — | descartada (proprietária/específica do escritório deles) |
| reset_view_hide_isolate | SEL-06 | set_view_temporary_visibility | **implementada** |
| resize_modulo_volume | — | — | descartada (proprietária/específica do escritório deles) |
| resize_porta_grupo | — | — | descartada (proprietária/específica do escritório deles) |
| rotate_elements | EDT-01 | transform_elements | **implementada** |
| roteiro_conversao | INF-06 | MCP | pendente (fase 1) |
| roteiro_familia | INF-06 | MCP | pendente (fase 1) |
| select_elements | SEL-01 | select_elements | **implementada** |
| select_elements_by_room | CON-16 | find_elements_in_volume | **implementada** |
| select_elements_by_view | SEL-01 | select_elements | **implementada** |
| send_code | INF-02 | send_code_to_revit | **implementada** |
| send_revit_keys | — | — | descartada (proprietária/específica do escritório deles) |
| set_chapa_abertura | — | — | descartada (proprietária/específica do escritório deles) |
| set_chapa_chanfro | — | — | descartada (proprietária/específica do escritório deles) |
| set_graphic_display_options | VIS-09 | set_view_properties | **implementada** |
| set_guia_lateral_offset | — | — | descartada (proprietária/específica do escritório deles) |
| set_volume_dimensions | — | — | descartada (proprietária/específica do escritório deles) |
| set_wall_base_top | EDT-10 | set_wall_base_top | **implementada** |
| set_work_plane | VIS-26 | set_work_plane | pendente (fase B) |
| setup_inicial_projeto | ARQ-12 | setup_project_levels | **implementada** |
| show_hide_attached_detail_group | SEL-06 | set_view_temporary_visibility | **implementada** |
| split_element | EDT-13 | edit_element_curve | **implementada** |
| substitute_filled_region_type | FAM-02 | replace_type_instances | **implementada** |
| tag_all_rooms | ANO-02 | tag_all_in_view | **implementada** |
| tag_multi_category_in_view | ANO-02 | tag_all_in_view | **implementada** |
| tampo_pedra | ARQ-26 | create_countertop | **implementada** |
| trim_extend_multiple_elements | EDT-13 | edit_element_curve | **implementada** |
| trim_extend_single_element | EDT-13 | edit_element_curve | **implementada** |
| trim_extend_to_corner | EDT-13 | edit_element_curve | **implementada** |
| trocar_acabamento | MAT-13 | assign_material | **implementada** |
| undo | UND-07 | undo_history | pendente (fase 1) |
| undo_to | UND-07 | undo_history | pendente (fase 1) |
| ungroup | EDT-08 | manage_groups | **implementada** |
| unhide_in_view | SEL-06 | set_view_temporary_visibility | **implementada** |
| unpin_elements | EDT-04 | set_elements_pinned | **implementada** |
| update_filter_overrides | VIS-17 | apply_view_filter | **implementada** |
| wall_attach_top_base | EDT-10 | set_wall_base_top | **implementada** |
