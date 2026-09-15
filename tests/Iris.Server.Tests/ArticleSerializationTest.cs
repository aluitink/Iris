using System.Text.Json;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

public class ArticleSerializationTest
{
    [Fact]
    public void Article_Serializes_WithType()
    {
        var article = new Article
        {
            Id = "https://example.com/article-1",
            Content = ["<p>Test content</p>"],
            Name = ["Test Title"],
        };

        var json = JsonSerializer.Serialize(article);
        Console.WriteLine($"Article JSON: {json}");
        Assert.Contains("\"type\":\"Article\"", json);
    }

    [Fact]
    public void Page_Serializes_WithType()
    {
        var page = new Page
        {
            Id = "https://example.com/page-1",
            Content = ["<p>Test content</p>"],
            Name = ["Test Title"],
        };

        var json = JsonSerializer.Serialize(page);
        Console.WriteLine($"Page JSON: {json}");
        Assert.Contains("\"type\":\"Page\"", json);
    }

    [Fact]
    public void Create_With_Page_Deserializes_Correctly()
    {
        var page = new Page
        {
            Id = "https://example.com/page-1",
            Content = ["<p>Test content</p>"],
        };

        var create = new Create
        {
            Id = "https://example.com/create-1",
            Actor = [new Link { Href = new Uri("https://example.com/user") }],
            Object = [page],
        };

        var json = JsonSerializer.Serialize(create);
        Console.WriteLine($"Create JSON: {json}");
        
        var deserialized = JsonSerializer.Deserialize<Create>(json);
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Object);
        
        var firstObject = deserialized.Object?.FirstOrDefault();
        Assert.NotNull(firstObject);
        
        // Check if it's a Page
        var isPage = firstObject is Page;
        Console.WriteLine($"First object is Page: {isPage}");
        Console.WriteLine($"First object type: {firstObject?.GetType()}");
        Assert.True(isPage, "The deserialized object should be a Page");
    }
}
