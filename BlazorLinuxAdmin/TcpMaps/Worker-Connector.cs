using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Net.Http;

namespace BlazorLinuxAdmin.TcpMaps
{
	public class TcpMapConnectorWorker : TcpMapBaseWorker
	{
		public TcpMapConnector Connector { get; set; }

		public bool IsListened { get; private set; }

		TcpListener _listener;
		CancellationTokenSource cts;

		public void StartWork()
		{
			if (Connector.IsDisabled)
				return;
			if (IsStarted)
				return;
			IsStarted = true;
			_ = WorkAsync();
		}

		async Task WorkAsync()
		{
			IsListened = false;
			LogMessage("ServerWorker WorkAsync start");
			try
			{
				if (this.Connector.License == null)
					throw new Exception("Miss License Key");

				int againTimeout = 500;
			StartAgain:
				_listener = new TcpListener(IPAddress.Any, Connector.LocalPort);
				try
				{
					_listener.Start();
				}
				catch (Exception x)
				{
					OnError(x);
					_listener = null;
					cts = new CancellationTokenSource();
					if (!IsStarted)
						return;
					if (againTimeout < 4) againTimeout = againTimeout * 2;
					if (await cts.Token.WaitForSignalSettedAsync(againTimeout))
						return;
					goto StartAgain;
				}
				IsListened = true;
				while (IsStarted)
				{
					var socket = await _listener.AcceptSocketAsync();

					LogMessage("Warning:accept socket " + socket.LocalEndPoint + "," + socket.RemoteEndPoint + " at " + DateTime.Now.ToString("HH:mm:ss.fff"));

					_ = Task.Run(async delegate
					{
						try
						{

							socket.InitTcp();
							await ProcessSocketAsync(socket);

						}
						catch (Exception x)
						{
							OnError(x);
						}
						finally
						{
							LogMessage("Warning:close socket " + socket.LocalEndPoint + "," + socket.RemoteEndPoint + " at " + DateTime.Now.ToString("HH:mm:ss.fff"));
							socket.CloseSocket();
						}
					});
				}
			}
			catch (Exception x)
			{
				if (IsStarted)
				{
					OnError(x);
				}
			}

			LogMessage("WorkAsync end");

			IsStarted = false;
			IsListened = false;

			if (_listener != null)
			{
				try
				{
					_listener.Stop();
				}
				catch (Exception x)
				{
					OnError(x);
				}
				_listener = null;
			}
		}

		async Task ProcessSocketAsync(Socket tcpSocket)
		{
			Socket serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			serverSocket.InitTcp();
			await serverSocket.ConnectAsync(Connector.ServerHost, 6022);

			bool supportEncrypt = false;
			byte[] clientKeyIV;

			CommandMessage connmsg = new CommandMessage();
			connmsg.Name = "ConnectorConnect";
			List<string> arglist = new List<string>();
			arglist.Add(this.Connector.License.Key);
			arglist.Add(this.Connector.ServerPort.ToString());
			byte[] encryptedKeyIV, sourceHash;
			Connector.License.GenerateSecureKeyAndHash(out clientKeyIV, out encryptedKeyIV, out sourceHash);
			arglist.Add(Convert.ToBase64String(encryptedKeyIV));
			arglist.Add(Convert.ToBase64String(sourceHash));
			arglist.Add(supportEncrypt ? "1" : "0");
			arglist.Add("USB");
			connmsg.Args = arglist.ToArray();

			await serverSocket.SendAsync(connmsg.Pack(), SocketFlags.None);

			connmsg = await CommandMessage.ReadFromSocketAsync(serverSocket);

			if(connmsg==null)
			{
				LogMessage("Warning:ConnectorWorker : remote closed connection.");
				return;
			}

			LogMessage("Warning:connmsg : " + connmsg);

			if (connmsg.Name != "ConnectOK")
			{
				return;
			}


			Stream _sread, _swrite;
			if (supportEncrypt)
			{
				Connector.License.OverrideStream(serverSocket.CreateStream(), clientKeyIV, out _sread, out _swrite);
			}
			else
			{
				_sread = _swrite = serverSocket.CreateStream();
			}

			TcpMapConnectorSession session = new TcpMapConnectorSession(new SimpleSocketStream(tcpSocket));
			await session.DirectWorkAsync(_sread, _swrite);

		}

		public void Stop()
		{
			if (!IsStarted) return;
			IsStarted = false;
			if (_listener != null)
			{
				var lis = _listener;
				_listener = null;
				lis.Stop();
			}
			if (cts != null) cts.Cancel();
			//close all clients/sessions
			//lock (_clients)
			//	foreach (var item in _clients.ToArray())
			//		item.Stop();
			//lock (_sessions)
			//	foreach (var item in _sessions.ToArray())
			//		item.Stop();
		}


	}
}
