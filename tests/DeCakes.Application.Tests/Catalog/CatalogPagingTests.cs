using DeCakes.Application.Catalog;

namespace DeCakes.Application.Tests.Catalog;

public class CatalogPagingTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 100)]
    [InlineData(int.MaxValue, 100)]
    public void Positive_pages_and_supported_sizes_are_valid(int page, int pageSize)
    {
        Assert.Empty(CatalogPaging.Validate(page, pageSize));
    }

    [Fact]
    public void Invalid_page_and_size_report_both_fields()
    {
        var errors = CatalogPaging.Validate(0, 101);

        Assert.Equal(2, errors.Count);
        Assert.Contains("page", errors.Keys);
        Assert.Contains("pageSize", errors.Keys);
    }

    [Fact]
    public void Offset_does_not_overflow_for_the_largest_supported_page()
    {
        Assert.Equal(214748364600L, CatalogPaging.GetOffset(int.MaxValue, 100));
        Assert.Equal(0, CatalogPaging.GetOffset(1, 100));
        Assert.Equal(40, CatalogPaging.GetOffset(3, 20));
    }
}
