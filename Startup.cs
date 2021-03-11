using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Throttling
{
    public interface IResourceLimiter
    {
        // For metrics, an inaccurate view of resources
        long EstimatedCount { get; }

        // Fast synchronous attempt to acquire resources, it won't actually acquire the resource
        IResource TryAcquire(long requestedCount, bool minimumCount = false);

        // Wait until the requested resources are available
        ValueTask<IResource> AcquireAsync(long requestedCount, bool minimumCount = false, CancellationToken cancellationToken = default);
    }

    public interface IResource : IDisposable
    {
        long Count { get; }

        // Return a part of the obtained resources early.
        void Release(long releaseCount);
    }

    public interface IResourceStore
    {
        // Read the resource count for a given Key
        ValueTask<long> GetResourceCountAsync(string key);

        // Update the resource count for a given Key, negative to free up resources
        ValueTask<long> UpdateResourceCountAsync(string key, long resourceRequested, bool allowBestEffort = false);
    }

    public interface IResourcePolicy
    {
        // Empty string if the policy is not applicable
        string GenerateKey(ResourceContext context);

        IResource CreateLimiter(string key);
    }

    public class ResourceContext
    {
        public ResourceContext(HttpContext context)
        {
            HttpContext = context;
        }

        public HttpContext HttpContext { get; }
    }

    // public interface IResourceManager

    public interface IResourceManager
    {
        IEnumerable<IResource> GetLimiters(ResourceContext context);
    }

    public class ResourceManager : IResourceManager
    {
        private IEnumerable<IResourcePolicy> _policies;
        private MemoryCache _limiterCache = new MemoryCache(new MemoryCacheOptions());

        public ResourceManager(IEnumerable<IResourcePolicy> policies)
        {
            _policies = policies;
        }

        public IEnumerable<IResource> GetLimiters(ResourceContext context)
        {
            foreach (var policy in _policies)
            {
                var key = policy.GenerateKey(context);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                yield return _limiterCache.GetOrCreate(key, e => policy.CreateLimiter(key));
            }
        }
    }

    public class GETResourcePolicy : IResourcePolicy
    {
        private IResourceStore _store;

        public GETResourcePolicy(IResourceStore store)
        {
            _store = store;
        }

        public string GenerateKey(ResourceContext context)
        {
            if (context.HttpContext.Request.Method == HttpMethods.Get)
            {
                return nameof(GETResourcePolicy) + context.HttpContext.Connection.RemoteIpAddress.ToString();
            }

            return string.Empty;
        }

        public IResource CreateLimiter(string key)
        {
            return new ResourceWithStore(_store, key, 100);
        }
    }

    // Non-renewable
    internal class ResourceWithStore : IResource
    {
        private string _key;
        private IResourceStore _store;
        private long _resourceCap;
        private object _lock = new object();
        private ManualResetEventSlim _mre;

        public ResourceWithStore(IResourceStore store, string key, long resouceCap)
        {
            _key = key;
            _store = store;
            _resourceCap = resouceCap;
        }

        public long EstimatedCount => _resourceCap - _store.GetResourceCountAsync(_key).GetAwaiter().GetResult();

        public void Release(long consumedCount)
        {
            _store.UpdateResourceCountAsync(_key, -consumedCount).GetAwaiter().GetResult();
        }

        public bool TryPeek(long requestedCount) => EstimatedCount > requestedCount;

        public async ValueTask<long> WaitAsync(long requestedCount, bool minimumCount = false, CancellationToken cancellationToken = default)
        {
            var currentCount = EstimatedCount;
            var updatedCount = await _store.UpdateResourceCountAsync(_key, requestedCount);
            if (updatedCount > _resourceCap)
            {
                updatedCount = await _store.UpdateResourceCountAsync(_key, _resourceCap - updatedCount);
            }

            return updatedCount - currentCount;
        }
    }

    internal class MockRedisConnection
    {

    }

    internal class RedisResourceStore : IResourceStore
    {
        private MockRedisConnection _connection;
        private MemoryCache _localCache;

        public RedisResourceStore(MockRedisConnection connection)
        {
            _connection = connection;

            // start a background task that periodically syncs the local cache with redis
        }

        public ValueTask<long> GetResourceCountAsync(string id)
        {
            return ValueTask.FromResult(_localCache.GetOrCreate(id, e => 0L));
        }

        public async ValueTask<long> UpdateResourceCountAsync(string id, long resourceRequested, bool allowBestEffort)
        {
            var result = await GetResourceCountAsync(id) + resourceRequested;
            // Need to lock on key
            _localCache.Set(id, result);

            return result;
        }
    }

    internal class ResourceLimiter : IResourceLimiter
    {
        private long _resourceCount;
        private object _lock = new object();
        private ManualResetEventSlim _mre = new ManualResetEventSlim(); // Dispose?
        private Timer _renewTimer;

        public long EstimatedCount => Interlocked.Read(ref _resourceCount);

        public ResourceLimiter(long initialResourceCount, long newResourcePerSecond)
        {
            _resourceCount = initialResourceCount;

            // Start timer, yikes allocations from capturing
            _renewTimer = new Timer(o =>
            {
                var resource = o as ResourceLimiter;

                if (resource == null)
                {
                    return;
                }

                lock (resource._lock)
                {
                    if (resource._resourceCount < initialResourceCount)
                    {
                        var resourceToAdd = Math.Min(newResourcePerSecond, initialResourceCount - _resourceCount);
                        Interlocked.Add(ref _resourceCount, resourceToAdd);
                        _mre.Set();
                    }
                }
            }, this, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        public ValueTask<IResource> AcquireAsync(long requestedCount, bool minimumCount = false, CancellationToken cancellationToken = default)
        {
            lock (_lock) // Check lock check
            {
                if (EstimatedCount > requestedCount)
                {
                    Interlocked.Add(ref _resourceCount, -requestedCount);
                    return ValueTask.FromResult((IResource)(new Resource(requestedCount, this)));
                }
            }

            if (minimumCount)
            {
                lock (_lock)
                {
                    var available = EstimatedCount;
                    if (available > 0)
                    {
                        var obtainedResource = Math.Min(requestedCount, available);
                        Interlocked.Add(ref _resourceCount, -obtainedResource);
                        return ValueTask.FromResult((IResource)(new Resource(obtainedResource, this)));
                    }
                    return ValueTask.FromResult((IResource)(new Resource(0, this)));
                }
            }

            while (true)
            {
                _mre.Wait(cancellationToken); // Handle cancellation

                lock (_lock)
                {
                    if (_mre.IsSet)
                    {
                        _mre.Reset();
                    }

                    if (EstimatedCount > requestedCount)
                    {
                        Interlocked.Add(ref _resourceCount, -requestedCount);
                        return ValueTask.FromResult((IResource)(new Resource(requestedCount, this)));
                    }
                }
            }
        }

        public IResource TryAcquire(long requestedCount, bool minimumCount = false)
        {
            if (EstimatedCount > requestedCount)
            {
                Interlocked.Add(ref _resourceCount, -requestedCount);
                return new Resource(requestedCount, this);
            }

            if (minimumCount)
            {
                // Check resource count is positive
                return new Resource(Interlocked.Exchange(ref _resourceCount, 0), this);
            }

            return new Resource(0, this);
        }

        internal class Resource : IResource
        {
            private long _count;
            private ResourceLimiter _limiter;

            public Resource(long count, ResourceLimiter limiter)
            {
                _count = count;
                _limiter = limiter;
            }

            public long Count => _count;

            public void Dispose()
            {
                // If ResourceLimiter is non-renewable
                // ResourceLimiter.Release(_count);
            }

            public void Release(long releaseCount)
            {
                // If ResourceLimiter is non-renewable
                // if (releaseCount <= _count)
                // {
                //     _count -= releaseCount
                //     ResourceLimiter.Release(releaseCount);
                // }
            }
        }
    }
    public class Startup
    {
        private IResource _renewable = new RenewableResource(5, 2);
        private IResource _nonRenewable = new NonRenewableResource(5);
        private IResourceLimiter _resourceLimiter = new ResourceLimiter(5, 2);

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IResourceStore, RedisResourceStore>();
            services.AddSingleton<IResourcePolicy, GETResourcePolicy>();
            services.AddSingleton<IResourcePolicy, POSTResourcePolicy>();
            services.AddSingleton<IResourceManager, ResourceManager>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", async context =>
                {
                    // Renewable
                    if (await _renewable.WaitAsync(1) == 0)
                    {
                        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        return;
                    }
                    await context.Response.WriteAsync("Hello World!");

                    // Nonrenewable
                    await _nonRenewable.WaitAsync(1, cancellationToken: context.RequestAborted); // check the count is non-zero
                    try
                    {
                        await context.Response.WriteAsync("Hello World!");
                    }
                    finally
                    {
                        _nonRenewable.Release(1);
                    }

                    // ResourceLimiter
                    using (var resourceWrapper = await _resourceLimiter.AcquireAsync(1, cancellationToken: context.RequestAborted))
                    {
                        if (resourceWrapper.Count == 1)
                        {
                            await context.Response.WriteAsync("Hello World!");
                        }
                        // 429
                    }

                    // DI based limiter
                    var manager = context.RequestServices.GetRequiredService<IResourceManager>();
                    var limiters = manager.GetLimiters(new ResourceContext(context));

                    foreach (var limiter in limiters)
                    {
                        // handle where only some of the limiters were obtained
                        // Order is import to prevent deadlock
                        // WaitAll might deadlock
                        await limiter.WaitAsync(1);
                    }

                    try
                    {
                        await context.Response.WriteAsync("Hello World!");
                    }
                    finally
                    {
                        foreach (var limiter in limiters)
                        {
                            limiter.Release(1);
                        }
                    }
                });
            });
        }
    }
}
