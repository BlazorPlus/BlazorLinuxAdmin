using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;


namespace BlazorLinuxAdmin.TcpMaps
{


	class TcpMapClientSession : TcpMapBaseSession
	{
		public TcpMapClient Client { get; set; }
		public string SessionId { get; set; }

		public bool IsConnected { get; set; }

		Socket _sock_local;   //Client
		Socket _sock_server; //Server
		CancellationTokenSource _cts_connect;
		CancellationTokenSource _cts_upgrade;
		Stream _sread, _swrite;
		DateTime _lastwritetime = DateTime.Now;

		public TcpMapClientSession(TcpMapClient client, string sid)
		{
			Client = client;
			SessionId = sid;
		}

		public async Task StartAsync()
		{
			_sock_local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			_sock_local.InitTcp();
			await _sock_local.ConnectWithTimeoutAsync(Client.ClientHost, Client.ClientPort, 12000);
			_ = WorkAsync();
		}


		public async Task<string> WaitUpgradeAsync()
		{
			// _peer is NULL
			_ = WorkAsync();

			_cts_upgrade = new CancellationTokenSource();
			while (_cts_connect != null && !_cts_connect.IsCancellationRequested)
			{
				if (!string.IsNullOrEmpty(SessionId))
					break;
				if (await _cts_upgrade.Token.WaitForSignalSettedAsync(9000))
					break;
			}
			return SessionId;
		}


		public void Close()
		{
			_cts_connect?.Cancel();
			_cts_upgrade?.Cancel();
			_sock_local?.CloseSocket();
			_sock_server?.CloseSocket();
		}



		async Task WorkAsync()
		{
			IsStarted = true;
			IsConnected = false;
			//LogMessage("ClientSession WorkAsync start");
			int connectedTimes = 0;
			try
			{
				int againTimeout = 125;
			StartAgain:

				_sock_server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				_sock_server.InitTcp();
				_cts_connect = new CancellationTokenSource();
				try
				{
					await _sock_server.ConnectAsync(Client.ServerHost, 6022);

					//LogMessage("connected to 6022");
				}
				catch (Exception x)
				{
					OnError(x);
					_sock_server.CloseSocket();
					_sock_server = null;
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
						connmsg.Name = "SessionConnect";
						List<string> arglist = new List<string>();
						arglist.Add(this.Client.License.Key);
						arglist.Add(this.Client.ServerPort.ToString());
						byte[] encryptedKeyIV, sourceHash;
						Client.License.GenerateSecureKeyAndHash(out clientKeyIV, out encryptedKeyIV, out sourceHash);
						arglist.Add(Convert.ToBase64String(encryptedKeyIV));
						arglist.Add(Convert.ToBase64String(sourceHash));
						arglist.Add(supportEncrypt ? "1" : "0");
						arglist.Add(SessionId);//sessionid at [5]
						connmsg.Args = arglist.ToArray();

						await _sock_server.SendAsync(connmsg.Pack(), SocketFlags.None);

						//LogMessage("wait for conn msg");

						connmsg = await CommandMessage.ReadFromSocketAsync(_sock_server);

						if (connmsg == null)
						{
							TcpMapService.LogMessage("no message ? Connected:" + _sock_server.Connected);
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
					//LogMessage("ConnectOK #" + connectedTimes);

					if (supportEncrypt)
					{
						Client.License.OverrideStream(_sock_server.CreateStream(), clientKeyIV, out _sread, out _swrite);
					}
					else
					{
						_sread = _swrite = _sock_server.CreateStream();
					}

					if (string.IsNullOrEmpty(SessionId))
					{
						//wait for Upgrade
						while (SessionId == null)
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
								throw (new SocketException(995));

							switch (msg.Name)
							{
								case "UpgradeSession":

									try
									{
										_sock_local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
										_sock_local.InitTcp();
										await _sock_local.ConnectWithTimeoutAsync(Client.ClientHost, Client.ClientPort, 12000);
									}
									catch (Exception x)
									{
										TcpMapService.OnError(x);
										await _swrite.WriteAsync(new CommandMessage("UpgradeSessionResult", "Failed").Pack());
										continue;
									}
									SessionId = msg.Args[0];
									if (_cts_upgrade != null) _cts_upgrade.Cancel();
									await _swrite.WriteAsync(new CommandMessage("UpgradeSessionResult", "OK").Pack());
									break;
								case "_ping_":
									await _swrite.WriteAsync(new CommandMessage("_ping_result_").Pack());
									break;
								case "_ping_result_":
									break;
								default:
									LogMessage("Error: 1 Ignore message " + msg);
									break;
							}
						}
					}

					await WorkAsync(_sock_local.CreateStream());

				}
				catch (Exception x)
				{
					OnError(x);
				}

			}
			catch (SocketException)
			{

			}
			catch (Exception x)
			{
				OnError(x);
			}

			IsStarted = false;
			IsConnected = false;
			_sock_server?.CloseSocket();
			_sock_local?.CloseSocket();
			_workend = true;
		}

		bool _workend = false;

		internal bool ShallRecycle()
		{
			return _workend;
		}


		protected override async Task<CommandMessage> ReadMessageAsync()
		{
		ReadAgain:

			CommandMessage msg;
			CancellationTokenSource cts = null;
			if (DateTime.Now - _lastwritetime > TimeSpan.FromMilliseconds(12000))
			{
				_lastwritetime = DateTime.Now;
				await _swrite.WriteAsync(new CommandMessage("_ping_", "forwrite").Pack());
			}
			else
			{
				cts = new CancellationTokenSource();
				_ = Task.Run(async delegate
				{
					if (await cts.Token.WaitForSignalSettedAsync(16000))
						return;
					_lastwritetime = DateTime.Now;
					await _swrite.WriteAsync(new CommandMessage("_ping_", "forread").Pack());
				});
			}
			try
			{
				msg = await CommandMessage.ReadFromStreamAsync(_sread);
			}
			finally
			{
				if (cts != null) cts.Cancel();
			}

			if (msg == null || msg.Name == "data")
				return msg;

			//TcpMapService.LogMessage("ClientSession:get message " + msg);

			switch (msg.Name)
			{
				case "_ping_":
					await _swrite.WriteAsync(new CommandMessage("_ping_result_").Pack());
					break;
				case "_ping_result_":
					break;
				default:
					TcpMapService.LogMessage("Error: 2 Ignore message " + msg);
					break;
			}
			goto ReadAgain;
		}

		protected override async Task WriteMessageAsync(CommandMessage msg)
		{
			_lastwritetime = DateTime.Now;
			await _swrite.WriteAsync(msg.Pack());
		}
	}

}
