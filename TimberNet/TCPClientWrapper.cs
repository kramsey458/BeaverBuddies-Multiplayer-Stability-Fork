using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TimberNet
{
    public class TCPClientWrapper : ISocketStream, ITransportInfo, IBlockingWrites
    {
        public readonly string? address;
        public readonly int port;
        private readonly TcpClient client;

        public int MaxChunkSize => 8192 * 4; // 32K
        public int MaxBytesPerSecond => 1024 * 1024; // 1 MB/s

        public string? Name => null;

        public string TransportName => "Direct";

        public TCPClientWrapper(string address, int port) 
        {
            client = new TcpClient();
            TurnOffNagle(client);
            this.address = address;
            this.port = port;
        }

        public TCPClientWrapper(TcpClient client)
        {
            this.client = client;
            TurnOffNagle(client);
            address = null;
            port = 0;
        }

        public bool Connected => client.Connected;

        /// <summary>True when each frame is sent as soon as it is written (TCP_NODELAY).</summary>
        public bool NoDelay => client.NoDelay;

        // A tick's events and the ping probes are small frames that should leave at once. With Nagle's algorithm on
        // (the default), a small write waits until everything sent before it is acknowledged, which the receiver
        // may delay by up to about 200 ms. Steam's path already sends without it (ReliableNoNagle). Set on the
        // socket this wrapper connects (before it connects; the connection keeps it) and on every accepted one.
        private static void TurnOffNagle(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
            }
            // Only a matter of latency: never a reason to refuse a connection.
            catch (Exception e) when (e is SocketException || e is ObjectDisposedException) { }
        }


        public Task ConnectAsync()
        {
            if (address == null)
            {
                throw new Exception("Client was initialized without an address.");
            }
            return client.ConnectAsync(address, port);
        }

        public void Close()
        {
            try
            {
                client.GetStream().Close();
                client.Close();
            }
            catch { }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!client.Connected)
            {
                throw new InvalidOperationException("Client is not connected");
            }
            return client.GetStream().Read(buffer, offset, count);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (count > MaxChunkSize)
            {
                throw new ArgumentException($"Count {count} exceeds max chunk size {MaxChunkSize}");
            }
            if (!client.Connected)
            {
                throw new InvalidOperationException("Client is not connected");
            }
            client.GetStream().Write(buffer, offset, count);
        }
    }
}
