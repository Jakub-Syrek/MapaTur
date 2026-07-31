using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="LruCache{TKey, TValue}"/> — the small bounded cache in front of repeated
/// decode-heavy reads (the bake reads every hi-res tif up to ~81 times through the 3×3 margin windows ×
/// the 8-neighbour kernel padding; without a cache that turned a ~10-minute z17 bake into hours).
/// </summary>
public sealed class LruCacheTests
{
    [Fact]
    public void GetOrAdd_SecondRead_DoesNotInvokeTheFactoryAgain()
    {
        var cache = new LruCache<string, string>(capacity: 4);
        int calls = 0;

        string a1 = cache.GetOrAdd("a", _ => { calls++; return "va"; });
        string a2 = cache.GetOrAdd("a", _ => { calls++; return "va2"; });

        a1.Should().Be("va");
        a2.Should().Be("va", "the cached value is served, not re-created");
        calls.Should().Be(1);
    }

    [Fact]
    public void GetOrAdd_BeyondCapacity_EvictsTheLeastRecentlyUsed()
    {
        var cache = new LruCache<string, string>(capacity: 2);
        var made = new List<string>();
        string Make(string k) { made.Add(k); return $"v{k}"; }

        cache.GetOrAdd("a", Make);
        cache.GetOrAdd("b", Make);
        cache.GetOrAdd("a", Make);      // touch a → b becomes LRU
        cache.GetOrAdd("c", Make);      // evicts b
        cache.GetOrAdd("a", Make);      // still cached
        cache.GetOrAdd("b", Make);      // re-created

        made.Should().Equal("a", "b", "c", "b");
    }

    [Fact]
    public void GetOrAdd_NullFactoryResult_IsNotCached()
    {
        // A missing neighbour tif may appear later (download in progress) — a null must never be pinned.
        var cache = new LruCache<string, string>(capacity: 4);
        int calls = 0;

        string? first = cache.GetOrAdd("a", _ => { calls++; return null!; });
        string? second = cache.GetOrAdd("a", _ => { calls++; return "va"; });

        first.Should().BeNull();
        second.Should().Be("va");
        calls.Should().Be(2, "the null result must be retried, not remembered");
    }

    [Fact]
    public async Task GetOrAdd_ConcurrentReaders_AreSafeAndConverge()
    {
        var cache = new LruCache<int, string>(capacity: 16);

        string[][] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => Enumerable.Range(0, 200).Select(i => cache.GetOrAdd(i % 20, k => $"v{k}")).ToArray())));

        foreach (string[] worker in results)
        {
            for (int i = 0; i < worker.Length; i++)
            {
                worker[i].Should().Be($"v{i % 20}");
            }
        }
    }
}