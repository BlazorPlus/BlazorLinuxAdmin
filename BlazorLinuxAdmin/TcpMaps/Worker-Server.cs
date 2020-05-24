﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BlazorLinuxAdmin.TcpMaps
{

	public class TcpMapServerWorker : TcpMapBaseWorker
	{
		public TcpMapServer Server { get; set; }

		public bool IsListened { get; private set; }

		TcpListener _listener;
		CancellationTokenSource cts;

		public void StartWork()
		{
			if (!Server.IsValidated || Server.IsDisabled)
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
				_listener = new TcpListener(IPAddress.Parse(Server.ServerBind), Server.ServerPort);
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
							bool allowThisSocket = true;
							if (!string.IsNullOrEmpty(this.Server.IPServiceUrl))
							{
								using (HttpClient hc = new HttpClient())
								{
									string queryurl = this.Server.IPServiceUrl + ((IPEndPoint)socket.RemoteEndPoint).Address;
									string res = await hc.GetStringAsync(queryurl);
									LogMessage(res + " - " + queryurl);
									if (res.StartsWith("NO:"))
									{
										allowThisSocket = false;
									}
								}
							}

							if (allowThisSocket)
							{
								socket.InitTcp();
								await ProcessSocketAsync(socket);
							}
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

		async Task ProcessSocketAsync(Socket socket)
		{
			//LogMessage("new server conn : " + socket.LocalEndPoint + " , " + socket.RemoteEndPoint);

			string sessionid = Guid.NewGuid().ToString();

			int tryagainIndex = 0;

		TryAgain:

			if (!socket.Connected)  //this property is not OK .. actually the client has disconnect 
				return;

			tryagainIndex++;

			if (tryagainIndex > 3)  //only try 3 times.
				return;

			TcpMapServerClient sclient = FindClient();

			if (sclient == null)
			{
				if (DateTime.Now - _lastDisconnectTime < TimeSpan.FromSeconds(8))//TODO:const connect wait sclient timeout
				{
					await Task.Delay(500);
					goto TryAgain;
				}
				throw new Exception("no sclient.");
			}


			TcpMapServerClient presession = null;

			try
			{

				lock (_presessions)
				{
					if (_presessions.Count != 0)
					{
						presession = _presessions[0];
						_presessions.RemoveAt(0);
					}
				}

				if (presession != null)
				{
					try
					{
						await presession.UpgradeSessionAsync(sessionid);
					}
					catch (Exception x)
					{
						OnError(x);
						LogMessage("Error:ServerWorker presession upgrade failed @" + tryagainIndex + " , " + sessionid);
						goto TryAgain;
					}
					lock (_sessions)
						_sessions.Add(presession);

					LogMessage("ServerWorker session upgraded @" + tryagainIndex + " , " + sessionid);
				}
				else
				{
					await sclient.StartSessionAsync(sessionid);

					LogMessage("ServerWorker session created @" + tryagainIndex + " , " + sessionid);
				}
			}
			catch (Exception x)
			{
				OnError(x);
				await Task.Delay(500);//TODO:const...
				goto TryAgain;
			}


			try
			{
				TcpMapServerSession session = new TcpMapServerSession(socket.CreateStream(), sessionid);
				sessionMap.TryAdd(sessionid, session);
				LogMessage("Warning: TcpMapServerSession added:" + sessionid);
				try
				{
					if (presession != null)
						presession.AttachToSession(session);
					await session.WorkAsync();
				}
				finally
				{
					sessionMap.TryRemove(sessionid, out _);
					LogMessage("Warning: TcpMapServerSession removed:" + sessionid);
				}
			}
			catch (Exception x)
			{
				OnError(x);
			}

			await sclient.CloseSessionAsync(sessionid);
			//LogMessage("ServerWorker session closed @" + tryagainIndex);


		}

		internal TcpMapServerClient FindClient()
		{
		TryAgain:
			TcpMapServerClient sclient = null;
			lock (_clients)
			{
				if (_clients.Count == 1)
				{
					sclient = _clients[0];
				}
				else if (_clients.Count != 0)
				{
					sclient = _clients[Interlocked.Increment(ref nextclientindex) % _clients.Count];

					if (DateTime.Now - sclient._lastPingTime > TimeSpan.FromSeconds(90))
					{
						//TODO:maybe the client socket has timeout
						sclient.Stop();
						//RemoveClientOrSession(sclient);
						goto TryAgain;
					}
				}
				else
				{
					// no client
				}
			}

			return sclient;
		}

		internal ConcurrentDictionary<string, TcpMapServerSession> sessionMap = new ConcurrentDictionary<string, TcpMapServerSession>();

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
			lock (_clients)
				foreach (var item in _clients.ToArray())
					item.Stop();
			lock (_sessions)
				foreach (var item in _sessions.ToArray())
					item.Stop();
		}

		int nextclientindex = 0;
		internal List<TcpMapServerClient> _clients = new List<TcpMapServerClient>();
		internal List<TcpMapServerClient> _sessions = new List<TcpMapServerClient>();
		internal List<TcpMapServerClient> _presessions = new List<TcpMapServerClient>();

		DateTime _lastDisconnectTime;

		internal void AddClientOrSession(TcpMapServerClient client)
		{
			var list = client._is_client ? _clients : (client.SessionId != null ? _sessions : _presessions);
			lock (list)
				list.Add(client);
			if (list.Count > 1)
			{
				//TODO:shall test alive for other clients
				//When client switch IP , the socket will not timeout for read
				List<TcpMapServerClient> others = new List<TcpMapServerClient>();
				lock (list)
				{
					others = new List<TcpMapServerClient>(list);
				}
				foreach (var other in others)
				{
					if (other == client)
						continue;
					other.TestAlive();
				}
			}
		}
		internal void RemoveClientOrSession(TcpMapServerClient client)
		{
			_lastDisconnectTime = DateTime.Now;
			var list = client._is_client ? _clients : (client.SessionId != null ? _sessions : _presessions);
			lock (list)
				list.Remove(client);
		}


	}

	public class TcpMapServerConnector
	{
		TcpMapServerWorker _worker;
		public TcpMapServerConnector(TcpMapServerWorker sworker)
		{
			_worker = sworker;

		}

		internal async Task AcceptConnectorAndWorkAsync(Socket clientSock, CommandMessage connmsg)
		{
			bool supportEncrypt = _worker.Server.UseEncrypt;
			if (connmsg.Args[4] == "0") supportEncrypt = false;

			byte[] clientKeyIV;
			try
			{
				_worker.Server.ConnectorLicense.DescriptSourceKey(Convert.FromBase64String(connmsg.Args[2]), Convert.FromBase64String(connmsg.Args[3]), out clientKeyIV);
			}
			catch (Exception x)
			{
				_worker.OnError(x);
				var failedmsg = new CommandMessage("ConnectFailed", "InvalidSecureKey");
				await clientSock.SendAsync(failedmsg.Pack(), SocketFlags.None);
				return;
			}

			TcpMapServerClient sclient = _worker.FindClient();
			if (sclient == null)
			{
				var failedmsg = new CommandMessage("ConnectFailed", "NoClient");
				await clientSock.SendAsync(failedmsg.Pack(), SocketFlags.None);
				return;
			}

			string mode = connmsg.Args[5];
			string connArgument = connmsg.Args[6];
			if (mode == "USB")//use server bandwidth
			{
				var resmsg = new CommandMessage("ConnectOK", "ConnectOK", supportEncrypt ? "1" : "0");
				await clientSock.SendAsync(resmsg.Pack(), SocketFlags.None);

				Stream _sread, _swrite;
				if (supportEncrypt)
				{
					_worker.Server.ConnectorLicense.OverrideStream(clientSock.CreateStream(), clientKeyIV, out _sread, out _swrite);
				}
				else
				{
					_sread = _swrite = clientSock.CreateStream();
				}

				using Socket localsock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				string ip = _worker.Server.ServerBind;
				if (ip == "0.0.0.0") ip = "127.0.0.1";
				localsock.InitTcp();
				await localsock.ConnectAsync(ip, _worker.Server.ServerPort);

				TcpMapConnectorSession session = new TcpMapConnectorSession(new SimpleSocketStream(localsock));
				await session.DirectWorkAsync(_sread, _swrite);
			}
			else if (mode == "UDP")
			{
				if (_udpcache == null)
				{
					lock (typeof(TcpMapServerConnector))
					{
						if (_udpcache == null)
						{
							var opt = new MemoryCacheOptions();
							Microsoft.Extensions.Options.IOptions<MemoryCacheOptions> iopt = Microsoft.Extensions.Options.Options.Create(opt);
							_udpcache = new MemoryCache(iopt);
						}

					}
				}

				UdpInfoItem natinfo;
				string key = connArgument + ":" + sclient.SessionId;
				if (!_udpcache.TryGetValue(key, out natinfo) || natinfo.HasExpired())
				{
					var udpmsg = await sclient.CreateUDPNatAsync(connArgument);
					string[] pair = udpmsg.Args[1].Split(':');
					string addr = pair[0];
					int port = int.Parse(pair[1]);
					natinfo = new UdpInfoItem(addr + ":" + port);
					_udpcache.Set(key, natinfo);
				}



				var resmsg = new CommandMessage("ConnectOK", "ConnectOK", supportEncrypt ? "1" : "0", natinfo.NatInfo);
				await clientSock.SendAsync(resmsg.Pack(), SocketFlags.None);

				resmsg = await CommandMessage.ReadFromSocketAsync(clientSock);
				if (resmsg == null)
					return;
				throw new NotImplementedException("work for " + resmsg);
			}
			else if (mode == "RCP")
			{

				var resmsg = new CommandMessage("ConnectOK", "ConnectOK", supportEncrypt ? "1" : "0"
					, ((IPEndPoint)sclient._socket.RemoteEndPoint).Address.ToString(), sclient.OptionRouterClientPort.ToString());
				await clientSock.SendAsync(resmsg.Pack(), SocketFlags.None);
			}
			else
			{
				throw new NotImplementedException();
			}
		}

		class UdpInfoItem
		{
			public UdpInfoItem(string natinfo)
			{
				NatInfo = natinfo;
			}
			public string NatInfo { get; private set; }
			DateTime DTStart = DateTime.Now;
			public bool HasExpired()
			{
				return DateTime.Now - DTStart > TimeSpan.FromSeconds(9);
			}
		}

		//TODO:use application level global cache..
		static MemoryCache _udpcache;

	}

	public class TcpMapServerClient
	{
		private TcpMapServerClient()
		{

		}


		internal int OptionRouterClientPort;

		static long _nextscid = 30001;

		long _scid = Interlocked.Increment(ref _nextscid);

		TcpMapServerWorker _worker = null;
		Stream _sread, _swrite;
		internal Socket _socket;

		internal bool _is_client = true;
		internal bool _is_session = false;

		internal DateTime _lastPingTime = DateTime.Now;

		public string SessionId = null;
		CancellationTokenSource _cts_wait_upgrade;


		internal async Task UpgradeSessionAsync(string newsid)
		{
			SessionId = newsid;
			await _swrite.WriteAsync(new CommandMessage("UpgradeSession", newsid).Pack());
		ReadAgain:
			var res = await CommandMessage.ReadFromStreamAsync(_sread);
			if (res == null)
				throw (new Exception("Invalid null message "));
			if (res.Name == "_ping_" || res.Name == "_ping_result_")
				goto ReadAgain;
			if (res.Name != "UpgradeSessionResult")
				throw (new Exception("Invalid message : " + res));
			if (res.Args[0] == "OK")
				return;
			throw (new Exception("Upgrade Session Failed : " + res));
		}

		static public async Task AcceptConnectAndWorkAsync(Socket socket, CommandMessage connmsg)
		{
			System.Diagnostics.Debug.Assert(connmsg.Name == "ClientConnect" || connmsg.Name == "SessionConnect");

			TcpMapServerClient client = new TcpMapServerClient();
			client._socket = socket;
			await client.WorkAsync(connmsg);

		}

		async Task WorkAsync(CommandMessage connmsg)
		{
			if (connmsg.Name == "SessionConnect")
			{
				_is_client = false;
				_is_session = true;
			}


			byte[] clientKeyIV;

			_worker = TcpMapService.FindServerWorkerByKey(connmsg.Args[0], int.Parse(connmsg.Args[1]));

			string failedreason = null;

			if (_worker == null)
				failedreason = "NotFound";
			else if (!_worker.Server.IsValidated)
				failedreason = "NotValidated";
			else if (_worker.Server.IsDisabled)
				failedreason = "NotEnabled";

			if (_worker == null || !string.IsNullOrEmpty(failedreason))
			{
				var failedmsg = new CommandMessage("ConnectFailed", failedreason);
				await _socket.SendAsync(failedmsg.Pack(), SocketFlags.None);
				return;
			}

			bool supportEncrypt = _worker.Server.UseEncrypt;
			if (connmsg.Args[4] == "0") supportEncrypt = false;

			try
			{
				_worker.Server.License.DescriptSourceKey(Convert.FromBase64String(connmsg.Args[2]), Convert.FromBase64String(connmsg.Args[3]), out clientKeyIV);
			}
			catch (Exception x)
			{
				_worker.OnError(x);
				var failedmsg = new CommandMessage("ConnectFailed", "InvalidSecureKey");
				await _socket.SendAsync(failedmsg.Pack(), SocketFlags.None);
				return;
			}

			var successMsg = new CommandMessage("ConnectOK", "ConnectOK", supportEncrypt ? "1" : "0");
			await _socket.SendAsync(successMsg.Pack(), SocketFlags.None);

			if (supportEncrypt)
			{
				_worker.Server.License.OverrideStream(_socket.CreateStream(), clientKeyIV, out _sread, out _swrite);
			}
			else
			{
				_sread = _swrite = _socket.CreateStream();
			}

			if (_is_session)
			{
				this.SessionId = connmsg.Args[5];
				if (SessionId == null)
				{
					_cts_wait_upgrade = new CancellationTokenSource();
				}
			}

			_worker.AddClientOrSession(this);
			try
			{
				if (_is_client)
				{

					_ = _swrite.WriteAsync(new CommandMessage("SetOption", "ClientEndPoint", _socket.RemoteEndPoint.ToString()).Pack());

					while (true)
					{
						var msg = await CommandMessage.ReadFromStreamAsync(_sread);
						//process it...

						if (msg == null)
						{
							//TcpMapService.LogMessage("no message ? Connected:" + _socket.Connected);
							throw new SocketException(995);
						}

						//_worker.LogMessage("TcpMapServerClient get msg " + msg);

						switch (msg.Name)
						{
							case "SetOption":
								string optvalue = msg.Args[1];
								switch (msg.Args[0])
								{
									case "RouterClientPort":
										this.OptionRouterClientPort = int.Parse(optvalue);
										break;
									default:
										_worker.LogMessage("Error:Ignore option " + msg);
										break;
								}
								break;
							case "StartSessionResult":
							case "CloseSessionResult":
							case "CreateUDPNatResult":
								long reqid = long.Parse(msg.Args[0]);
								if (reqmap.TryGetValue(reqid, out var ritem))
								{
									ritem.Response = msg;
									ritem.cts.Cancel();
								}
								else
								{
									_worker.LogMessage("Request Expired : " + msg);
								}
								break;
							case "_ping_":
								this._lastPingTime = DateTime.Now;
								await _swrite.WriteAsync(new CommandMessage("_ping_result_").Pack());
								break;
							case "_ping_result_":
								break;
							default:
								_worker.LogMessage("Error: 5 Ignore message " + msg);
								break;
						}

					}
				}
				else if (_is_session)
				{
					if (SessionId == null)
					{
						_worker.LogMessage($"Warning:ServerClient*{_scid} Wait for Upgrade To Session ");

						while (SessionId == null)
						{
							if (await _cts_wait_upgrade.Token.WaitForSignalSettedAsync(28000))  //check the presession closed or not every 28 seconds
							{
								if (SessionId != null)
									break;  //OK, session attached.
								throw new SocketException(995); //_cts_wait_upgrade Cancelled , by SessionId is not seted
							}

							//if (!_socket.Connected) //NEVER useful..
							//{
							//	_worker.LogMessage("Warning:presession exit.");
							//	throw new SocketException(995);
							//}

							if (!_socket.Poll(0, SelectMode.SelectRead)) //WORKS..
								continue;

							if (_socket.Available == 0)
							{
								_worker.LogMessage("Warning:presession exit!");
								throw new SocketException(995);
							}

							//_worker.LogMessage("Warning:presession send message before upgrade ?"+_socket.Available);
							if (!_cts_wait_upgrade.IsCancellationRequested)
							{
								//TODO:not locked/sync, not so safe it the presession is upgrading
								var msg = await CommandMessage.ReadFromSocketAsync(_socket);
								if (msg.Name == "_ping_")
								{
									byte[] resp = new CommandMessage("_ping_result_").Pack();
									await _socket.SendAsync(resp, SocketFlags.None);
								}
								else
								{
									_worker.LogMessage("Warning:presession unexpected msg : " + msg);
								}
							}

						}

						_worker.LogMessage($"Warning:ServerClient*{_scid} SessionId:" + SessionId);
					}

					int waitMapTimes = 0;
				TryGetMap:

					if (_attachedSession != null)
					{
						_worker.LogMessage($"ServerClient*{_scid} use attached Session : {SessionId} *{waitMapTimes}");
						await _attachedSession.UseThisSocketAsync(_sread, _swrite);
					}
					else if (_worker.sessionMap.TryGetValue(SessionId, out var session))
					{
						_worker.LogMessage($"ServerClient*{_scid} session server ok : {SessionId} *{waitMapTimes}");
						await session.UseThisSocketAsync(_sread, _swrite);
					}
					else
					{
						if (waitMapTimes < 5)
						{
							waitMapTimes++;
							await Task.Delay(10);//wait sessionMap be added..
							goto TryGetMap;
						}

						_worker.LogMessage($"Warning:ServerClient*{_scid} session not found : {SessionId}");
						throw new Exception($"ServerClient*{_scid} session not found : {SessionId}");
					}
				}
				else
				{
					throw new InvalidOperationException();
				}
			}
			catch (SocketException)
			{
				//no log
			}
			catch (Exception x)
			{
				_worker.OnError(x);
			}
			finally
			{
				_worker.RemoveClientOrSession(this);
			}

			_worker.LogMessage($"ServerClient*{_scid} WorkAsync END " + SessionId);
		}

		TcpMapServerSession _attachedSession;

		internal void AttachToSession(TcpMapServerSession session)
		{
			_attachedSession = session;
			_cts_wait_upgrade.Cancel();
		}

		internal void TestAlive()
		{
			_ = Task.Run(async delegate
			  {
				  try
				  {
					  await _swrite.WriteAsync(new CommandMessage("_ping_result_").Pack());
				  }
				  catch (Exception x)
				  {
					  _worker.OnError(x);
					  Stop();
				  }
			  });
		}

		internal void Stop()
		{
			_cts_wait_upgrade?.Cancel();
			if (_socket != null)
			{
				try
				{
					_socket.CloseSocket();
				}
				catch (Exception x)
				{
					TcpMapService.OnError(x);
				}
			}
		}

		class RequestItem
		{
			internal CommandMessage Response;
			internal CancellationTokenSource cts = new CancellationTokenSource();
		}

		long nextreqid;
		ConcurrentDictionary<long, RequestItem> reqmap = new ConcurrentDictionary<long, RequestItem>();

		public async Task StartSessionAsync(string sid)
		{
			await SendCmdAsync("StartSession", sid);
		}

		public async Task CloseSessionAsync(string sid)
		{
			await SendCmdAsync("CloseSession", sid);
		}

		public async Task<CommandMessage> CreateUDPNatAsync(string natinfo)
		{
			return await SendCmdAsync("CreateUDPNat", natinfo);
		}

		async Task<CommandMessage> SendCmdAsync(string cmd, string arg)
		{
			int timeout = 18000;
			long reqid = Interlocked.Increment(ref nextreqid);
			CommandMessage msg = new CommandMessage();
			msg.Name = cmd;
			msg.Args = new string[] { reqid.ToString(), arg, timeout.ToString() };

			RequestItem ritem = new RequestItem();

			reqmap.TryAdd(reqid, ritem);
			try
			{
				_worker.LogMessage("TcpMapServerClient sending #" + reqid + " : " + msg);

				await _swrite.WriteAsync(msg.Pack());
				await _swrite.FlushAsync();

				if (!await ritem.cts.Token.WaitForSignalSettedAsync(timeout))//TODO:const
					throw new Exception($"request timeout #{reqid} '{msg}'");

				if (ritem.Response == null)
					throw new Exception($"No Response ? ");

				if (ritem.Response.Args[1] == "Error")
					throw new Exception("Command Failed.");

				return ritem.Response;
			}
			finally
			{
				reqmap.TryRemove(reqid, out _);
			}
		}



	}
}
