// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using k8s;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using k8s.Models;
using k8s.Autorest;
using Json.Patch;

namespace Aspire.Hosting.Tests.Dcp;

internal sealed class TestKubernetesService : IKubernetesService
{
    // In user port range, but otherwise no particular reason to start with this value.
    // This is meant to help tests select ports that do not clash with ports auto-generated by TestKubernetesService.
    public const int StartOfAutoPortRange = 52000;

    public ConcurrentQueue<CustomResource> CreatedResources { get; } = [];
    public ConcurrentQueue<string> DeletedResources { get; } = [];

    private readonly List<Channel<(WatchEventType, CustomResource)>> _watchChannels = [];
    private readonly Func<CustomResource, string, Stream> _startStream;
    private readonly bool _ignoreDeletes;
    private int _nextPort = StartOfAutoPortRange;

    public TestKubernetesService(Func<CustomResource, string, Stream>? startStream = null, bool ignoreDeletes = false)
    {
        _startStream = startStream ?? ((obj, logStreamType) => new MemoryStream(Encoding.UTF8.GetBytes($"Logs for {obj.Metadata.Name} ({logStreamType})")));
        _ignoreDeletes = ignoreDeletes;
    }

    public Task<T> GetAsync<T>(string name, string? namespaceParameter = null, CancellationToken _ = default) where T : CustomResource
    {
        if (DeletedResources.Contains(name))
        {
            throw new HttpOperationException("Not found")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.NotFound }, "Not found")
            };
        }

        var res = CreatedResources.OfType<T>().FirstOrDefault(r =>
            r.Metadata.Name == name &&
            string.Equals(r.Metadata.NamespaceProperty ?? string.Empty, namespaceParameter ?? string.Empty)
        );
        if (res == null)
        {
            throw new ArgumentException($"Resource '{namespaceParameter ?? ""}/{name}' not found");
        }
        return Task.FromResult(res);
    }

    public Task<T> CreateAsync<T>(T obj, CancellationToken cancellationToken = default) where T : CustomResource
    {
        static T Clone(T r)
        {
            var serialized = JsonSerializer.Serialize(r);
            var clone = JsonSerializer.Deserialize<T>(serialized);
            return clone!;
        }

        var res = Clone(obj);

        // "Allocate" port for a service.
        if (res is Service svc)
        {
            if (svc.Status is null)
            {
                svc.Status = new ServiceStatus();
            }
            svc.Status.EffectiveAddress = svc.Spec.Address ?? "localhost";
            svc.Status.EffectivePort = svc.Spec.Port ?? Interlocked.Increment(ref _nextPort);
        }

        lock (CreatedResources)
        {
            CreatedResources.Enqueue(res);
            foreach (var c in _watchChannels)
            {
                c.Writer.TryWrite((WatchEventType.Added, res));
            }
        }

        return Task.FromResult(res);
    }

    public void PushResourceModified(CustomResource resource)
    {
        lock (CreatedResources)
        {
            foreach (var c in _watchChannels)
            {
                c.Writer.TryWrite((WatchEventType.Modified, resource));
            }
        }
    }

    public async Task<T> DeleteAsync<T>(string name, string? namespaceParameter = null, CancellationToken cancellationToken = default) where T : CustomResource
    {
        try
        {
            var resource = await GetAsync<T>(name, namespaceParameter, cancellationToken);
            if (!_ignoreDeletes)
            {
                DeletedResources.Enqueue(name);
            }
            return resource;
        }
        catch (Exception ex)
        {
            throw new HttpOperationException(ex.Message)
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.NotFound }, ex.Message)
            };
        }
    }

    public Task<List<T>> ListAsync<T>(string? namespaceParameter = null, CancellationToken cancellationToken = default) where T : CustomResource
    {
        var res = CreatedResources.OfType<T>().Where(r =>
            string.Equals(r.Metadata.NamespaceProperty ?? string.Empty, namespaceParameter ?? string.Empty)
        );
        return Task.FromResult(res.ToList());
    }

    public async IAsyncEnumerable<(WatchEventType, T)> WatchAsync<T>(string? namespaceParameter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : CustomResource
    {
        var chan = Channel.CreateUnbounded<(WatchEventType, CustomResource)>();

        lock (CreatedResources)
        {
            _watchChannels.Add(chan);
            foreach (var res in CreatedResources.OfType<T>())
            {
                chan.Writer.TryWrite((WatchEventType.Added, res));
            }
        }

        try
        {
            while (true)
            {
                var (evtType, res) = await chan.Reader.ReadAsync(cancellationToken);
                if (res is T tRes)
                {
                    yield return (evtType, tRes);
                }
            }
        }
        finally
        {
            lock (CreatedResources)
            {
                _watchChannels.Remove(chan);
            }
        }
    }

    public Task<Stream> GetLogStreamAsync<T>(
        T obj,
        string logStreamType,
        CancellationToken cancellationToken = default,
        bool? follow = true,
        bool? timestamps = false,
        bool? lineNumbers = false,
        long? limit = null,
        long? tail = null,
        long? skip = null
    ) where T : CustomResource
    {
        return Task.FromResult(_startStream(obj, logStreamType));
    }

    public Task<T> PatchAsync<T>(T obj, V1Patch patch, CancellationToken cancellationToken = default) where T : CustomResource
    {
        // Not a complete implementation, but Aspire is using patching only to stop resources,
        // so this is good enough.

        if (patch.Type == V1Patch.PatchType.JsonPatch)
        {
            Json.Patch.JsonPatch jsonPatch = (Json.Patch.JsonPatch)patch.Content;

            var res = CreatedResources.OfType<T>().FirstOrDefault(r =>
                r.Metadata.Name == obj.Metadata.Name &&
                string.Equals(r.Metadata.NamespaceProperty, obj.Metadata.NamespaceProperty)
            );
            if (res == null)
            {
                throw new ArgumentException($"Resource '{obj.Metadata.NamespaceProperty}/{obj.Metadata.Name}' not found");
            }

            var result = jsonPatch.Apply<T, T>(res);

            if (res is Executable exe && result is Executable eu)
            {
                if (eu.Spec.Stop == true)
                {
                    exe.Spec.Stop = true;
                    if (exe.Status is null)
                    {
                        exe.Status = new ExecutableStatus();
                    }
                    exe.Status.State = ExecutableState.Finished;
                }
            }

            if (res is Container ctr && result is Container cu)
            {
                if (cu.Spec.Stop == true)
                {
                    ctr.Spec.Stop = true;
                    if (ctr.Status is null)
                    {
                        ctr.Status = new ContainerStatus();
                    }
                    ctr.Status.State = ContainerState.Exited;
                }
            }

            return Task.FromResult(res);
        }

        // Fall back to doing noting.
        return Task.FromResult(obj);
    }

    public Task StopServerAsync(string resourceCleanup = "Full", CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        lock (CreatedResources)
        {
            foreach (var c in _watchChannels)
            {
                _ = c.Writer.TryComplete();
            }
        }

        return Task.CompletedTask;
    }

    public Task CleanupResourcesAsync(CancellationToken cancellationToken = default)
    {
        return StopServerAsync("Full", cancellationToken);
    }
}
