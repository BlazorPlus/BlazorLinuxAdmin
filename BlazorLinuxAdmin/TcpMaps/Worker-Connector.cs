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

			CommandMessage connmsg = new CommandMessage("ConnectorConnect", Connector.ServerPort.ToString(),"USB");
			await serverSocket.SendAsync(connmsg.Pack(), SocketFlags.None);

			connmsg = await CommandMessage.ReadFromSocketAsync(serverSocket);

			LogMessage("Warning:connmsg : " + connmsg);

			TcpMapConnectorSession session = new TcpMapConnectorSession(new SimpleSocketStream(tcpSocket));
			Stream clientStream = new SimpleSocketStream(serverSocket);
			await session.DirectWorkAsync(clientStream);

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
