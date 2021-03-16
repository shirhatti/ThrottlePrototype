using System;
using System.Collections.Concurrent;
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
#region Rate limit APIs
    // Self replenishing resource
    public interface IRateLimiter
    {
        // an inaccurate view of resources
        long EstimatedCount { get; }

        // Fast synchronous attempt to acquire resources, it won't actually acquire the resource
        bool TryAcquire(long requestedCount);

        // Wait until the requested resources are available
        ValueTask<bool> AcquireAsync(long requestedCount, CancellationToken cancellationToken = default);
    }

    internal class SelfCountingRateLimiter : IRateLimiter
    {
        private long _resourceCount;
        private readonly long _maxResourceCount;
        private readonly long _newResourcePerSecond;
        private Timer _renewTimer;
        private readonly ConcurrentQueue<RateLimitRequest> _queue = new ConcurrentQueue<RateLimitRequest>();

        public long EstimatedCount => Interlocked.Read(ref _resourceCount);

        public SelfCountingRateLimiter(long resourceCount, long newResourcePerSecond)
        {
            _resourceCount = resourceCount;
            _maxResourceCount = resourceCount; // Another variable for max resource count?
            _newResourcePerSecond = newResourcePerSecond;

            // Start timer, yikes allocations from capturing
            _renewTimer = new Timer(Replenish, this, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public bool TryAcquire(long requestedCount)
        {
            if (Interlocked.Add(ref _resourceCount, -requestedCount) >= 0)
            {
                return true;
            }

            Interlocked.Add(ref _resourceCount, requestedCount);
            return false;
        }

        public ValueTask<bool> AcquireAsync(long requestedCount, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Add(ref _resourceCount, -requestedCount) >= 0)
            {
                return ValueTask.FromResult(true);
            }

            Interlocked.Add(ref _resourceCount, requestedCount);

            var registration = new RateLimitRequest(requestedCount);

            if (WaitHandle.WaitAny(new[] {registration.MRE.WaitHandle, cancellationToken.WaitHandle}) == 0)
            {
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }

        private static void Replenish(object? state)
        {
            // Return if Replenish already running to avoid concurrency.

            var limiter = state as SelfCountingRateLimiter;

            if (limiter == null)
            {
                return;
            }

            if (limiter._resourceCount < limiter._maxResourceCount)
            {
                var resourceToAdd = Math.Min(limiter._newResourcePerSecond, limiter._maxResourceCount - limiter._resourceCount);
                Interlocked.Add(ref limiter._resourceCount, resourceToAdd);
            }

            // Process queued requests
            var queue = limiter._queue;
            while(queue.TryPeek(out var request))
            {
                if (Interlocked.Add(ref limiter._resourceCount, -request.Count) >= 0)
                {
                    // Request can be fulfilled
                    queue.TryDequeue(out var requestToFulfill);

                    if (requestToFulfill == request)
                    {
                        // If requestToFulfill == request, the fulfillment is successful.
                        requestToFulfill.MRE.Set();
                    }
                    else
                    {
                        // If requestToFulfill != request, there was a concurrent Dequeue:
                        // 1. Reset the resource count.
                        // 2. Put requestToFulfill back in the queue (no longer FIFO) if not null
                        Interlocked.Add(ref limiter._resourceCount, request.Count);
                        if (requestToFulfill != null)
                        {
                            queue.Enqueue(requestToFulfill);
                        }
                    }
                }
                else
                {
                    // Request cannot be fulfilled
                    Interlocked.Add(ref limiter._resourceCount, request.Count);
                    break;
                }
            }
        }

        private class RateLimitRequest
        {
            public RateLimitRequest(long count)
            {
                Count = count;
                MRE = new ManualResetEventSlim();
            }

            public long Count { get; }

            public ManualResetEventSlim MRE { get; }
        }
    }

    public interface IResourceStore
    {
        // Read the resource count for a given resourceId
        ValueTask<long> GetCountAsync(string resourceId);

        // Update the resource count for a given resourceId
        ValueTask<long> IncrementCountAsync(string resourceId, long amount);
    }

#endregion

#region Rule engine APIs
    public interface IRateLimitPolicy
    {
        // Empty string if the policy is not applicable
        IRateLimiter? GetRateLimiter(HttpContext context); // Use another format for context
    }

    public class ReadRateLimitByIPPolicy : IRateLimitPolicy
    {
        private int _ipBuckets = 16;
        private readonly SelfCountingRateLimiter[] _readLimiters;

        public ReadRateLimitByIPPolicy(int numOfBuckets)
        {
            _ipBuckets = numOfBuckets;
            _readLimiters = new SelfCountingRateLimiter[_ipBuckets];

            for (var i = 0; i < _ipBuckets; i++)
            {
                _readLimiters[i] = new SelfCountingRateLimiter(5, 5);
            }
        }

        public IRateLimiter? GetRateLimiter(HttpContext context)
        {
            if (context.Request.Path != "/read")
            {
                return null;
            }

            var ipBucket = context.Connection.RemoteIpAddress?.GetAddressBytes()[0] / (byte.MaxValue / ipBuckets) ?? 0;

            return _readLimiters[ipBucket];
        }
    }

    public class DefaultRateLimitPolicy : IRateLimitPolicy
    {
        private SelfCountingRateLimiter _resourceLimiter = new SelfCountingRateLimiter(10, 5);

        public IRateLimiter? GetRateLimiter(HttpContext context) => _resourceLimiter;
    }

    public interface IRateLimitPolicyManager
    {
        IEnumerable<IRateLimiter> ResolveRateLimiters(HttpContext context); // Use another format for context
    }

    public class RateLimitPolicyManager : IRateLimitPolicyManager
    {
        private IEnumerable<IRateLimitPolicy> _policies;

        public RateLimitPolicyManager(IEnumerable<IRateLimitPolicy> policies)
        {
            _policies = policies;
        }

        public IEnumerable<IRateLimiter> ResolveRateLimiters(HttpContext context)
        {
            foreach (var policy in _policies)
            {
                var limiter = policy.GetRateLimiter(context);

                if (limiter != null)
                {
                    yield return limiter;
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
            services.AddSingleton<IRateLimitPolicy, DefaultRateLimitPolicy>();
            services.AddSingleton<IRateLimitPolicy, ReadRateLimitByIPPolicy>();
            services.AddSingleton<IRateLimitPolicyManager, RateLimitPolicyManager>();
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
                    var manager = context.RequestServices.GetRequiredService<IRateLimitPolicyManager>();
                    var limiters = manager.ResolveRateLimiters(context);

                    // This can be an extension method
                    foreach (var limiter in limiters)
                    {
                        if (!limiter.TryAcquire(1))
                        {
                            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                            return;
                        }
                    }

                    await context.Response.WriteAsync("Hello World!");
                });
            });
        }
    }
}
