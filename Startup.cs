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

#nullable enable

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
        IResourceLimiter? ResolveResourceLimiter(HttpContext context); // Use another format for context
    }

    // public interface IResourceManager

    public interface IResourceManager
    {
        IEnumerable<IResourceLimiter> GetLimiters(HttpContext context); // Use another format for context
    }

    public class ResourceManager : IResourceManager
    {
        private IEnumerable<IResourcePolicy> _policies;

        public ResourceManager(IEnumerable<IResourcePolicy> policies)
        {
            _policies = policies;
        }

        public IEnumerable<IResourceLimiter> GetLimiters(HttpContext context)
        {
            foreach (var policy in _policies)
            {
                var limiter = policy.ResolveResourceLimiter(context);

                if (limiter != null)
                {
                    yield return limiter;
                }
            }
        }
    }

    public class ReadRateLimitPolicy : IResourcePolicy
    {
        private RenewableResourceLimiter _resourceLimiter = new RenewableResourceLimiter(5, 5);

        public IResourceLimiter? ResolveResourceLimiter(HttpContext context)
        {
            if (context.Request.Path != "/read")
            {
                return null;
            }

            return _resourceLimiter;
        }
    }

    public class DefaultRateLimitPolicy : IResourcePolicy
    {
        private RenewableResourceLimiter _resourceLimiter = new RenewableResourceLimiter(10, 5);

        public IResourceLimiter? ResolveResourceLimiter(HttpContext context) => _resourceLimiter;
    }

    public class WriteRateLimitPolicy : IResourcePolicy
    {
        private readonly int _ipBuckets;
        private NonrenewableResourceLimiter[] _writeLimiters;

        public WriteRateLimitPolicy(int ipBuckets)
        {
            _ipBuckets = ipBuckets;
            _writeLimiters = new NonrenewableResourceLimiter[_ipBuckets];
        }

        public IResourceLimiter? ResolveResourceLimiter(HttpContext context)
        {
            if (context.Request.Path != "/write")
            {
                return null;
            }

            var ipBucket = context.Connection.RemoteIpAddress?.GetAddressBytes()[0] / (byte.MaxValue / _ipBuckets) ?? 0;

            if (_writeLimiters[ipBucket] == null)
            {
                _writeLimiters[ipBucket] = new NonrenewableResourceLimiter(1);
            }

            return _writeLimiters[ipBucket];
        }
    }

    // // Non-renewable
    // internal class ResourceWithStore : IResourceLimiter
    // {
    //     private string _key;
    //     private IResourceStore _store;
    //     private long _resourceCap;
    //     private object _lock = new object();
    //     private ManualResetEventSlim _mre;

    //     public ResourceWithStore(IResourceStore store, string key, long resouceCap)
    //     {
    //         _key = key;
    //         _store = store;
    //         _resourceCap = resouceCap;
    //     }

    //     public long EstimatedCount => _resourceCap - _store.GetResourceCountAsync(_key).GetAwaiter().GetResult();

    //     public void Release(long consumedCount)
    //     {
    //         _store.UpdateResourceCountAsync(_key, -consumedCount).GetAwaiter().GetResult();
    //     }

    //     public bool TryAcquire(long requestedCount) => EstimatedCount > requestedCount;

    //     public async ValueTask<long> AquireAsync(long requestedCount, bool minimumCount = false, CancellationToken cancellationToken = default)
    //     {
    //         var currentCount = EstimatedCount;
    //         var updatedCount = await _store.UpdateResourceCountAsync(_key, requestedCount);
    //         if (updatedCount > _resourceCap)
    //         {
    //             updatedCount = await _store.UpdateResourceCountAsync(_key, _resourceCap - updatedCount);
    //         }

    //         return updatedCount - currentCount;
    //     }
    // }

    // internal class MockRedisConnection
    // {

    // }

    // internal class RedisResourceStore : IResourceStore
    // {
    //     private MockRedisConnection _connection;
    //     private MemoryCache _localCache;

    //     public RedisResourceStore(MockRedisConnection connection)
    //     {
    //         _connection = connection;

    //         // start a background task that periodically syncs the local cache with redis
    //     }

    //     public ValueTask<long> GetResourceCountAsync(string id)
    //     {
    //         return ValueTask.FromResult(_localCache.GetOrCreate(id, e => 0L));
    //     }

    //     public async ValueTask<long> UpdateResourceCountAsync(string id, long resourceRequested, bool allowBestEffort)
    //     {
    //         var result = await GetResourceCountAsync(id) + resourceRequested;
    //         // Need to lock on key
    //         _localCache.Set(id, result);

    //         return result;
    //     }
    // }

    internal class RenewableResourceLimiter : IResourceLimiter
    {
        private long _resourceCount;
        private object _lock = new object();
        private ManualResetEventSlim _mre = new ManualResetEventSlim(); // Dispose?
        private Timer _renewTimer;

        public long EstimatedCount => Interlocked.Read(ref _resourceCount);

        public RenewableResourceLimiter(long initialResourceCount, long newResourcePerSecond)
        {
            _resourceCount = initialResourceCount;

            // Start timer, yikes allocations from capturing
            _renewTimer = new Timer(o =>
            {
                var resource = o as RenewableResourceLimiter;

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
            private RenewableResourceLimiter _limiter;

            public Resource(long count, RenewableResourceLimiter limiter)
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

    internal class NonrenewableResourceLimiter : IResourceLimiter
    {
        private long _resourceCount;
        private readonly long _maxResourceCount;
        private object _lock = new object();
        private ManualResetEventSlim _mre = new ManualResetEventSlim(); // Dispose?

        public long EstimatedCount => Interlocked.Read(ref _resourceCount);

        public NonrenewableResourceLimiter(long initialResourceCount)
        {
            _resourceCount = initialResourceCount;
            _maxResourceCount = initialResourceCount;
            _mre = new ManualResetEventSlim();
        }
        public ValueTask<IResource> AcquireAsync(long requestedCount, bool minimumCount = false, CancellationToken cancellationToken = default)
        {
            if (requestedCount <= 0 || requestedCount > _maxResourceCount)
            {
                return ValueTask.FromResult<IResource>(new Resource(0, this));
            }

            if (EstimatedCount > requestedCount)
            {
                lock (_lock) // Check lock check
                {
                    if (EstimatedCount > requestedCount)
                    {
                        Interlocked.Add(ref _resourceCount, -requestedCount);
                        return ValueTask.FromResult<IResource>(new Resource(requestedCount, this));
                    }
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
            if (requestedCount <= 0 || requestedCount > _maxResourceCount || EstimatedCount < requestedCount)
            {
                return new Resource(0, this);
            }

            lock (_lock)
            {
                if (EstimatedCount > requestedCount)
                {
                    Interlocked.Add(ref _resourceCount, -requestedCount);
                    return new Resource(requestedCount, this);
                }
            }

            return new Resource(0, this);
        }

        private void Release(long count)
        {
            // Check for negative requestCount
            Interlocked.Add(ref _resourceCount, count);
            _mre.Set();
        }

        internal class Resource : IResource
        {
            private long _count;
            private NonrenewableResourceLimiter _limiter;

            public Resource(long count, NonrenewableResourceLimiter limiter)
            {
                _count = count;
                _limiter = limiter;
            }

            public long Count => _count;

            public void Dispose()
            {
                _limiter.Release(_count);
            }

            public void Release(long releaseCount)
            {
                if (releaseCount <= _count)
                {
                    _count -= releaseCount;
                    _limiter.Release(releaseCount);
                }
            }
        }
    }
    public class Startup
    {
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            // services.AddSingleton<IResourceStore, RedisResourceStore>();
            services.AddSingleton<IResourcePolicy, DefaultRateLimitPolicy>();
            services.AddSingleton<IResourcePolicy, ReadRateLimitPolicy>();
            services.AddSingleton<IResourcePolicy, WriteRateLimitPolicy>();
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
                    var manager = context.RequestServices.GetRequiredService<IResourceManager>();
                    var limiters = manager.GetLimiters(context);

                    var obtainedResources = new List<IResource>();

                    foreach (var limiter in limiters)
                    {
                        var resource = limiter.TryAcquire(1);

                        if (resource.Count == 0)
                        {
                            // fail
                            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                            foreach (var obtainedResource in obtainedResources)
                            {
                                obtainedResource.Dispose();
                            }
                            return;
                        }

                        obtainedResources.Add(resource);
                    }

                    try
                    {
                        await context.Response.WriteAsync("Hello World!");
                    }
                    finally
                    {
                        foreach (var obtainedResource in obtainedResources)
                        {
                            obtainedResource.Dispose();
                        }
                    }
                });
            });
        }
    }
}
