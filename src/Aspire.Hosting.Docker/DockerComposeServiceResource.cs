// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Represents a compute resource for Docker Compose with strongly-typed properties.
/// </summary>
public class DockerComposeServiceResource(string name, IResource resource, DockerComposeEnvironmentResource composeEnvironmentResource) : Resource(name), IResourceWithParent<DockerComposeEnvironmentResource>
{
    /// <summary>
    /// Most common shell executables used as container entrypoints in Linux containers.
    /// These are used to identify when a container's entrypoint is a shell that will execute commands.
    /// </summary>
    private static readonly HashSet<string> s_shellExecutables = new(StringComparer.OrdinalIgnoreCase)
        {
            "/bin/sh",
            "/bin/bash",
            "/sh",
            "/bash",
            "sh",
            "bash",
            "/usr/bin/sh",
            "/usr/bin/bash"
        };

    internal bool IsShellExec { get; private set; }

    internal record struct EndpointMapping(
        IResource Resource,
        string Scheme,
        string Host,
        string InternalPort,
        int? ExposedPort,
        bool IsExternal,
        string EndpointName);

    /// <summary>
    /// Gets the resource that is the target of this Docker Compose service.
    /// </summary>
    internal IResource TargetResource => resource;

    /// <summary>
    /// Gets the collection of environment variables for the Docker Compose service.
    /// </summary>
    internal Dictionary<string, object> EnvironmentVariables { get; } = [];

    /// <summary>
    /// Gets the collection of commands to be executed by the Docker Compose service.
    /// </summary>
    internal List<object> Args { get; } = [];

    /// <summary>
    /// Gets the collection of volumes for the Docker Compose service.
    /// </summary>
    internal List<Volume> Volumes { get; } = [];

    /// <summary>
    /// Gets the mapping of endpoint names to their configurations.
    /// </summary>
    internal Dictionary<string, EndpointMapping> EndpointMappings { get; } = [];

    /// <inheritdoc/>
    public DockerComposeEnvironmentResource Parent => composeEnvironmentResource;

    internal Service BuildComposeService()
    {
        var composeService = new Service
        {
            Name = resource.Name.ToLowerInvariant(),
        };

        if (TryGetContainerImageName(TargetResource, out var containerImageName))
        {
            SetContainerImage(containerImageName, composeService);
        }

        SetContainerName(composeService);
        SetEntryPoint(composeService);
        AddEnvironmentVariablesAndCommandLineArgs(composeService);
        AddPorts(composeService);
        AddVolumes(composeService);
        SetDependsOn(composeService);
        return composeService;
    }

    private bool TryGetContainerImageName(IResource resourceInstance, out string? containerImageName)
    {
        // If the resource has a Dockerfile build annotation, we don't have the image name
        // it will come as a parameter
        if (resourceInstance.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _) || resourceInstance is ProjectResource)
        {
            containerImageName = this.AsContainerImagePlaceholder();
            return true;
        }

        return resourceInstance.TryGetContainerImageName(out containerImageName);
    }

    private void SetContainerName(Service composeService)
    {
        if (TargetResource.TryGetLastAnnotation<ContainerNameAnnotation>(out var containerNameAnnotation))
        {
            composeService.ContainerName = containerNameAnnotation.Name;
        }
    }

    private void SetEntryPoint(Service composeService)
    {
        if (TargetResource is ContainerResource { Entrypoint: { } entrypoint })
        {
            composeService.Entrypoint.Add(entrypoint);

            if (s_shellExecutables.Contains(entrypoint))
            {
                IsShellExec = true;
            }
        }
    }

    private void SetDependsOn(Service composeService)
    {
        if (TargetResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations))
        {
            foreach (var waitAnnotation in waitAnnotations)
            {
                // We can only wait on other compose services
                if (waitAnnotation.Resource is ProjectResource || waitAnnotation.Resource.IsContainer())
                {
                    // https://docs.docker.com/compose/how-tos/startup-order/#control-startup
                    composeService.DependsOn[waitAnnotation.Resource.Name.ToLowerInvariant()] = new()
                    {
                        Condition = waitAnnotation.WaitType switch
                        {
                            // REVIEW: This only works if the target service has health checks,
                            // revisit this when we have a way to add health checks to the compose service
                            // WaitType.WaitUntilHealthy => "service_healthy",
                            WaitType.WaitForCompletion => "service_completed_successfully",
                            _ => "service_started",
                        },
                    };
                }
            }
        }
    }

    private static void SetContainerImage(string? containerImageName, Service composeService)
    {
        if (containerImageName is not null)
        {
            composeService.Image = containerImageName;
        }
    }

    private void AddEnvironmentVariablesAndCommandLineArgs(Service composeService)
    {
        var env = new Dictionary<string, string>();

        foreach (var kv in EnvironmentVariables)
        {
            var value = this.ProcessValue(kv.Value);

            env[kv.Key] = value?.ToString() ?? string.Empty;
        }

        if (env.Count > 0)
        {
            foreach (var variable in env)
            {
                composeService.AddEnvironmentalVariable(variable.Key, variable.Value);
            }
        }

        var args = new List<string>();

        foreach (var arg in Args)
        {
            var value = this.ProcessValue(arg);

            if (value is not string str)
            {
                throw new NotSupportedException("Command line args must be strings");
            }

            args.Add(str);
        }

        if (args.Count > 0)
        {
            if (IsShellExec)
            {
                var sb = new StringBuilder();
                foreach (var command in args)
                {
                    // Escape any environment variables expressions in the command
                    // to prevent them from being interpreted by the docker compose CLI
                    EnvVarEscaper.EscapeUnescapedEnvVars(command, sb);
                    composeService.Command.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                composeService.Command.AddRange(args);
            }
        }
    }

    private void AddPorts(Service composeService)
    {
        if (EndpointMappings.Count == 0)
        {
            return;
        }

        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expose = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, mapping) in EndpointMappings)
        {
            // Resolve the internal port for the endpoint mapping
            var internalPort = mapping.InternalPort;

            if (mapping.IsExternal)
            {
                var exposedPort = mapping.ExposedPort?.ToString(CultureInfo.InvariantCulture);

                // No explicit exposed port, let docker compose assign a random port
                if (exposedPort is null)
                {
                    ports.Add(internalPort);
                }
                else
                {
                    // Explicit exposed port, map it to the internal port
                    ports.Add($"{exposedPort}:{internalPort}");
                }
            }
            else
            {
                // Internal endpoints use expose with just internalPort
                expose.Add(internalPort);
            }
        }

        composeService.Ports.AddRange(ports);
        composeService.Expose.AddRange(expose);
    }

    private void AddVolumes(Service composeService)
    {
        if (Volumes.Count == 0)
        {
            return;
        }

        foreach (var volume in Volumes)
        {
            composeService.AddVolume(volume);
        }
    }
}
