using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests.Collections;

/// <summary>
/// Unit tests for <see cref="CollectionPageFactory"/>.
/// </summary>
public class CollectionPageFactoryTests
{
    [Fact]
    public void FromOrderedCollectionPage_ValidPage_ReturnsFlattenedPage()
    {
        var page = new OrderedCollectionPage
        {
            Id = "https://a.domain.local/c/1",
            Items = new IObjectOrLink[]
            {
                new Note { Id = "https://a.domain.local/n/1" },
                new Link { Href = new Uri("https://a.domain.local/n/2") },
            },
            Next = new Link { Href = new Uri("https://a.domain.local/c/2") },
            Prev = new Link { Href = new Uri("https://a.domain.local/c/0") },
            TotalItems = 42,
        };

        var result = CollectionPageFactory.FromOrderedCollectionPage((IObject)page);

        Assert.NotNull(result);
        Assert.Same(page, result!.Page);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("https://a.domain.local/c/2", result.NextPage!.Value.Value);
        Assert.Equal("https://a.domain.local/c/0", result.PrevPage!.Value.Value);
        Assert.Equal(42, result.TotalItems);
        Assert.Equal("https://a.domain.local/c/1", result.PageId!.Value.Value);
        Assert.False(result.IsLastPage);
    }

    [Fact]
    public void FromOrderedCollectionPage_NoNextPage_IsLastPage()
    {
        var page = new OrderedCollectionPage
        {
            Id = "https://a.domain.local/c/last",
            Items = [],
        };

        var result = CollectionPageFactory.FromOrderedCollectionPage((IObject)page);

        Assert.NotNull(result);
        Assert.Null(result!.NextPage);
        Assert.True(result.IsLastPage);
    }

    [Fact]
    public void FromOrderedCollectionPage_NoItems_ReturnsEmptyList()
    {
        var page = new OrderedCollectionPage
        {
            Id = "https://a.domain.local/c/empty",
        };

        var result = CollectionPageFactory.FromOrderedCollectionPage((IObject)page);

        Assert.NotNull(result);
        Assert.Empty(result!.Items);
    }

    [Fact]
    public void FromOrderedCollectionPage_NoId_ReturnsNullPageId()
    {
        var page = new OrderedCollectionPage();

        var result = CollectionPageFactory.FromOrderedCollectionPage((IObject)page);

        Assert.NotNull(result);
        Assert.Null(result!.PageId);
    }

    [Fact]
    public void FromOrderedCollectionPage_NonPageObject_ReturnsNull()
    {
        IObject notAPage = new Note { Id = "https://a.domain.local/n/1" };

        var result = CollectionPageFactory.FromOrderedCollectionPage(notAPage);

        Assert.Null(result);
    }

    [Fact]
    public void FromOrderedCollectionPage_Null_ReturnsNull()
    {
        var result = CollectionPageFactory.FromOrderedCollectionPage(null);

        Assert.Null(result);
    }
}
