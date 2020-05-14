using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.IO;


namespace BlazorLinuxAdmin.TcpMaps
{
	// Intranet ClientWorker <-websocket-> TcpMapService ServerClient <-> ServerWorker <-tcp-> PublicInternet

	public class TcpMapClientWorker : TcpMapBaseWorker
	{
		public TcpMapClient Client { get; set; }

		public bool IsConnected { get; private set; }

		//System.Net.WebSockets.ClientWebSocket ws;
		Socket _socket;
		CancellationTokenSource _cts_connect;
		Stream _sread, _swrite;

		public void StartWork()
		{
			if (Client.IsDisabled)
				return;
			if (IsStarted)
				return;
			IsStarted = true;
			_ = WorkAsync();
		}


		async Task WorkAsync()
		{
			IsConnected = false;
			LogMessage("ClientWorker WorkAsync start");
			int connectedTimes = 0;
			try
			{
				int againTimeout = 125;
			StartAgain:

				_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				_socket.InitTcp();
				_cts_connect = new CancellationTokenSource();
				try
				{
					await _socket.ConnectAsync(Client.ServerHost, 6022);

					LogMessage("connected to 6022");
				}
				catch (Exception x)
				{
					OnError(x);
					_socket.CloseSocket();
					_socket = null;
					_cts_connect = new CancellationTokenSource();
					if (!IsStarted)
						return;
					if (againTimeout < 4) againTimeout = againTimeout * 2;
					if (await _cts_connect.Token.WaitForSignalSettedAsync(againTimeout))
						return;
					goto StartAgain;
				}

				try
				{
					bool supportEncrypt = false;
					byte[] clientKeyIV;

					{
						CommandMessage connmsg = new CommandMessage();
						connmsg.Name = "ClientConnect";
						List<string> arglist = new List<string>();
						arglist.Add(this.Client.License.Key);
						arglist.Add(this.Client.ServerPort.ToString());
						byte[] encryptedKeyIV, sourceHash;
						Client.License.GenerateSecureKeyAndHash(out clientKeyIV, out encryptedKeyIV, out sourceHash);
						arglist.Add(Convert.ToBase64String(encryptedKeyIV));
						arglist.Add(Convert.ToBase64String(sourceHash));
						arglist.Add(supportEncrypt ? "1" : "0");
						connmsg.Args = arglist.ToArray();

						await _socket.SendAsync(connmsg.Pack(), SocketFlags.None);

						//LogMessage("wait for conn msg");

						connmsg = await CommandMessage.ReadFromSocketAsync(_socket);

						if (connmsg == null)
						{
							TcpMapService.LogMessage("no message ? Connected:" + _socket.Connected);
							return;
						}

						//LogMessage("connmsg : " + connmsg.Name + " : " + string.Join(",", connmsg.Args));

						if (connmsg.Name != "ConnectOK")
						{
							IsStarted = false;//don't go until start it again.
							throw new Exception(connmsg.Name + " : " + string.Join(",", connmsg.Args));
						}

						if (connmsg.Args[1] == "0")
						{
							supportEncrypt = false;
						}

					}

					IsConnected = true;

					connectedTimes++;
					LogMessage("ConnectOK #" + connectedTimes);

					if (supportEncrypt)
					{
						Client.License.OverrideStream(_socket.CreateStream(), clientKeyIV, out _sread, out _swrite);
					}
					else
					{
						_sread = _swrite = _socket.CreateStream();
					}

					for (int i = 0; i < Math.Min(5, Client.PreSessionCount); i++)//TODO:const of 5
						_ = Task.Run(ProvidePreSessionAsync);

					_ = Task.Run(MaintainSessionsAsync);

					byte[] readbuff = new byte[TcpMapService.DefaultBufferSize];
					while (IsStarted)
					{
						CommandMessage msg;
						var cts = new CancellationTokenSource();
						_ = Task.Run(async delegate
						  {
							  if (await cts.Token.WaitForSignalSettedAsync(16000))
								  return;

							  await _swrite.WriteAsync(new CommandMessage("_ping_", "forread").Pack());
						  });
						try
						{
							msg = await CommandMessage.ReadFromStreamAsync(_sread);
						}
						finally
						{
							cts.Cancel();
						}

						if (msg == null)
						{
							TcpMapService.LogMessage("no message ? Connected:" + _socket.Connected);
							return;
						}

						//this.LogMessage("TcpMapClientWorker get msg " + msg);

						switch (msg.Name)
						{
							case "StartSession":
								Task.Run(async delegate
								{
									try
									{
										await DoStartSessionAsync(msg);
									}
									catch (Exception x)
									{
										OnError(x);
									}
								}).ToString();
								break;
							case "CloseSession":
								Task.Run(async delegate
								{
									try
									{
										await DoCloseSessionAsync(msg);
									}
									catch (Exception x)
									{
										OnError(x);
									}
								}).ToString();
								break;
							case "_ping_":
								await _swrite.WriteAsync(new CommandMessage("_ping_result_").Pack());
								break;
							case "_ping_result_":
								break;
							default:
								LogMessage("Error: 4 Ignore message " + msg);
								break;
						}
					}

				}
				catch (SocketException)
				{
					//no log
				}
				catch (Exception x)
				{
					OnError(x);
				}

				if (IsStarted)
				{
					_socket.CloseSocket();//logic failed..
					againTimeout = 125;
					goto StartAgain;
				}
			}
			catch (Exception x)
			{
				OnError(x);
			}

			IsStarted = false;
			IsConnected = false;

			if (_socket != null)
			{
				try
				{
					_socket.CloseSocket();
				}
				catch (Exception x)
				{
					OnError(x);
				}
				_socket = null;
			}
		}


		private async Task ProvidePreSessionAsync()
		{
			while (IsConnected)
			{
				try
				{
					var session = new TcpMapClientSession(Client, null);
					lock (_presessions)
						_presessions.Add(session);
					try
					{
						await session.WaitUpgradeAsync();
					}
					finally
					{
						lock (_presessions)
							_presessions.Remove(session);
					}
					if (session.SessionId != null)
					{
						sessionMap.TryAdd(session.SessionId, session);
						this.LogMessage("Warning:ClientWorker Session Upgraded  " + session.SessionId);
					}
					else
					{
						this.LogMessage("Warning:ClientWorker Session Closed ?  " + IsConnected +" , "+ session.SessionId);
					}
				}
				catch (Exception x)
				{
					OnError(x);
				}

				await Task.Yield();
				//await Task.Delay(2000);
			}
		}



		private async Task DoStartSessionAsync(CommandMessage msg)
		{
			string sid = msg.Args[1];
			if (sessionMap.TryRemove(sid, out var session))
			{
				session.Close();
			}

			session = new TcpMapClientSession(Client, sid);
			sessionMap.TryAdd(sid, session);
			try
			{
				await session.StartAsync();
				msg.Args[1] = "OK";
			}
			catch (Exception x)
			{
				OnError(x);
				msg.Args[2] = "Error";
			}
			msg.Name = "StartSessionResult";
			this.LogMessage("TcpMapClientWorker sending " + msg);
			await _swrite.WriteAsync(msg.Pack());
		}

		private async Task DoCloseSessionAsync(CommandMessage msg)
		{
			string sid = msg.Args[1];
			if (sessionMap.TryGetValue(sid, out var session))
			{
				session.Close();
			}
			msg.Name = "CloseSessionResult";

			//TODO: whether the WriteAsync OK for concurrent calling?
			await _swrite.WriteAsync(msg.Pack());
			//lock(swrite)swrite.Write(msg.Pack());
			await _swrite.FlushAsync();
		}

		public void Stop()
		{
			if (!IsStarted) return;
			IsStarted = false;
			IsConnected = false;
			LogMessage("Warning:Stop at " + Environment.StackTrace);
			if (_socket != null)
			{
				try
				{
					_socket.CloseSocket();
				}
				catch (Exception x)
				{
					OnError(x);
				}
				_socket = null;
			}
			_cts_connect?.Cancel();
			LogMessage("ClientWorker Close :_presessions:" + _presessions.Count + " , sessionMap:" + sessionMap.Count);

			foreach (TcpMapClientSession session in _presessions.LockToArray())
				session.Close();
			foreach (TcpMapClientSession session in sessionMap.LockToArray().Select(v => v.Value))
				session.Close();
		}

		async Task MaintainSessionsAsync()
		{
			while (IsConnected)
			{
				await Task.Delay(5000);
				try
				{
					if (sessionMap.Count == 0)
						continue;
					foreach (var kvp in sessionMap.LockToArray())
					{
						if (kvp.Value.ShallRecycle())
						{
							lock (sessionMap)
								sessionMap.TryRemove(kvp.Key, out _);
						}
					}
				}
				catch (Exception x)
				{
					OnError(x);
				}
			}
		}

		internal List<TcpMapClientSession> _presessions = new List<TcpMapClientSession>();
		internal ConcurrentDictionary<string, TcpMapClientSession> sessionMap = new ConcurrentDictionary<string, TcpMapClientSession>();

	}



}
