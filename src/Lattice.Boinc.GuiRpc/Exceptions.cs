namespace Lattice.Boinc.GuiRpc;

/// <summary>The TCP connection failed or was closed. The connection is dead; reconnect.</summary>
public class BoincConnectionException : Exception
{
    /// <summary>Creates the exception, optionally wrapping the underlying IO failure.</summary>
    public BoincConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A reply could not be parsed even after sanitizing. Carries the raw payload for diagnosis.</summary>
public class BoincProtocolException : Exception
{
    /// <summary>The unparseable reply text (truncated for very large replies).</summary>
    public string RawPayload { get; }

    /// <summary>Creates the exception, capturing the offending payload.</summary>
    public BoincProtocolException(string message, string rawPayload, Exception? inner = null)
        : base(message, inner) => RawPayload = rawPayload;
}

/// <summary>The daemon returned &lt;unauthorized/&gt;. Re-run AuthorizeAsync.</summary>
public class BoincUnauthorizedException : Exception
{
    /// <summary>Creates the exception with a fixed message.</summary>
    public BoincUnauthorizedException() : base("The BOINC client rejected the request as unauthorized.") { }
}

/// <summary>The daemon returned an &lt;error&gt; tag. ErrorText is for display only — never branch on it.</summary>
public class BoincRpcException : Exception
{
    /// <summary>The daemon's error text, verbatim. Display only; wording changes between versions.</summary>
    public string ErrorText { get; }

    /// <summary>Creates the exception from the daemon's error text.</summary>
    public BoincRpcException(string errorText)
        : base($"The BOINC client returned an error: {errorText}") => ErrorText = errorText;
}
