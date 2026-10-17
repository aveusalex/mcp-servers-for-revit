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
  {
    name: "roteiro_familia",
    title: "Roteiro: criar uma família paramétrica simples",
    description:
      "Cria uma família de modelo genérico (caixa ou perfil extrudado) com largura/profundidade/altura travadas a " +
      "parâmetros e material, testa a flexão, carrega no projeto e insere uma instância.",
    args: [
      { name: "familia", description: "O que modelar e as medidas (ex.: nicho 600x300x400). Vazio = perguntar.", required: false },
      { name: "tipos", description: "Tipos a criar (ex.: 600x300, 900x300). Vazio = só o tipo padrão.", required: false },
    ],
    build: (a) => `Quero criar a família: ${alvo(a.familia, "(pergunte o que modelar e as medidas)")}.
${a.tipos ? `Tipos desejados: ${a.tipos}.\n` : ""}Siga este roteiro, um passo por vez:

1. Antes de criar
   - Veja se já existe algo parecido: \`list_families\` e \`get_available_family_types\` (reaproveitar é melhor que
     duplicar). Se existir e só faltar um tamanho, use \`duplicate_type\` + \`set_type_parameters\`.
   - Combine comigo nome, categoria, medidas, quais parâmetros são de instância e o material.
2. Criação (prévia primeiro)
   - \`create_family_from_template\` com dryRun:true: a família é montada e flexionada em memória e a resposta diz
     se largura, profundidade e altura ficaram travadas (locks/flex). Se algum eixo não flexionar, pare e explique.
   - Contorno não retangular: passe polygon (só a altura fica paramétrica).
   - Aplicar (dryRun:false) é risco médio: \`propose_change_plan\` → \`wait_for_plan_decision\` → \`execute_plan\`.
3. Uso no projeto
   - Insira uma instância com \`place_family_instance\` e confira com \`get_element_details\`.
   - Ajustes finos de parâmetros/fórmulas: \`edit_family\` (abre a cópia no editor) + \`manage_family_parameters\`,
     e \`edit_family\` de novo para recarregar.
   - Material: \`create_finish_material\` se o acabamento não existir; depois \`set_type_parameters\`.
4. Verificação
   - \`capture_view_image\` numa vista 3D para eu ver a peça, e \`get_model_warnings\` para avisos novos.
${CONVENCOES}`,
  },
  {
    name: "roteiro_conversao",
    title: "Roteiro: converter uma referência em modelo nativo",
    description:
      "Converte um vínculo Revit (ou uma planta de referência) em elementos nativos do projeto: níveis, paredes, " +
      "portas/janelas e ambientes, conferindo áreas no fim.",
    args: [
      { name: "origem", description: "Vínculo Revit (nome), arquivo .rvt ou imagem de planta. Vazio = perguntar.", required: false },
      { name: "escopo", description: "O que converter (ex.: só paredes do térreo). Vazio = níveis, paredes, aberturas e ambientes.", required: false },
    ],
    build: (a) => `Quero converter ${alvo(a.origem, "(pergunte qual referência)")} em modelo nativo.
Escopo: ${alvo(a.escopo, "níveis, paredes, portas/janelas e ambientes")}.
Siga este roteiro e me mostre a prévia de cada etapa:

1. Referência
   - Vínculo Revit: \`list_links\` e \`get_link_elements\` (níveis, paredes com linha, espessura e altura; portas e
     janelas com hospedeiro e posição) — tudo em coordenadas do projeto.
   - Arquivo .rvt para trazer inteiro como grupo: \`load_rvt_as_group\` (risco médio, pede aprovação).
   - Imagem de planta: \`import_image\` numa vista, em escala; as medidas devem vir de mim (não leia cotas no olho).
   - Resuma o que vai ser criado (quantidades por categoria) e espere minha confirmação.
2. Níveis
   - \`setup_project_levels\` (ou \`create_or_update_levels\`) com as cotas da referência; confira com \`list_levels\`.
3. Paredes
   - Mapeie cada espessura para um tipo existente (\`get_available_family_types\`); se faltar, \`duplicate_type\` +
     \`set_compound_structure\`.
   - \`create_walls\` em lotes por nível, com a linha de localização correta (a resposta traz a linha de referência
     para conferir). Una as paredes que precisarem com \`join_cut_geometry\`.
4. Portas e janelas
   - \`place_door_window\` em cada parede nova (posição ao longo da parede em mm, peitoril das janelas).
5. Ambientes e conferência
   - \`create_rooms\` (mode all_circuits) e \`list_spatial_elements\`: compare as áreas com a referência.
   - \`get_model_warnings\` para paredes sobrepostas; \`capture_view_image\` para eu comparar lado a lado.
   - Ao terminar, o vínculo pode ser descarregado com \`manage_links\` (operation unload).
${CONVENCOES}`,
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
