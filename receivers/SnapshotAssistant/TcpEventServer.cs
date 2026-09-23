using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SnapshotAssistant;

public sealed class TcpEventServer : IAsyncDisposable
{
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptTask;

    public event Action<string>? RawMessageReceived;
    public event Action<string>? ServerError;
    public event Action? ClientConnected;
    public int BoundPort { get; private set; }
    public bool IsListening => _listener is not null;

    public TcpEventServer(int port = 8765)
    {
        _port = port;
    }

    public void Start()
    {
        if(_listener is not null)
            return;
        _cancellation = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        try
        {
            _listener.Start();
            BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptLoopAsync(_cancellation.Token);
        }
        catch
        {
            _listener = null;
            _cancellation.Dispose();
            _cancellation = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if(_listener is null)
            return;
        _cancellation?.Cancel();
        _listener.Stop();
        try
        {
            if(_acceptTask is not null)
                await _acceptTask.ConfigureAwait(false);
        }
        catch(OperationCanceledException)
        {
        }
        catch(ObjectDisposedException)
        {
        }
        _listener = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while(!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                ClientConnected?.Invoke();
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch(OperationCanceledException)
            {
                break;
            }
            catch(Exception ex) when(!cancellationToken.IsCancellationRequested)
            {
                ServerError?.Invoke(ex.Message);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using(client)
        using(var stream = client.GetStream())
        using(var memory = new MemoryStream())
        {
            try
            {
                var buffer = new byte[8192];
                while(memory.Length <= 4 * 1024 * 1024)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if(read == 0)
                        break;
                    memory.Write(buffer, 0, read);
                }
                if(memory.Length > 4 * 1024 * 1024)
                {
                    ServerError?.Invoke("收到的单条数据超过4MB，已跳过");
                    return;
                }
                if(memory.Length > 0)
                    RawMessageReceived?.Invoke(Encoding.UTF8.GetString(memory.ToArray()));
            }
            catch(OperationCanceledException)
            {
            }
            catch(Exception ex)
            {
                ServerError?.Invoke(ex.Message);
            }
        }
    }
}
