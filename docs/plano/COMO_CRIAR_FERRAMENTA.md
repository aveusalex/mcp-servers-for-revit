# Como criar uma ferramenta (Fase 1+)

Uma ferramenta = **uma classe C#** em `commandset/Tools/<Grupo>/` (mais a classe de argumentos). Nada de arquivo TS,
`command.json` ou registro manual: o plugin descobre a classe, o `ManifestGen` gera o schema e o servidor a expõe.

Exemplos prontos para copiar o estilo: `Tools/Editing/SetElementsPinned*.cs` (escrita simples),
`Tools/Query/FindElementsTool.cs` (leitura com filtros e paginação), `Tools/Parameters/*` (uso do ParameterService),
`Tools/Sheets/CreateSheetsTool.cs` (criação em lote com validação prévia), `Tools/Core/GetSessionContextTool.cs`.

## Esqueleto

```csharp
[McpTool("nome_snake_case", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
    Description = "O que faz, quando usar, unidades, como desfazer. Em inglês, 2–5 frases.")]
public sealed class MinhaFerramenta : McpTool<MinhaFerramentaArgs>
{
    protected override void Validate(MinhaFerramentaArgs a) { /* throw ToolException("INVALID_ARGUMENTS", ...) */ }
    protected override object Run(ToolContext ctx, MinhaFerramentaArgs a)
    {
        // ctx.Doc = documento alvo (NUNCA ActiveUIDocument, exceto Kind = UI)
        // ctx.Services.Categories / Units / Lookup / Names / Parameters / Paging
        // ctx.Warnings.Add(new ToolWarning(...)) para avisos próprios
        return new { ... }; // objeto anônimo/POCO → vira "data" no envelope
    }
}
```

## Regras

- **Kind:** `Read` (sem transação), `Write` (o despachante abre a transação, captura avisos, faz dryRun e rastreia
  mudanças: **nunca abra Transaction você mesmo**; use `SubTransaction` só se precisar de isolamento por item),
  `UI` (seleção, vista ativa, zoom: exige documento ativo, não roda em lote nem em dryRun).
- **Risk:** `None` (leitura) · `Low` (criação/edição aditiva e reversível de poucos elementos) · `Medium` (em massa,
  tipos, exclusões, renomear/renumerar muitos, purge) · `High` (irreversível, arquivos fora da pasta de saída, código).
  `Medium+` passa pela aprovação humana. Na dúvida entre dois, use o maior.
- **Argumentos:** POCO com `[JsonProperty("camelCase")]`, `[Description]`, `[Required]`, `[MinLength]/[MaxLength]`,
  `[Range]`, `[DefaultValue]`. Medidas **sempre** com `[Unit("mm"|"m2"|"m3"|"deg")]` (o teste de contrato falha sem).
  Listas fechadas com `[EnumValues("a","b")]` (string) ou enum C#. IDs de elemento: `long` (ou `string` quando aceitar
  também UniqueId / `"link:elemento"`). Não declare `document` nem `dryRun` (o framework injeta).
- **Nomes/categorias/níveis/tipos/vistas por nome:** use `ctx.Services.Lookup` e `ctx.Services.Categories` (aceitam
  pt-BR e inglês, dão `AMBIGUOUS_NAME`/`NOT_FOUND` com sugestões). Parâmetros: `ctx.Services.Parameters`.
- **Erros esperados:** `throw new ToolException(code, mensagem, hint, recommendedTool)`. Códigos estáveis em
  MAIÚSCULAS. Nunca `TaskDialog`, nunca `MessageBox`, nunca devolver "sucesso" sem ter feito.
- **Leia de volta** o que escreveu (nome aplicado, parâmetro gravado, elemento criado) e devolva os ids.
- **Listas grandes:** pagine (`limit/offset`, `ctx.Services.Paging`) e devolva `total`.
- **Unidades no fio:** mm, m², m³, graus. Internas do Revit = pés/radianos: converta com `ctx.Services.Units` ou
  `UnitUtils`.
- **Versões:** compila para R22–R26 (e R27). Use `#if REVIT2024_OR_GREATER` etc. para APIs que mudaram
  (`ElementId.Value` vs `IntegerValue`: use `id.GetValue()` / `long.ToRevitElementId()` de `Utils/ElementIdExtensions`).
- **Arquivos:** só ler/gravar dentro de pastas permitidas (padrão `%USERPROFILE%\Documents\RevitMCP\` e
  `REVIT_MCP_ALLOWED_DIRS`); fora disso recuse com `PATH_NOT_ALLOWED`.
- **Licença:** inspiração nas fontes de `research/capabilities/*.md` (coluna "Melhor referência"). Não copie código de
  sam-aec (GPL) nem do MAPA (proprietário); bimwright (Apache-2.0) só reescrito; MIT pode adaptar com atribuição.

## Depois de escrever

1. `dotnet build commandset/RevitMCPCommandSet.csproj -c "Debug R26" -p:AppData=<pasta temporária>` (e R22, R24, R25).
   O `-p:AppData` evita copiar o add-in para o Revit do usuário.
2. `cd server && npm run manifest && npm test` (regenera `tools.manifest.json` e roda os testes de contrato).
3. Commit pequeno por grupo de ferramentas.
