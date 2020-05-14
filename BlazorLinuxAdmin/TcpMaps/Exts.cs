using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualBasic;
using System.Runtime.InteropServices.ComTypes;

namespace BlazorLinuxAdmin.TcpMaps
{

    static class _Exts
    {
        static public void InitTcp(this Socket sock)
        {
            sock.UseOnlyOverlappedIO = true;
            sock.Blocking = false;
            sock.NoDelay = true;
            sock.ReceiveTimeout = 0;
            sock.SendTimeout = 0;
            sock.SendBufferSize = TcpMapService.DefaultBufferSize;
            sock.ReceiveBufferSize = TcpMapService.DefaultBufferSize * 2;
            
        }

        static public async Task ConnectWithTimeoutAsync(this Socket socket,string host,int port,int timeout)
        {
            Task tconn = socket.ConnectAsync(host, port);
            CancellationTokenSource cts = new CancellationTokenSource();
            Task twait = Task.Delay(timeout,cts.Token);
            await Task.WhenAny(tconn, twait);
            if (!tconn.IsCompleted)
                throw new TimeoutException();
            cts.Cancel();
        }

        static public void CloseSocket(this Socket socket)
        {
            try
            {
                //socket.Close();
                //socket.Disconnect(false);
                socket.Dispose();
            }
            catch(Exception x)
            {
                TcpMapService.OnError(x);
            }
        }
        static public Stream CreateStream(this Socket socket)
        {
            return new SimpleSocketStream(socket);
        }

        static public async Task<bool> WaitForSignalSettedAsync(this CancellationToken token,int timeout)
        {
            try
            {
                await Task.Delay(timeout, token);
            }
            catch
            {

            }
            return token.IsCancellationRequested;
        }

        static public T[] LockToArray<T>(this IEnumerable<T> coll)
        {
            lock (coll)
                return coll.ToArray();
        }
    }


    class SimpleSocketStream : Stream
    {
        Socket sock;
        public SimpleSocketStream(Socket socket)
        {
            sock = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArraySegment<byte> seg = new ArraySegment<byte>(buffer, offset, count);
            return await sock.ReceiveAsync(seg, SocketFlags.None, cancellationToken);//SocketFlags.Partial not OK in Linu
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArraySegment<byte> seg = new ArraySegment<byte>(buffer, offset, count);
            await sock.SendAsync(seg, SocketFlags.None, cancellationToken);//SocketFlags.Partial not OK in Linu
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return await sock.ReceiveAsync(buffer, SocketFlags.Partial, cancellationToken);
        }

        public override void Flush()
        {

        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }



        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }


        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

    }
}
