using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using Xunit;

namespace RevitMCPCommandSet.Services.Tests;

public class CategoryCatalogTests
{
    private static readonly string[] KnownEnum =
        { "OST_Walls", "OST_Doors", "OST_GenericModel", "OST_Sheets", "OST_TitleBlocks", "OST_StructuralColumns", "OST_Columns" };

    private static string Canon(string s) =>
        KnownEnum.FirstOrDefault(e => string.Equals(e, s, StringComparison.OrdinalIgnoreCase));

    // A Portuguese document.
    private static readonly CategoryEntry[] PtBr =
    {
        new("OST_Walls", "Paredes", -2000011),
        new("OST_Doors", "Portas", -2000023),
        new("OST_GenericModel", "Modelos genéricos", -2000151),
        new("OST_Sheets", "Folhas", -2003100),
        new(null, "Minha categoria", 123),
    };

    // An English document.
    private static readonly CategoryEntry[] En =
    {
        new("OST_Walls", "Walls", -2000011),
        new("OST_Doors", "Doors", -2000023),
        new("OST_GenericModel", "Generic Models", -2000151),
    };

    [Theory]
    [InlineData("OST_Walls")]
    [InlineData("ost_walls")]
    [InlineData("Walls")]
    [InlineData("Paredes")]
    [InlineData("paredes")]
    [InlineData(" PAREDES ")]
    [InlineData("Parede")]
    public void Walls_resolve_in_any_language(string input)
    {
        Assert.Equal("OST_Walls", CategoryCatalog.Resolve(input, PtBr, Canon).BuiltInName);
        Assert.Equal("OST_Walls", CategoryCatalog.Resolve(input, En, Canon).BuiltInName);
    }

    [Fact]
    public void Localized_name_with_accents_and_spaces()
    {
        Assert.Equal("OST_GenericModel", CategoryCatalog.Resolve("modelos genericos", PtBr, Canon).BuiltInName);
        Assert.Equal("OST_GenericModel", CategoryCatalog.Resolve("Generic Models", En, Canon).BuiltInName);
        Assert.Equal("OST_GenericModel", CategoryCatalog.Resolve("Modelos genéricos", En, Canon).BuiltInName);
    }

    [Fact]
    public void Custom_category_resolves_by_localized_name()
    {
        var e = CategoryCatalog.Resolve("minha categoria", PtBr, Canon);
        Assert.Null(e.BuiltInName);
        Assert.Equal(123, e.Id);
    }

    [Fact]
    public void Enum_not_in_doc_list_still_resolves()
    {
        Assert.Equal("OST_TitleBlocks", CategoryCatalog.Resolve("Carimbos", PtBr, Canon).BuiltInName);
    }

    [Fact]
    public void Unknown_category_fails_with_suggestions_and_no_substring_match()
    {
        var ex = Assert.Throws<ToolException>(() => CategoryCatalog.Resolve("Pareds", PtBr, Canon));
        Assert.Equal("CATEGORY_NOT_FOUND", ex.Code);
        Assert.Contains("Paredes", ex.Hint);

        // "Port" must not silently resolve to Portas.
        Assert.Throws<ToolException>(() => CategoryCatalog.Resolve("Port", PtBr, Canon));
    }

    [Fact]
    public void Pilares_and_structural_columns_are_distinct()
    {
        Assert.Equal("OST_Columns", CategoryCatalog.Resolve("Pilares", En, Canon).BuiltInName);
        Assert.Equal("OST_StructuralColumns", CategoryCatalog.Resolve("Pilares estruturais", En, Canon).BuiltInName);
    }
}
