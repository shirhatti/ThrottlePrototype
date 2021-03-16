using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

#nullable enable

// Ignore this file
// This document was included to illustrate which APIs are shared among different types of resources.
namespace Throttling2
{
    public interface IResourceLimiter
    {
        long EstimatedCount { get; }

        bool TryAcquire(long requestedCount);
    }

    public interface IReleasingRateLimiter : IResourceLimiter
    {
        void Release(long count);
    }

    public interface IWaitingRateLimiter : IResourceLimiter
    {
        ValueTask<bool> AcquireAsync(long requestedCount, CancellationToken cancellationToken = default);
    }

    public interface IWaitingRenewable : IResourceLimiter, IWaitingRateLimiter
    {
    }

    public interface IWaitingNonrenewable : IResourceLimiter, IReleasingRateLimiter, IWaitingRateLimiter
    {
    }

    public interface INoWaitNonrenewable : IResourceLimiter, IReleasingRateLimiter
    {
    }

    public interface INoWaitRenewable : IResourceLimiter
    {
    }

    public class WaitingRenewable : IWaitingRenewable
    {
        private long _resourceCount;
        private readonly long _maxResourceCount;
        private readonly long _newResourcePerSecond;
        private object _lock = new object();
        private ManualResetEventSlim _mre = new ManualResetEventSlim(); // How about a FIFO queue instead of randomness?
        private Timer _renewTimer;

        public long EstimatedCount => Interlocked.Read(ref _resourceCount);

        public WaitingRenewable(long initialResourceCount, long newResourcePerSecond)
        {
            _resourceCount = initialResourceCount;
            _maxResourceCount = initialResourceCount;
            _newResourcePerSecond = newResourcePerSecond;
            _renewTimer = new Timer(Replenish, this, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private void Replenish(object? state)
        {
            var resource = state as WaitingRenewable;

            if (resource == null)
            {
                return;
            }

            lock (resource._lock)
            {
                if (resource._resourceCount < resource._maxResourceCount)
                {
                    var resourceToAdd = System.Math.Min(resource._newResourcePerSecond, resource._maxResourceCount - _resourceCount);
                    Interlocked.Add(ref _resourceCount, resourceToAdd);
                    _mre.Set();
                }
            }
        }

        public ValueTask<bool> AcquireAsync(long requestedCount, CancellationToken cancellationToken = default)
        {
            if (requestedCount < 0 || requestedCount > _maxResourceCount)
            {
                return ValueTask.FromResult(false);
            }

            if (requestedCount == 0)
            {
                return ValueTask.FromResult(true);
            }

            if (EstimatedCount > requestedCount)
            {
                lock (_lock) // Check lock check
                {
                    if (EstimatedCount > requestedCount)
                    {
                        Interlocked.Add(ref _resourceCount, -requestedCount);
                        return ValueTask.FromResult(true);
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
                        return ValueTask.FromResult(true);
                    }
                }
            }
        }
    }

    public class NoWaitRenewable : INoWaitRenewable
    {
        private long _resourceCount;
        private readonly long _maxResourceCount;
        private readonly long _newResourcePerSecond;
        private object _lock = new object();
        private Timer _renewTimer;

        public long EstimatedCount => Interlocked.Read(ref _resourceCount);

        public NoWaitRenewable(long initialResourceCount, long newResourcePerSecond)
        {
            _resourceCount = initialResourceCount;
            _maxResourceCount = initialResourceCount;
            _newResourcePerSecond = newResourcePerSecond;
            _renewTimer = new Timer(Replenish, this, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private void Replenish(object? state)
        {
            var resource = state as NoWaitRenewable;

            if (resource == null)
            {
                return;
            }

            lock (resource._lock)
            {
                if (resource._resourceCount < resource._maxResourceCount)
                {
                    var resourceToAdd = System.Math.Min(resource._newResourcePerSecond, resource._maxResourceCount - _resourceCount);
                    Interlocked.Add(ref _resourceCount, resourceToAdd);
                }
            }
        }

        public bool TryAcquire(long requestedCount)
        {
            if (requestedCount < 0 || requestedCount > _maxResourceCount)
            {
                return false;
            }

            if (requestedCount == 0)
            {
                return true;
            }

            if (EstimatedCount > requestedCount)
            {
                lock (_lock) // Check lock check
                {
                    if (EstimatedCount > requestedCount)
                    {
                        Interlocked.Add(ref _resourceCount, -requestedCount);
                        return true;
                    }
                }
            }

            return false;
        }
    }

    internal class WaitingNonrenewable : IWaitingNonrenewable
    {
        private long _resourceCount;
        private readonly long _maxResourceCount;
        private object _lock = new object();
        private ManualResetEventSlim _mre; // How about a FIFO queue instead of randomness?

        public long EstimatedCount => Interlocked.Read(ref _resourceCount);

        public WaitingNonrenewable(long initialResourceCount)
        {
            _resourceCount = initialResourceCount;
            _maxResourceCount = initialResourceCount;
            _mre = new ManualResetEventSlim();
        }

        public void Release(long consumedCount)
        {
            // Check for negative requestCount
            Interlocked.Add(ref _resourceCount, consumedCount);
            _mre.Set();
        }

        public ValueTask<bool> AcquireAsync(long requestedCount, CancellationToken cancellationToken = default)
        {
            if (requestedCount < 0 || requestedCount > _maxResourceCount)
            {
                return ValueTask.FromResult(false);
            }

            if (requestedCount == 0)
            {
                return ValueTask.FromResult(true);
            }

            if (EstimatedCount > requestedCount)
            {
                lock (_lock) // Check lock check
                {
                    if (EstimatedCount > requestedCount)
                    {
                        Interlocked.Add(ref _resourceCount, -requestedCount);
                        return ValueTask.FromResult(true);
                    }
                }
            }

            // Handle cancellation
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
                        return ValueTask.FromResult(true);
                    }
                }
            }
        }
    }

    internal class NoWaitNonrenewable : INoWaitNonrenewable
    {
        private long _resourceCount;
        private readonly long _maxResourceCount;
        private object _lock = new object();

        public long EstimatedCount => Interlocked.Read(ref _resourceCount);

        public NoWaitNonrenewable(long initialResourceCount)
        {
            _resourceCount = initialResourceCount;
            _maxResourceCount = initialResourceCount;
        }

        public void Release(long consumedCount)
        {
            // Check for negative requestCount
            Interlocked.Add(ref _resourceCount, consumedCount);
        }

        public bool TryAcquire(long requestedCount)
        {
            if (requestedCount < 0 || requestedCount > _maxResourceCount)
            {
                return false;
            }

            if (requestedCount == 0)
            {
                return true;
            }

            if (EstimatedCount > requestedCount)
            {
                lock (_lock) // Check lock check
                {
                    if (EstimatedCount > requestedCount)
                    {
                        Interlocked.Add(ref _resourceCount, -requestedCount);
                        return true;
                    }
                }
            }

            return false;
        }
    }

    public class Startup
    {
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            var allLimiter = new NoWaitRenewable(initialResourceCount: 10, newResourcePerSecond: 5);
            var readLimiter = new WaitingRenewable(initialResourceCount: 5, newResourcePerSecond: 5);
            var ipBuckets = 16;
            var writeLimiters = new NoWaitNonrenewable[ipBuckets];

            for (var i = 0; i < ipBuckets; i++)
            {
                writeLimiters[i] = new NoWaitNonrenewable(1);
            }

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", async context =>
                {
                    // Limiter usage should be based on Routing

                    if (!allLimiter.TryAcquire(1))
                    {
                        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        return;
                    }

                    if (context.Request.Path == "/read")
                    {
                        await readLimiter.AcquireAsync(1);
                        await context.Response.WriteAsync("Read Op");
                        return;
                    }

                    if (context.Request.Path == "/write")
                    {
                        var ipBucket = context.Connection.RemoteIpAddress?.GetAddressBytes()[0] / (byte.MaxValue / ipBuckets) ?? 0;

                        if (!writeLimiters[ipBucket].TryAcquire(1))
                        {
                            try
                            {
                                await context.Response.WriteAsync("Write Op");
                            }
                            finally
                            {
                                writeLimiters[ipBucket].Release(1);
                            }
                        }
                    }

                    await context.Response.WriteAsync("Other Op");
                });
            });
        }
    }
}
