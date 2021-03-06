# Throttling APIs

## Terms

### Implementation Layer

The different levels of the library hierarchy. Currently consists of Core, Rule Engine and Consumption layers.

### Core layer

This layer contains the core abstractions for rate limit concerns including the rate limiter itself and the interface for rate limit count storage.

This layer is planned to be part of the BCL and will likely include some default implementations (sliding window, fixed window, etc) in-box. Implementations of the rate limit count store will likely not live in this layer.

### Rule Engine layer

This layer contains the functionalities that matches a request to the underlying rate limiters. This layer consists of the concepts such as policies, rules, and configuration.

This layer will be implemented using primitives from the Core Layer. Implementation examples include a new Rate Limit Middleware in ASP.NET Core, ACR, ATS and OneAccess.

### Consumption layer

This layer represent the user code. For example, an ASP.NET Core Web app or a service on Azure.

In simpler cases, this layer can directly use the Core layer, for example where the user wants to define a rate limit for a particular channel. In more complex scenarios, the user can opt into the more feature rich Rule Engine layer.

## Rate limiter

### Role

Users will interact with this component in order to obtain decisions for rate limiting, i.e. self replenishing resources. Non self-replenishing resources are deemed out of scope since they are better represented by the use of Semaphores (though waiting on more than one resource may be necessary in addition to `Semaphore` or `SemaphoreSlim`). This component encompasses the TryAcquire/AcquireAsync mechanics (i.e. check vs wait behaviours) and accounting method (fixed window, sliding window). This API should allow for a simple implementation that keeps an internal count of the underlying resource but also allow the use of an external resource count storage.

To keep the complexity low in this component, for bucketized policies (for example, rate limit by IP), each bucket will be represented by one limiter. The complexity of how bucketing is computed for a request will live in the policy component.

### Implementation Layer

Core Layer. Both this abstraction and some default implementations will be shipped in the BCL.

### Reference Designs

In ACR, the closes resembles the `IRateLimiter` interface.

```c#
public interface IRateLimiter
{
    /// <summary>
    /// Determines if the given request should be throttled or not as per the given policy.
    /// </summary>
    /// <param name="request">The request parameters that will be used for applying the policy.</param>
    /// <param name="policy">The rate limiting policy.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    Task<RateLimiterResponse> ProcessRequestAsync(
        IRequest request,
        IRateLimitPolicy policy,
        CancellationToken cancellationToken = default);
}

namespace RateLimiter.Core.Interfaces
{
    /// <summary>
    /// The set of parameters that define a request/user.
    /// </summary>
    public interface IRequest
    {
        /// <summary>
        /// The correlation Id of the request.
        /// </summary>
        string CorrelationId { get;  }

        /// <summary>
        /// The request Id which can represent a user/resource etc.
        /// </summary>
        string Id { get; }
    }
}

```

In OneAccess, this is close to the concept of `RateLimitClient`.

```c#
/// <summary>
/// Entry point into the rate limiting system
/// </summary>
public abstract class RateLimitClient
{
    /// <summary>
    /// Request a rate limiting quota for performing an action against a resource as described by the input parameter
    /// </summary>
    /// <param name="request">Describes the action and the target resource for which a rate limiting quota is being requested</param>
    /// <returns>Describes the response by the rate limiting system which will indicate if the action is approved or not</returns>
    public abstract Task<RateLimitResponse> GetDecisionAsync(RateLimitRequest request);
}
```

In ATS, this looks like `IThrottleClient`:

```c#
public interface IThrottleClient
{
    ThrottleEnforcement[] GetDecision(string category);
    bool ReportActivity(string category, int value, string id);
}
```

### API prototype

Currently we are prototyping the following API:

```c#
public interface IRateLimiter
{
    // an inaccurate view of resources
    long EstimatedCount { get; }

    // Fast synchronous attempt to acquire resources, it won't actually acquire the resource
    bool TryAcquire(long requestedCount);

    // Wait until the requested resources are available
    ValueTask<bool> AcquireAsync(long requestedCount, CancellationToken cancellationToken = default);
}
```

Usage may look like:

```c#
endpoints.MapGet("/", async context =>
{
    if (!await _resourceLimiter.TryAcquire(1))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }
    await context.Response.WriteAsync("Hello World!");
}
```

### Open discussions

- Currently trying to keep the API as simple as possiblel.
  - This means returning a bool, representing if the acquisition is successful. Is there a reason to return more information?
  - Current iteration removed the option for partial acquisition, either all of the request count is acquired or nothing.

## Rate limit count store

### Role

This component's main responsibility is to store the actual count of the resouces. This component should allow for local storage such as an in-memory cache, or a remote storage such as redis. For remote storage, this component will likely need a local cache of the remote count also needs to account for the balance between optimism (i.e. speed) vs coherency (i.e. accuracy).

### Implementation Layer

Core Layer. Only abstraction will be shipped in the BCL.

### Reference Designs

In ACR, this looks like `IRateLimiterCounterStore`:

```c#
public interface IRateLimiterCounterStore
{
    /// <summary>
    /// Gets the counter with the given key
    /// </summary>
    /// <param name="counterId">The Id that represents the counter key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    Task<RateLimiterCounter> GetCounterAsync(
        string counterId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the list of counters with the given list of keys. This API allows for
    /// pipelining in some stores like Redis
    /// </summary>
    /// <param name="counterIds">The list of counter Ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public Task<RateLimiterCounter[]> GetCountersAsync(
        string[] counterIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the value at the given counter Id. If the counter Id doesn't exist, it will create
    /// a new counter with that Id.
    /// </summary>
    /// <param name="counterId">The counter Id.</param>
    /// <param name="expiryTimeSpan">The expiry of the counter that will be created if not already exists.</param>
    /// <param name="incrementValue">The increment value by which the counter has to be incremented.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    Task<RateLimiterCounter> IncrementAndGetAsync(
        string counterId,
        TimeSpan expiryTimeSpan,
        long incrementValue,
        CancellationToken cancellationToken = default);
}
```

In OneAccess, this is close to the concept of `IQuotaStore`.

```c#
internal interface IQuotaStore
{
    /// <summary>
    /// Reads the quota buckets for the requested ids asynchronously
    /// If the method executes successfully it will return a ReadQuotaResult which contains quotas for each id and a read timestamp.
    /// If a given id is not found an empty quota bucket will be created and returned.
    /// </summary>
    /// <param name="ids">Collection of ids of quota buckets to read</param>
    /// <param name="containerName">The container holding the quota counters</param>
    Task<ReadQuotaResult> ReadQuotaAsync(string[] ids, string containerName);

    /// <summary>
    /// Writes the quota buckets asynchronously
    /// </summary>
    /// <param name="quota">Collection of quotas to write</param>
    /// <param name="containerName">The container holding the quota counters</param>
    /// <returns></returns>
    Task<UpdateResult> WriteQuotaAsync(TargetQuota[] quota, string containerName);
}
```

### API prototype

```c#
    public interface IResourceStore
    {
        // Read the resource count for a given resourceId
        ValueTask<long> GetCountAsync(string resourceId);

        // Update the resource count for a given resourceId
        ValueTask<long> IncrementCountAsync(string resourceId, long amount);
    }
```

### Open discussions

Is there a better way to represent the resourceId than a string?

## Rate limit policies

### Role

The responsibilities of this component will probably include the settings for rules and limits. For rules, this may include specifying which types of requests the limits are applicable or how requests are to be bucketized. For limits, this will likely entail initial resource counts, soft caps, hard caps, throttling levels, and replentishment parameters (frequency and amount) for self-renewing resources. Potentially, this might need to be separated into different abstractions.

### Implementation layer

This is a concern of the rate limit rule engine which is above the rate limiter layer.

### Reference Designs

In ACR, this looks like `IRateLimitPolicy` and `IRateLimitPolicyRule`:

```c#
/// <summary>
/// The set of parameters that define a rate limiting policy.
/// </summary>
public interface IRateLimitPolicy
{
    /// <summary>
    /// The name of the policy.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The allowed limit value.
    /// </summary>
    long Limit { get; }

    /// <summary>
    /// The time period over which the limit is allowed.
    /// </summary>
    TimeSpan Period { get; }

    /// <summary>
    /// The number of windows the rate limit period needs to be sliced for sliding window rate limiting.
    /// </summary>
    int NumberOfWindows { get; }

    /// <summary>
    /// The type of counter store that should be used for this policy.
    /// </summary>
    IRateLimiterCounterStore CounterStore { get; }

    /// <summary>
    /// Depending on the counter store, a policy can have a channel that is configured to sync with global stores if needed.
    /// </summary>
    ISyncChannel<string> CounterSyncChannel { get; }
}

/// <summary>
/// The set of parameters that define a rate limit policy rule.
/// </summary>
public interface IRateLimitPolicyRule
{
    /// <summary>
    /// The priority index value that will be used when multiple policy rules match a request.
    /// The lower the index value the higher the priority of choosing a rule.
    /// </summary>
    int PriorityIndex { get; }

    /// <summary>
    /// The type of rate limit policy rule.
    /// </summary>
    /// <example>For request rate policy rules, it can be Request</example>
    /// <example>For bandwidth rate policy rules, it can be Ingress or Egress</example>
    /// <example>For burst rate policy rules, it can be Burst</example>
    string Type { get; }

    /// <summary>
    /// The name of the policy rule.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The Id of the request that will be used for matching the rules.
    /// </summary>
    /// <example>Hostname like "myregistry"</example>
    /// <example>IP address</example>
    string RequestId { get; }

    /// <summary>
    /// The list of Http methods that will be used for matching the rules.
    /// </summary>
    /// <example>["GET", "HEAD"]</example>
    string[] Methods { get; }

    /// <summary>
    /// The list of request URL path patterns that will be used for matching the rules.
    /// Each of the included path can follow simple glob style pattern with the below representations.
    /// "**" : arbitrary URL path depth
    /// "*" : wild card single path component in a URL.
    /// </summary>
    /// <example>["v1/**/_manifests", **/v2/_tags/*"]</example>
    string[] IncludedPaths { get; }

    /// <summary>
    /// Indicates if this rule is enabled or not.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// The service sku or tier of the request that will be used for matching the rules
    /// </summary>
    string ServiceTier { get; }

    /// <summary>
    /// The list of polcies that will be used if this rule is matched.
    /// </summary>
    IRateLimitPolicy[] Policies { get; }

    /// <summary>
    /// The type of counter store that should be used for the polices if this rule is matched.
    /// </summary>
    string CounterStoreType { get; }

    /// <summary>
    /// Indicates whether sync channel should be created for the policies if this rule is matched.
    /// </summary>
    bool IsSyncEnabled { get; }

    /// <summary>
    /// Etag for the rule that helps to identify changes to the rule.
    /// </summary>
    string ETag { get; }
}
```

In OneAccess, this is close to the concept of `RateLimitRule`,  `IRuleMatchingStrategy`, `IQuotaKeyGenerator` and `IQuotaRefillStrategy`.

```c#
[Serializable]
public class RateLimitRule : PolicyElement
{
    public string Name { get; set; }

    public RuleOverride? Override { get; set; }

    public TargetMatch TargetMatch { get; set; }

    public uint? PerSecond { get; set; }

    public uint? PerMinute { get; set; }

    public uint? PerHour { get; set; }

    public uint? PerDay { get; set; }

    public uint? PerWeek { get; set; }

    public uint? ThrottlingInSeconds { get; set; }
}
internal interface IRuleMatchingStrategy
{
    bool TryMatch(Target target, RateLimitRule rule, out KeyValuePair<string, string>[] matchingProperties);
}
internal interface IQuotaKeyGenerator
{
    string GenerateKey(string ruleName, KeyValuePair<string, string>[] matchingProperties);
}
internal interface IQuotaRefillStrategy
{
    void Refill(TargetQuota quota, RateLimitRule rule, long readTimeInTicks);
}
```

In ATS, this likely maps to the policies defined like:

```json
"policies": [
  {
      "category": "contoso.getstatus.peruserperthreadpersec",
      "windowDuration": "00:00:10",
      "thresholds": [
        {
          "minDecisionDuration": "00:00:05",
          "decisionType": "GetStatusPerUserPerThreadPerSecExceeded",
          "value": 100
        }
      ]
  },
  {
      "category": "contoso.getstatus.perthreadpersec",
      "windowDuration": "00:00:10",
      "thresholds": [
        {
          "minDecisionDuration": "00:00:05",
          "decisionType": "GetStatusPerThreadPerSecExceeded",
          "value": 1000
        }
      ]
  },
```

### API prototype

In ASP.NET Core, these could be represented by attributes on routes.

### Open discussions

## Configuration and management of policies

### Role

This component handles the management of policies defined by the previous component. Specifically, it should maintain a collection of active policies as defined in code or via a configuration source. For an incoming request, this component will be queried to obtain the relevant rate limits and potentially have additional functionality to try acquiring them.

### Implementation layer

This is a concern of the Rule Engine. This component is likely to be closely coupled to the rate limit policies.

### Reference Designs

The relevant concepts in ACR include `IPolicyRuleStore` and `IPolicyRuleService`:

```c#
/// <summary>
/// This interface defines methods that a policy rule store provides.
/// </summary>
public interface IPolicyRuleStore
{
    /// <summary>
    /// Get all the policy rules that are defined with the given type.
    /// </summary>
    /// <param name="policyType">The type of policy <see cref="IRateLimitPolicyRule.Type"/></param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list of policy rules.</returns>
    Task<IEnumerable<IRateLimitPolicyRule>> GetAllPolicyRulesAsync(string policyType, CancellationToken cancellationToken);

    /// <summary>
    /// Get the policy rule with the given type and name.
    /// </summary>
    /// <param name="policyType">The type of policy <see cref="IRateLimitPolicyRule.Type"/></param>
    /// <param name="policyName">The name of the policy <see cref="IRateLimitPolicyRule.Name"/></param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The policy rule with the given name.</returns>
    Task<IRateLimitPolicyRule> GetPolicyRuleAsync(string policyType, string policyName, CancellationToken cancellationToken);
}
/// <summary>
/// This interface defines the methods a policy rule service provides.
/// </summary>
public interface IPolicyRuleService
{
    /// <summary>
    /// Find all the polices that match the given set of request parameters.
    /// Searches all policy rules as defined by <see cref="IRateLimitPolicyRule"/> and match them against the request.
    /// If a rule matches the request, the polices configured for that rule will be returned.
    /// </summary>
    /// <param name="requestId"><see cref="IRateLimitPolicyRule.RequestId"/></param>
    /// <param name="requestMethod">The Http request method.</param>
    /// <param name="requestPath">The Htto request URL path</param>
    /// <param name="serviceTier"><see cref="IRateLimitPolicyRule.ServiceTier"/></param>
    /// <returns>If a rule matching the request is found, the polices configured for that rule will be returned.
    /// If no matching rule is found, the return value will be null.</returns>
    IRateLimitPolicy[] FindMatchingPolices(
        string requestId,
        string requestMethod,
        string requestPath,
        string serviceTier);

    /// <summary>
    /// Get rate limit policies configured for a rule with the given name.
    /// </summary>

    /// <param name="policyRuleName">The name of rule to get the policies.</param>
    /// <returns>All policies configured for the rule with the given name otherwise null.</returns>
    IRateLimitPolicy[] GetPoliciesForRule(string policyRuleName);

    /// <summary>
    /// The count of policy rules currently available.
    /// </summary>
    int PolicyRuleCount { get; }
}
```

In One Access, the `RateLimitClient` contains much of the logic for matching requests and policies. The policies are contained in the `RateLimitRequest`:

```c#
// TODO: Consider allowing clients to specify negative cost to enable reverting/refilling quota consumption in certain rollbacks scenarios.
/// <summary>
/// Defines a request to the rate limiting system
/// </summary>
[Serializable]
public sealed class RateLimitRequest
{
    /// <summary>
    /// The policy ruleset to evaluate targets against.
    /// </summary>
    public RateLimitRule[] Rules { get; set; }

    /// <summary>
    /// The target(s) of a rate limiting policy request. Represents resource(s) protected by rate limiting rules
    /// </summary>
    public Target Target { get; set; }

    /// <summary>
    /// The name of the container holding target quota counters
    /// </summary>
    public string Container { get; set; }
}
```

Part of the rule maching logic is represented by the interface `IRuleMatchingStrategy`:

```c#
internal interface IRuleMatchingStrategy
{
    bool TryMatch(Target target, RateLimitRule rule, out KeyValuePair<string, string>[] matchingProperties);
}
```

Configuration for ATS is represented in `Microsoft.OneAccess.Policy.Configuration` namespace.

### API prototype

In ASP.NET Core, the current thought is to rely on the routing component.

### Open discussions

## Diagnostics

### Role

The idea here is to provide logs and diagnostic information to extract information such as what resources are throttled, how often resources are requested, etc.

### Implementation layer

This is a higher level concern and should be implemented in the layer where the Rate Limiter API is consumed. Additional logging could also be added in the implementation of the rate limit count storage.

### Reference Designs

In ACR, there are two main interfaces that are used for logging such `ILogger` and `IRequestLogger`:

```c#
public interface ILogger
{
    void LogInfo(string message, string correlationId = default);

    void LogDebug(string message, string correlationId = default);

    void LogError(string message, string detail = default, string correlationId = default);

    void LogWarning(string message, string detail = default, string correlationId = default);
}
/// <summary>
/// An inetrface for logging request/response.
/// </summary>
internal interface IRequestLogger : ILogger
{
    /// <summary>
    /// Logs the request/response.
    /// </summary>
    /// <param name="requestMarker">The request marker that identifies start or end of the request.</param>
    /// <param name="correlationId">The correlation Id.</param>
    /// <param name="requestHost">The request host.</param>
    /// <param name="responseStatus">The response status.</param>
    /// <param name="extraData">The extended data.</param>
    /// <param name="responseDurationInMs">The response duration in ms.</param>
    void LogRequest(
        string requestMarker,
        string correlationId,
        string requestHost,
        int responseStatus = default,
        string extraData = default,
        long responseDurationInMs = default);
}
```

For how these are implemented, consult `GenevaLogger`.

In ATS, here's a document of how to work with metrics and logs: https://eng.ms/docs/products/azure-common-building-blocks/azure-throttling-solution/reference/metrics-and-logs.

### API prototype

NA

### Open discussions

## Experimental implementations

1. Pipelines writer in dotnet/runtimes
2. Channel writer in dotnet/runtimesd
3. Rate limit on IP/users in dotnet/aspnetcore
   1. Leaky bucket
4. Concurrency middleware in dotnet/aspnetcore
   1. 50 concurrent requests, with a queue of 100
   2. Support other policy
