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
using BlazorLinuxAdmin.TcpMaps.UDP;

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

		DateTime _stopUseRouterClientPortUntil;
		DateTime _stopUseUDPPunchingUntil;

		async Task ProcessSocketAsync(Socket tcpSocket)
		{
		TryAgain:

			string connmode = null;
			if (Connector.UseRouterClientPort && DateTime.Now > _stopUseRouterClientPortUntil)
			{
				connmode = "RCP";
			}
			else if (Connector.UseUDPPunching && DateTime.Now > _stopUseRouterClientPortUntil)
			{
				connmode = "UDP";
			}
			else if (Connector.UseServerBandwidth)
			{
				connmode = "USB";
			}
			else
			{
				//TODO:  try ..
				if (Connector.UseUDPPunching)
					connmode = "UDP";
				else if (Connector.UseRouterClientPort)
					connmode = "RCP";
				else
					throw new Exception("No connection mode.");
			}


			Task<KeyValuePair<string, UDPClientListener>> task2 = null;

			if (connmode == "UDP")
			{
				//TODO: shall cache the UDPClientListener ...
				task2 = Task.Run(GetUdpClientAsync);
			}

			Socket serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			serverSocket.InitTcp();
			await serverSocket.ConnectAsync(Connector.ServerHost, 6022);

			string connArgument = null;
			UDPClientListener udp = null;
			if (task2 != null)
			{
				try
				{
					var kvp = await task2;
					connArgument = kvp.Key;
					udp = kvp.Value;

				}
				catch (Exception x)
				{
					OnError(x);
					LogMessage("UDP Failed , switch ...");
					if (Connector.UseServerBandwidth)
					{
						connmode = "USB";
					}
					else
					{
						throw new Exception(x.Message, x);
					}
				}
			}

			bool supportEncrypt = Connector.UseEncrypt;
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
			arglist.Add(connmode);
			arglist.Add(connArgument);
			connmsg.Args = arglist.ToArray();

			await serverSocket.SendAsync(connmsg.Pack(), SocketFlags.None);

			connmsg = await CommandMessage.ReadFromSocketAsync(serverSocket);

			if (connmsg == null)
			{
				LogMessage("Warning:ConnectorWorker : remote closed connection.");
				return;
			}

			//LogMessage("Warning:connmsg : " + connmsg);

			if (connmsg.Name != "ConnectOK")
			{
				return;
			}

			//TODO: add to session list

			if (supportEncrypt && connmsg.Args[1] == "0")
			{
				supportEncrypt = false; LogMessage("Warning:server don't support encryption : " + Connector.ServerHost);
			}

			Stream _sread, _swrite;

			if (connmode == "RCP")
			{
				serverSocket.CloseSocket();

				string ip = connmsg.Args[2];
				int port = int.Parse(connmsg.Args[3]);
				if (port < 1)
				{
					LogMessage("Error:Invalid configuration , remote-client-side don't provide RouterClientPort , stop use RCP for 1min");
					_stopUseRouterClientPortUntil = DateTime.Now.AddSeconds(60);
					goto TryAgain;//TODO: reuse the serverSocket and switch to another mode 
				}

				LogMessage("Warning:" + tcpSocket.LocalEndPoint + " forward to " + ip + ":" + port);
				await tcpSocket.ForwardToAndWorkAsync(ip, port);

				return;
			}


			if (connmode == "UDP")
			{
				LogMessage("MY UDP..." + connArgument + " REMOTE..." + connmsg.Args[2]);

				string mynat = connArgument;
				string[] pair = connmsg.Args[2].Split(':');

				serverSocket.CloseSocket();

				try
				{
					UDPClientStream stream = await UDPClientStream.ConnectAsync(udp, pair[0], int.Parse(pair[1]), TimeSpan.FromSeconds(6));

					_sread = stream;
					_swrite = stream;

					LogMessage("UDP Connected #" + stream.SessionId + " " + connArgument + " .. " + connmsg.Args[2]);
				}
				catch (Exception x)
				{
					LogMessage("UDP ERROR " + connArgument + " .. " + connmsg.Args[2] + " " + x.Message);
					throw;
				}
			}
			else
			{
				if (supportEncrypt)
				{
					Connector.License.OverrideStream(serverSocket.CreateStream(), clientKeyIV, out _sread, out _swrite);
				}
				else
				{
					_sread = _swrite = serverSocket.CreateStream();
				}
			}

			TcpMapConnectorSession session = new TcpMapConnectorSession(new SimpleSocketStream(tcpSocket));
			await session.DirectWorkAsync(_sread, _swrite);

		}

		string _lastnat;
		UDPClientListener _lastudp;
		DateTime _timeudp;
		CancellationTokenSource _ctsudpnew;

		async Task<KeyValuePair<string, UDPClientListener>> GetUdpClientAsync()
		{
			if (!Connector.UDPCachePort)
				return await CreteUdpClientAsync();

			TryAgain:

			if (_lastudp != null && DateTime.Now - _timeudp < TimeSpan.FromSeconds(8))
			{
				lock (this)
				{
					return new KeyValuePair<string, UDPClientListener>(_lastnat, _lastudp);
				}
			}

			bool ctsCreated = false;
			CancellationTokenSource cts = _ctsudpnew;
			if (cts == null)
			{
				lock (this)
				{
					if (_ctsudpnew == null)
					{
						_ctsudpnew = new CancellationTokenSource();
						ctsCreated = true;
					}
					cts = _ctsudpnew;
				}
			}

			if (!ctsCreated)
			{
				await cts.Token.WaitForSignalSettedAsync(3000);
				goto TryAgain;
			}

			try
			{
				//TODO: cache the port and reuse.

				var kvp = await CreteUdpClientAsync();

				lock (this)
				{
					_lastnat = kvp.Key;
					_lastudp = kvp.Value;
					_timeudp = DateTime.Now;
				}

				return kvp;
			}
			finally
			{
				lock (this)
				{
					cts.Cancel();
					_ctsudpnew = null;
				}
			}


		}

		async Task<KeyValuePair<string, UDPClientListener>> CreteUdpClientAsync()
		{
			UdpClient udp = new UdpClient();

			udp.Client.ReceiveTimeout = 4321;
			udp.Client.SendTimeout = 4321;
			udp.Send(System.Text.Encoding.ASCII.GetBytes("whoami"), 6, Connector.ServerHost, 6023);

			LogMessage("Warning:udp.ReceiveAsync");

			var rr = await udp.ReceiveAsync();
			string exp = System.Text.Encoding.ASCII.GetString(rr.Buffer);

			LogMessage("Warning:udp get " + exp);

			if (!exp.StartsWith("UDP="))
				throw (new Exception("failed"));

			string natinfo = exp.Remove(0, 4);
			UDPClientListener udpc = new UDPClientListener(udp);

			return new KeyValuePair<string, UDPClientListener>(natinfo, udpc);
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
		}

	}


}
