namespace BS3D.Api.AdminWeb;

/// <summary>What one run of the admin page (issue #5) works with. Never read from configuration that could bind a port.</summary>
public sealed class AdminWebOptions
{
    /// <summary>The live score database, opened read-only.</summary>
    public required string Database { get; init; }

    /// <summary>The service's ceiling tables, to list every board the service takes clears for.</summary>
    public required string CeilingsDirectory { get; init; }

    /// <summary>The loopback port. The only address the page ever listens on is 127.0.0.1.</summary>
    public int Port { get; init; } = 5001;

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>How long the printed link works.</summary>
    public TimeSpan LinkLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Wrong keys after which the link stops working.</summary>
    public int MaxWrongKeys { get; init; } = 5;

    /// <summary>The page stops after this long without the owner doing anything in it.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Where the page says what it is doing: the terminal that started it.</summary>
    public TextWriter Output { get; init; } = Console.Out;
}
