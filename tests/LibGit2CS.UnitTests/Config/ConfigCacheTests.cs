using LibGit2CS.Config;

namespace LibGit2CS.UnitTests.Config;

public class ConfigCacheTests
{
    private enum TestKey
    {
        A = 0,
        B = 1,
        C = 2,
    }

    [Fact]
    public void GetOrCompute_FirstCall_InvokesFactory()
    {
        var cache = new ConfigCache<TestKey>(3, notCachedSentinel: -1);
        int callCount = 0;

        int result = cache.GetOrCompute(TestKey.A, _ =>
        {
            callCount++;
            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void GetOrCompute_SecondCall_UsesCache()
    {
        var cache = new ConfigCache<TestKey>(3, notCachedSentinel: -1);
        int callCount = 0;

        _ = cache.GetOrCompute(TestKey.A, _ =>
        {
            callCount++;
            return 42;
        });
        int result = cache.GetOrCompute(TestKey.A, _ =>
        {
            callCount++;
            return 99;
        });

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void GetOrCompute_DifferentKeys_Independent()
    {
        var cache = new ConfigCache<TestKey>(3, notCachedSentinel: -1);

        int a = cache.GetOrCompute(TestKey.A, _ => 1);
        int b = cache.GetOrCompute(TestKey.B, _ => 2);

        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public void Clear_ResetsCache()
    {
        var cache = new ConfigCache<TestKey>(3, notCachedSentinel: -1);
        int callCount = 0;

        _ = cache.GetOrCompute(TestKey.A, _ =>
        {
            callCount++;
            return 42;
        });
        cache.Clear();
        int result = cache.GetOrCompute(TestKey.A, _ =>
        {
            callCount++;
            return 99;
        });

        Assert.Equal(99, result);
        Assert.Equal(2, callCount);
    }
}
