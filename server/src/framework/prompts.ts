import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";

/**
 * INF-06: MCP prompts = guided workflows ("roteiros") the user picks in the client (e.g. the "/" menu of Claude
 * Desktop). Each one expands into a user message that tells the model which of our tools to call, in which order,
 * and with which safety conventions (preview first, approval for medium risk, read back, how to undo).
 *
 * Convention checked by test/prompts.test.js: every tool name is written between backticks, and nothing else
 * that looks like a snake_case tool name is backticked.
 */

export interface WorkflowPromptArg {
  name: string;
  description: string;
  required?: boolean;
}

export interface WorkflowPrompt {
  name: string;
  title: string;
  description: string;
  args: WorkflowPromptArg[];
  build(args: Record<string, string | undefined>): string;
}

const CONVENCOES = `
Convenções (valem para todo o roteiro):
- Comece com \`get_session_context\` para saber qual projeto está aberto e qual é o documento-alvo.
- Medidas sempre em mm, áreas em m², volumes em m³ e ângulos em graus.
- Antes de qualquer escrita, rode a ferramenta com dryRun:true (prévia: o Revit executa e desfaz, nada muda) e
  mostre ao usuário o que vai acontecer. Algumas ferramentas em massa já fazem só a prévia por padrão: para aplicar,
  passe dryRun:false.
- Ferramentas de risco médio ou alto respondem APPROVAL_REQUIRED: monte o plano com \`propose_change_plan\` (o hint
  da resposta traz o passo exato), espere com \`wait_for_plan_decision\` e só então rode \`execute_plan\`. Nunca
  contorne a aprovação.
- Leia de volta o que foi feito (ids, áreas, nomes) e confira com o que foi pedido.
- Para desfazer: \`get_undo_history\` mostra o último passo; \`undo_last\` desfaz um passo do MCP (pede aprovação).
- Padrões do escritório (nomes, folhas, parâmetros): \`get_office_knowledge\`.
- Pergunte ao usuário quando faltar uma escolha (tipo, material, escala); não invente nomes de tipos: liste-os.`;

function alvo(value: string | undefined, fallback: string): string {
  const v = (value ?? "").trim();
  return v.length > 0 ? v : fallback;
}

export const workflowPrompts: WorkflowPrompt[] = [
  {
    name: "roteiro_ambiente",
    title: "Roteiro: acabamentos e fechamento de um ambiente",
    description:
      "Levanta um ambiente (contorno, aberturas, acabamentos atuais), propõe piso/forro/rodapé/tabica e " +
      "materiais, confirma com o usuário, aplica com as ferramentas e verifica.",
    args: [
      { name: "ambiente", description: "Número ou nome do ambiente (ex.: 101 ou Sala). Vazio = perguntar.", required: false },
      { name: "nivel", description: "Nível do ambiente, se houver ambiguidade (ex.: Térreo).", required: false },
    ],
    build: (a) => `Quero tratar o ambiente ${alvo(a.ambiente, "(pergunte qual)")}${a.nivel ? ` do nível ${a.nivel}` : ""}.
Siga este roteiro, um passo por vez, e me mostre o resultado de cada etapa:

1. Levantamento
   - Ache o ambiente com \`list_spatial_elements\` (confira status placed; se notEnclosed, pare e explique).
   - Contorno e paredes delimitadoras: \`get_room_boundaries\`. Portas e janelas: \`get_room_openings\`.
   - Acabamentos atuais e áreas: \`compute_room_finishes\`. Camadas das paredes/pisos: \`get_compound_structure\`.
   - Se ajudar, mostre a planta com \`capture_view_image\`.
2. Proposta
   - Liste os tipos disponíveis (\`get_available_family_types\`) e materiais (\`list_materials\`).
   - Proponha piso de acabamento, forro (altura), rodapé, tabica e troca de acabamento de parede, com
     quantidades (m², m) tiradas do levantamento. Espere minha confirmação antes de seguir.
3. Aplicação (só depois de eu confirmar)
   - Fechamento completo de uma vez: \`create_room_envelope\` (primeiro com dryRun:true; aplicar pede aprovação).
   - Ou por partes: \`create_floor\`, \`create_ceiling\`, \`create_room_perimeter_profile\`.
   - Material de acabamento em paredes/pisos: \`assign_material\` (prévia com dryRun:true; aplicar pede aprovação).
   - Material novo, se faltar: \`create_material\`.
4. Verificação
   - Recalcule com \`compute_room_finishes\` e compare com a proposta.
   - Confira avisos novos com \`warnings_delta\` (baseline antes da aplicação, delta depois).
   - Mostre o resultado com \`capture_view_image\` e resuma os ids criados.
${CONVENCOES}`,
  },
  {
    name: "roteiro_documentacao",
    title: "Roteiro: documentação por ambiente (vistas, folhas, tabelas, PDF)",
    description:
      "Cria vistas por ambiente, monta folhas, coloca as vistas, cria e formata tabelas e exporta o PDF.",
    args: [
      { name: "ambientes", description: "Ambientes (números/nomes separados por vírgula) ou um nível. Vazio = perguntar.", required: false },
      { name: "escala", description: "Escala das vistas, ex.: 50 para 1:50 (padrão: perguntar).", required: false },
    ],
    build: (a) => `Quero documentar ${alvo(a.ambientes, "os ambientes que eu indicar (pergunte quais)")}${a.escala ? ` na escala 1:${a.escala}` : ""}.
Siga este roteiro e me mostre o plano antes de criar qualquer coisa:

1. Ponto de partida
   - Ambientes: \`list_spatial_elements\`. Vistas e folhas existentes: \`list_views\`, \`list_sheets\`.
   - Padrões de folha e nomes do escritório: \`get_office_knowledge\` (tópicos folhas e nomenclatura).
   - Carimbos disponíveis: \`list_titleblocks\`. Modelos de vista: \`list_view_templates\`.
2. Vistas por ambiente
   - \`create_room_views\` (planta recortada + elevações internas; prévia com dryRun:true; aplicar pede aprovação).
   - Ajustes: \`set_view_crop\`, \`apply_view_template\`, \`set_view_properties\`.
3. Folhas
   - \`create_sheets\` (número, nome, carimbo) e \`place_on_sheet\` (posições em mm do papel).
   - Alinhamento: \`align_viewports\`. Parâmetros do carimbo: \`set_titleblock_parameters\`.
   - Confira a montagem com \`get_sheet_layout\` e \`capture_view_image\` da folha.
4. Tabelas
   - \`create_schedule\` (campos, filtros, ordenação), conferência com \`get_schedule_data\`.
   - Visual: \`style_schedule\` (cabeçalho, listras, linhas de grade) e coloque na folha com \`place_on_sheet\`.
5. Anotação e revisão
   - Etiquetas: \`tag_all_in_view\` (prévia por padrão). Pendências: \`find_untagged_elements\`, \`audit_sheets_views\`.
6. Entrega
   - \`export_pdf\` (use listOnly:true para conferir a lista de folhas antes de exportar) e informe os arquivos gerados.
${CONVENCOES}`,
  },
  {
    name: "roteiro_marcenaria",
    title: "Roteiro: marcenaria (módulos, ajustes, lista de corte)",
    description:
      "Define os módulos de marcenaria de um ambiente, cria e ajusta os módulos e gera a lista de corte.",
    args: [
      { name: "ambiente", description: "Ambiente onde vai a marcenaria (número ou nome). Vazio = perguntar.", required: false },
      { name: "programa", description: "O que se quer (ex.: balcão de pia 2 portas + aéreo 3 portas).", required: false },
    ],
    build: (a) => `Quero projetar a marcenaria de ${alvo(a.ambiente, "um ambiente (pergunte qual)")}${a.programa ? `: ${a.programa}` : ""}.
Siga este roteiro:

1. Levantamento
   - Ambiente e contorno: \`list_spatial_elements\` e \`get_room_boundaries\` (comprimentos de parede em mm).
   - Portas e janelas que limitam os módulos: \`get_room_openings\`. Níveis: \`list_levels\`.
   - Materiais (MDF, padrões): \`list_materials\`; crie o que faltar com \`create_material\`.
2. Proposta
   - Proponha os módulos (tipo base/aéreo/torre, largura, altura, profundidade, portas, gavetas, prateleiras,
     posição e rotação) numa tabela e espere minha confirmação.
3. Criação
   - Um módulo por chamada com \`create_millwork_module\` (primeiro com dryRun:true para conferir as peças).
   - Ajustes depois de criado: \`configure_millwork_module\` (reconstrói no mesmo lugar).
   - Tampo com cuba, se houver: \`create_countertop\`.
   - Confira com \`capture_view_image\` (planta e 3D).
4. Lista de corte
   - \`millwork_cutting_list\` agrupada por material, com estimativa de chapas; apresente a tabela e os totais.
${CONVENCOES}`,
  },
  {
    name: "roteiro_qa",
    title: "Roteiro: controle de qualidade do modelo",
    description:
      "Avalia a saúde do modelo, agrupa os avisos, mostra a prévia da limpeza e propõe correções com aprovação.",
    args: [
      { name: "profundidade", description: "rapida (padrão) ou completa (inclui famílias purgáveis e auditorias).", required: false },
    ],
    build: (a) => {
      const completa = /complet/i.test(a.profundidade ?? "");
      return `Quero um controle de qualidade ${completa ? "completo" : "rápido"} do modelo aberto.
Siga este roteiro e não altere nada sem me mostrar a prévia:

1. Saúde geral
   - \`get_model_health\`${completa ? " com deep:true" : ""}: nota, problemas priorizados e a ferramenta sugerida para cada um.
2. Avisos
   - \`get_model_warnings\` agrupado por tipo; explique os maiores grupos e a correção sugerida.
   - Guarde a linha de base com \`warnings_delta\` (operation baseline) antes de corrigir qualquer coisa.
3. Verificações${completa ? "" : " (as que a nota indicar)"}
   - \`check_naming\`, \`check_required_parameters\`, \`check_levels_grids\`, \`find_untagged_elements\`${completa ? ", `audit_families`, `audit_sheets_views`, `check_clashes`" : ""}.
4. Limpeza (prévia)
   - \`purge_unused\` só lista por padrão: mostre o que seria removido. Aplicar exige apply:true, dryRun:false e
     aprovação (\`propose_change_plan\`).
5. Correções
   - Proponha as correções em lista (ex.: renomear com \`rename_elements\`, preencher parâmetros com
     \`set_parameters\`, etiquetar com \`tag_all_in_view\`, excluir duplicados com \`delete_elements\`); as de
     risco médio passam pela aprovação. Aplique só o que eu aprovar.
6. Conferência
   - \`warnings_delta\` (operation delta) para provar que não surgiram avisos novos, e \`get_model_health\` de novo
     para comparar a nota.
${CONVENCOES}`;
    },
  },
];

/** Registers the workflow prompts on the MCP server (before connect, so the prompts capability is advertised). */
export function registerWorkflowPrompts(server: McpServer, prompts: WorkflowPrompt[] = workflowPrompts): string[] {
  const names: string[] = [];
  for (const p of prompts) {
    const shape: Record<string, z.ZodTypeAny> = {};
    for (const arg of p.args) {
      const base = z.string().describe(arg.description);
      shape[arg.name] = arg.required ? base : base.optional();
    }
    server.registerPrompt(
      p.name,
      { title: p.title, description: p.description, argsSchema: shape as any },
      ((args: Record<string, string | undefined>) => ({
        description: p.description,
        messages: [{ role: "user" as const, content: { type: "text" as const, text: p.build(args ?? {}) } }],
      })) as any
    );
    names.push(p.name);
  }
  return names;
}
