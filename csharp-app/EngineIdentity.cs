using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Typryt;

/// <summary>
/// Who the engine is for this launch: a port nobody else is using and a random token. The app starts the engine with
/// both and talks to it only on that port with that token, so another program that happens to listen on a well-known
/// port, or that sends requests to the engine, is not mistaken for it. In test mode (the same switch as the test
/// input) TYPRYT_ENGINE_PORT and TYPRYT_ENGINE_TOKEN choose the port and the token.
/// </summary>
public static class EngineIdentity
{
    public const string TokenHeader = "X-Typryt-Token";
    public const string NonceHeader = "X-Typryt-Nonce";

    private static readonly object Gate = new();
    private static int _port;

    /// <summary>The token this launch uses. 64 hexadecimal characters from a cryptographic generator, unless the test switch sets it.</summary>
    public static string Token { get; } = ChooseToken();

    /// <summary>The port of the engine started now. Chosen again when an engine is started and the port is no longer free.</summary>
    public static int Port
    {
        get
        {
            lock (Gate)
            {
                if (_port == 0) _port = ChoosePort();
                return _port;
            }
        }
    }

    public static Uri BaseAddress => new($"http://127.0.0.1:{Port}/");

    /// <summary>Called before each engine start: keeps the port when it is still free, otherwise picks a free one.</summary>
    public static void PrepareForStart()
    {
        lock (Gate)
        {
            if (_port != 0 && IsFree(_port)) return;
            int previous = _port;
            _port = ChoosePort();
            if (previous != 0) Log.Write("engine", "the engine's port was in use, another free port is used");
        }
    }

    /// <summary>The answer a real engine gives to a health check with this nonce: it proves the engine knows the token.</summary>
    public static string ExpectedProof(string nonce)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Token));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();
    }

    private static string ChooseToken()
    {
        if (InputMarks.TestInput)
        {
            string? fromTest = Environment.GetEnvironmentVariable("TYPRYT_ENGINE_TOKEN");
            if (!string.IsNullOrWhiteSpace(fromTest)) return fromTest.Trim();
        }
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static int ChoosePort()
    {
        if (InputMarks.TestInput && int.TryParse(Environment.GetEnvironmentVariable("TYPRYT_ENGINE_PORT"), out int fromTest) && fromTest is > 0 and < 65536)
        {
            if (IsFree(fromTest)) return fromTest;
            Log.Write("engine", "the port set by the test switch is in use, a free port is used");
        }
        return FindFreePort();
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
