using LibGit2CS.Attributes;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Filter;

public sealed class FilterRegistryTests
{
    [Fact]
    public void Lookup_Crlf_ReturnsFilter()
    {
        var ctx = new GitContext();
        IFilter? filter = ctx.Filters.Lookup(FilterRegistry.CrlfName);
        Assert.NotNull(filter);
        Assert.Equal(FilterRegistry.CrlfName, filter.Name);
    }

    [Fact]
    public void Lookup_Ident_ReturnsFilter()
    {
        var ctx = new GitContext();
        IFilter? filter = ctx.Filters.Lookup(FilterRegistry.IdentName);
        Assert.NotNull(filter);
        Assert.Equal(FilterRegistry.IdentName, filter.Name);
    }

    [Fact]
    public void Lookup_Unknown_ReturnsNull()
    {
        var ctx = new GitContext();
        Assert.Null(ctx.Filters.Lookup("nonexistent"));
    }

    [Fact]
    public void Register_DuplicateName_Throws()
    {
        var ctx = new GitContext();
        ctx.Filters.Register("test_dup", new NoOpFilter("test_dup"), 50);
        Assert.Throws<ArgumentException>(() =>
            ctx.Filters.Register("test_dup", new NoOpFilter("test_dup"), 60));
        ctx.Filters.Unregister("test_dup");
    }

    [Fact]
    public void Unregister_Builtin_Throws()
    {
        var ctx = new GitContext();
        Assert.Throws<ArgumentException>(() => ctx.Filters.Unregister(FilterRegistry.CrlfName));
        Assert.Throws<ArgumentException>(() => ctx.Filters.Unregister(FilterRegistry.IdentName));
    }

    [Fact]
    public void Register_CustomFilter_Lookupable()
    {
        var ctx = new GitContext();
        ctx.Filters.Register("myfilter", new NoOpFilter("myfilter"), 50);
        try
        {
            IFilter? filter = ctx.Filters.Lookup("myfilter");
            Assert.NotNull(filter);
            Assert.Equal("myfilter", filter!.Name);
        }
        finally
        {
            ctx.Filters.Unregister("myfilter");
        }
    }

    [Fact]
    public void Unregister_RemovesFilter()
    {
        var ctx = new GitContext();
        ctx.Filters.Register("temp", new NoOpFilter("temp"), 50);
        ctx.Filters.Unregister("temp");
        Assert.Null(ctx.Filters.Lookup("temp"));
    }

    private sealed class NoOpFilter(string name) : IFilter
    {
        public string Name => name;
        public string Attributes => name;
        public ValueTask<GitFilterResult> CheckAsync(GitFilterSource source, IReadOnlyList<GitAttrValue> attrValues, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GitFilterResult.Passthrough);
        public ValueTask<GitApplyResult> ApplyAsync(GitFilterSource source, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GitApplyResult.Passthrough);
    }
}
