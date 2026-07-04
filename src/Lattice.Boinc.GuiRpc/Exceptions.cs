namespace Lattice.Boinc.GuiRpc;

/// <summary>The TCP connection failed or was closed. The connection is dead; reconnect.</summary>
public class BoincConnectionException : Exception
{
    public BoincConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A reply could not be parsed even after sanitizing. Carries the raw payload for diagnosis.</summary>
public class BoincProtocolException : Exception
{
    public string RawPayload { get; }

    public BoincProtocolException(string message, string rawPayload, Exception? inner = null)
        : base(message, inner) => RawPayload = rawPayload;
}

/// <summary>The daemon returned &lt;unauthorized/&gt;. Re-run AuthorizeAsync.</summary>
public class BoincUnauthorizedException : Exception
{
    public BoincUnauthorizedException() : base("The BOINC client rejected the request as unauthorized.") { }
}

/// <summary>The daemon returned an &lt;error&gt; tag. ErrorText is for display only — never branch on it.</summary>
public class BoincRpcException : Exception
{
    public string ErrorText { get; }

    public BoincRpcException(string errorText)
        : base($"The BOINC client returned an error: {errorText}") => ErrorText = errorText;
}
