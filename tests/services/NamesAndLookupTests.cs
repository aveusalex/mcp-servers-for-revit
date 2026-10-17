using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using Xunit;

namespace RevitMCPCommandSet.Services.Tests;

public class NameMatcherTests
{
    private static NamedCandidate<int>[] Levels(params string[] names) =>
        names.Select((n, i) => new NamedCandidate<int>(n, i, "id " + i)).ToArray();

    [Fact]
    public void Exact_match_wins_over_lenient_match()
    {
        var c = Levels("Térreo", "terreo", "1º Pavimento");
        Assert.Equal(0, NameMatcher.Single("Térreo", c, "level"));
    }

    [Fact]
    public void Lenient_match_ignores_case_accents_and_spaces()
    {
        var c = Levels("Pavimento Térreo", "1º Pavimento");
        Assert.Equal(0, NameMatcher.Single("pavimento terreo", c, "level"));
    }

    [Fact]
    public void Two_lenient_hits_is_ambiguous_and_lists_candidates()
    {
        var c = Levels("Térreo", "TÉRREO", "Cobertura");
        var ex = Assert.Throws<ToolException>(() => NameMatcher.Single("térreo", c, "level"));
        Assert.Equal("AMBIGUOUS_NAME", ex.Code);
        Assert.Contains("Térreo (id 0)", ex.Message);
        Assert.Contains("TÉRREO (id 1)", ex.Message);
    }

    [Fact]
    public void Duplicate_exact_names_are_ambiguous()
    {
        var c = Levels("A1", "A1");
        Assert.Equal("AMBIGUOUS_NAME", Assert.Throws<ToolException>(() => NameMatcher.Single("A1", c, "type")).Code);
    }

    [Fact]
    public void Not_found_suggests_close_names()
    {
        var c = Levels("Pavimento Tipo", "Cobertura", "Barrilete");
        var ex = Assert.Throws<ToolException>(() => NameMatcher.Single("Cobertur", c, "level"));
        Assert.Equal("NOT_FOUND", ex.Code);
        Assert.Contains("Cobertura", ex.Hint);
    }

    [Theory]
    [InlineData("Carimbo A1: A1", "Carimbo A1", "A1")]
    [InlineData("A1", null, "A1")]
    [InlineData(" Fam :  Tipo ", "Fam", "Tipo")]
    public void Family_type_name_parsing(string text, string? family, string type)
    {
        FamilyTypeName.Parse(text, out var f, out var t);
        Assert.Equal(family, f);
        Assert.Equal(type, t);
    }
}

public class NameRulesTests
{
    [Theory]
    [InlineData("A-101")]
    [InlineData("Planta Baixa - Térreo")]
    [InlineData("Corte AA (1-50)")]
    public void Legal_names_pass(string name)
    {
        NameRules.Validate(name, "sheet name");
    }

    [Theory]
    [InlineData("A{1}")]
    [InlineData("Planta: Térreo")]
    [InlineData("x|y")]
    [InlineData("a;b")]
    [InlineData("a<b>")]
    [InlineData("a?")]
    [InlineData("a`b")]
    [InlineData("a~b")]
    [InlineData("a\\b")]
    [InlineData("[x]")]
    public void Illegal_characters_fail(string name)
    {
        var ex = Assert.Throws<ToolException>(() => NameRules.Validate(name));
        Assert.Equal("INVALID_NAME", ex.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" lead")]
    public void Empty_or_padded_names_fail(string name)
    {
        Assert.Equal("INVALID_NAME", Assert.Throws<ToolException>(() => NameRules.Validate(name)).Code);
    }

    [Fact]
    public void Collision_is_case_insensitive()
    {
        var ex = Assert.Throws<ToolException>(() => NameRules.EnsureUnique("a-101", new[] { "A-100", "A-101" }, "sheet number"));
        Assert.Equal("NAME_COLLISION", ex.Code);
        NameRules.EnsureUnique("A-102", new[] { "A-100", "A-101" });
    }

    [Fact]
    public void Duplicates_within_request()
    {
        Assert.Equal(new[] { "A-101" }, NameRules.DuplicatesIn(new[] { "A-101", "A-102", "a-101" }));
    }
}

public class ElementRefTests
{
    [Fact]
    public void Numeric_id()
    {
        var r = ElementRef.Parse(" 123456 ");
        Assert.Equal(ElementRefKind.Id, r.Kind);
        Assert.Equal(123456, r.Id);
    }

    [Fact]
    public void Large_id_beyond_int()
    {
        Assert.Equal(5_000_000_000L, ElementRef.Parse("5000000000").Id);
    }

    [Fact]
    public void Linked_reference()
    {
        var r = ElementRef.Parse("987:654");
        Assert.Equal(ElementRefKind.Linked, r.Kind);
        Assert.Equal(987, r.LinkInstanceId);
        Assert.Equal(654, r.Id);
    }

    [Fact]
    public void Unique_id()
    {
        const string uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
        var r = ElementRef.Parse(uid);
        Assert.Equal(ElementRefKind.UniqueId, r.Kind);
        Assert.Equal(uid, r.UniqueId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("1:2:3")]
    public void Invalid_references(string text)
    {
        Assert.Equal("INVALID_ARGUMENTS", Assert.Throws<ToolException>(() => ElementRef.Parse(text)).Code);
    }
}

public class PagingTests
{
    [Fact]
    public void Middle_page()
    {
        var p = Paging.Page(Enumerable.Range(0, 10), 3, 3);
        Assert.Equal(new[] { 3, 4, 5 }, p.Items);
        Assert.Equal(10, p.Total);
        Assert.True(p.Truncated);
    }

    [Fact]
    public void Last_page_is_not_truncated()
    {
        var p = Paging.Page(Enumerable.Range(0, 10), 5, 5);
        Assert.Equal(5, p.Items.Count);
        Assert.False(p.Truncated);
    }

    [Fact]
    public void Offset_beyond_end_is_empty()
    {
        var p = Paging.Page(Enumerable.Range(0, 3), 5, 10);
        Assert.Empty(p.Items);
        Assert.Equal(3, p.Total);
        Assert.False(p.Truncated);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5001, 0)]
    [InlineData(10, -1)]
    public void Bad_limits_fail(int limit, int offset)
    {
        Assert.Equal("INVALID_ARGUMENTS", Assert.Throws<ToolException>(() => Paging.Page(new[] { 1 }, limit, offset)).Code);
    }
}
