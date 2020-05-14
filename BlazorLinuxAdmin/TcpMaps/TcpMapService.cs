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

	static public class TcpMapService
	{
		public static int DefaultBufferSize = 1024 * 128;

		public static string DataFolder { get; set; }

		public static bool IsServiceAdded { get; private set; }

		//public static bool IsServiceMapped { get; private set; }

		public static bool IsServiceRunning { get; private set; }

		public static Exception ServiceError { get; private set; }

		public static ConcurrentQueue<Exception> ServiceErrors { get; } = new ConcurrentQueue<Exception>();
		static public void OnError(Exception err)
		{
			Console.WriteLine(err);
			ServiceError = err;
			ServiceErrors.Enqueue(err);
			while (ServiceErrors.Count > 100)
				ServiceErrors.TryDequeue(out _);
			ErrorOccurred?.Invoke(err);
		}

		static public event Action<Exception> ErrorOccurred;

		static public ConcurrentQueue<string> LogMessages = new ConcurrentQueue<string>();

		static public void LogMessage(string msg)
		{
			Console.WriteLine(msg);
			LogMessages.Enqueue(msg);
			while (LogMessages.Count > 200)
				LogMessages.TryDequeue(out _);
			MessageLogged?.Invoke(msg);
		}

		static public event Action<string> MessageLogged;


		static public void AddTcpMaps(IServiceCollection services)
		{
			IsServiceAdded = true;
			StartService();
		}

		static TcpListener _listener6022;

		static void StartService()
		{
			try
			{
				_listener6022 = new TcpListener(IPAddress.Any, 6022);
				_listener6022.Start();

				if (string.IsNullOrEmpty(DataFolder))
					DataFolder = Path.Combine(Directory.GetCurrentDirectory(), "data_tcpmaps");

				if (!Directory.Exists(DataFolder)) Directory.CreateDirectory(DataFolder);

				foreach (string jsonfilepath in Directory.GetFiles(DataFolder, "*.json"))
				{
					string shortname = Path.GetFileName(jsonfilepath);
					try
					{
						if (shortname.StartsWith("TcpMapClient_"))
						{
							var mapclient = JsonSerializer.Deserialize<TcpMapClient>(File.ReadAllText(jsonfilepath));
							AddStartClient(mapclient);
						}
						if (shortname.StartsWith("TcpMapServer_"))
						{
							var mapserver = JsonSerializer.Deserialize<TcpMapServer>(File.ReadAllText(jsonfilepath));
							AddStartServer(mapserver);
						}
						if (shortname.StartsWith("TcpMapConnector_"))
						{
							var mapconnector = JsonSerializer.Deserialize<TcpMapConnector>(File.ReadAllText(jsonfilepath));
							AddStartConnector(mapconnector);
						}
					}
					catch (Exception x)
					{
						OnError(new Exception("Failed process " + shortname + " , " + x.Message, x));
					}
				}
			}
			catch (Exception x)
			{
				OnError(x);
				if (_listener6022 != null)
				{
					_listener6022.Stop();
					_listener6022 = null;
				}
				return;
			}

			IsServiceRunning = true;

			_ = Task.Run(AcceptWorkAsync);
		}

		static async Task AcceptWorkAsync()
		{
			try
			{
				while (true)
				{
					var socket = await _listener6022.AcceptSocketAsync();
					_ = Task.Run(async delegate
					  {
						  LogMessage("Warning:accept server " + socket.LocalEndPoint + "," + socket.RemoteEndPoint);
						  try
						  {
							  socket.InitTcp();
							  await ProcesSocketAsync(socket);
						  }
						  catch (Exception x)
						  {
							  OnError(x);
						  }
						  finally
						  {
							  LogMessage("Warning:close server " + socket.LocalEndPoint + "," + socket.RemoteEndPoint);
							  socket.CloseSocket();
						  }
					  });
				}
			}
			catch (Exception x)
			{
				OnError(x);
			}
		}

		static async Task ProcesSocketAsync(Socket socket)
		{

			while (true)
			{
				var msg = await CommandMessage.ReadFromSocketAsync(socket);

				if (msg == null)
				{
					//LogMessage("no message ? Connected:" + socket.Connected);
					return;
				}

				//LogMessage("new msg : " + msg);

				switch (msg.Name)
				{
					case "ClientConnect":
					case "SessionConnect":
						await TcpMapServerClient.AcceptConnectAndWorkAsync(socket, msg);
						return;
					case "ConnectorConnect":
						var server = FindServerWorkerByPort(int.Parse(msg.Args[1]));
						string failedmsg = null;
						if (server == null)
						{
							failedmsg = "NoPort";
						}
						else if (server.Server.IsDisabled)
						{
							failedmsg = "IsDisabled";
						}
						else if (!server.Server.AllowConnector)
						{
							failedmsg = "NotAllow";
						}
						else if (server.Server.ConnectorLicense == null)
						{
							failedmsg = "NotLicense";
						}
						else if (server.Server.ConnectorLicense.Key != msg.Args[0])
						{
							failedmsg = "LicenseNotMatch";
						}
						if (failedmsg != null)
						{
							var resmsg = new CommandMessage("ConnectFailed", failedmsg);
							await socket.SendAsync(resmsg.Pack(), SocketFlags.None);
						}
						else
						{
							await server.AcceptConnectorAndWorkAsync(socket, msg);
						}
						break;
					case ""://other direct request..
					default:
						throw new Exception("Invaild command " + msg.Name);
				}
			}

		}



		static List<TcpMapClientWorker> _clients = new List<TcpMapClientWorker>();// as client side
		static List<TcpMapServerWorker> _servers = new List<TcpMapServerWorker>();// as server side
		static List<TcpMapConnectorWorker> _connectors = new List<TcpMapConnectorWorker>();// as connector side

		static public TcpMapClientWorker[] GetClientWorkers()
		{
			return _clients.LockToArray();
		}
		static public TcpMapServerWorker[] GetServerWorkers()
		{
			return _servers.LockToArray();
		}
		static public TcpMapConnectorWorker[] GetConnectorWorkers()
		{
			return _connectors.LockToArray();
		}

		static TcpMapClientWorker AddStartClient(TcpMapClient client)
		{
			var conn = new TcpMapClientWorker() { Client = client };
			lock (_clients)
				_clients.Add(conn);
			if (!client.IsDisabled)
				conn.StartWork();
			return conn;
		}
		static TcpMapServerWorker AddStartServer(TcpMapServer server)
		{
			var conn = new TcpMapServerWorker() { Server = server };
			lock (_servers)
				_servers.Add(conn);
			conn.StartWork();
			return conn;
		}
		static TcpMapConnectorWorker AddStartConnector(TcpMapConnector mapconnector)
		{
			var conn = new TcpMapConnectorWorker() { Connector = mapconnector };
			lock (_connectors)
				_connectors.Add(conn);
			conn.StartWork();
			return conn;
		}

		static internal TcpMapConnectorWorker FindConnectorWorkerByPort(int localport)
		{
			lock (_connectors)
				return _connectors.FirstOrDefault(v => v.Connector.LocalPort == localport);

		}
		static internal TcpMapServerWorker FindServerWorkerByPort(int serverport)
		{
			lock (_servers)
				return _servers.FirstOrDefault(v => v.Server.ServerPort == serverport);
		}

		static internal TcpMapServerWorker FindServerWorkerByKey(string licenseKey, int serverport)
		{
			lock (_servers)
				return _servers.FirstOrDefault(v => v.Server.ServerPort == serverport && v.Server.License.Key == licenseKey);
		}

		static public TcpMapClientWorker CreateClientWorker(TcpMapLicense lic, int serverPort)
		{
			TcpMapClient client = new TcpMapClient() { License = lic };
			client.Id = DateTime.Now.ToString("yyyyMMddHHmmssfff");
			client.IsDisabled = true;
			client.ServerHost = "servername";
			client.ServerPort = serverPort;
			client.ClientHost = "localhost";
			client.ClientPort = 80;
			string jsonfilepath = Path.Combine(DataFolder, "TcpMapClient_" + client.Id + ".json");
			File.WriteAllText(jsonfilepath, JsonSerializer.Serialize(client));
			return AddStartClient(client);
		}
		static public void ReAddClient(TcpMapClient client)
		{
			string jsonfilepath = Path.Combine(DataFolder, "TcpMapClient_" + client.Id + ".json");
			File.WriteAllText(jsonfilepath, JsonSerializer.Serialize(client));
			TcpMapClientWorker clientWorker = null;
			lock (_clients)
			{
				clientWorker = _clients.Where(v => v.Client.Id == client.Id).FirstOrDefault();
				if (clientWorker != null) _clients.Remove(clientWorker);
			}
			if (clientWorker != null)
			{
				clientWorker.Stop();
			}
			AddStartClient(client);
		}

		static void CheckPortAvailable(int port)
		{
			{
				var existWorker = FindServerWorkerByPort(port);
				if (existWorker != null)
					throw new Exception("port is being used by server worker " + existWorker.Server.Id);
			}
			{
				var existWorker = FindConnectorWorkerByPort(port);
				if (existWorker != null)
					throw new Exception("port is being used by connector worker " + existWorker.Connector.Id);
			}
		}

		static public TcpMapServerWorker CreateServerWorker(TcpMapLicense lic, int port)
		{
			CheckPortAvailable(port);

			TcpMapServer server = new TcpMapServer();
			server.Id = DateTime.Now.ToString("yyyyMMddHHmmssfff");
			server.License = lic;
			server.IsDisabled = false;
			server.IsValidated = true;
			server.ServerPort = port;
			string jsonfilepath = Path.Combine(DataFolder, "TcpMapServer_" + server.Id + ".json");
			File.WriteAllText(jsonfilepath, JsonSerializer.Serialize(server));
			return AddStartServer(server);
		}

		static public void ReAddServer(TcpMapServer server)
		{
			string jsonfilepath = Path.Combine(DataFolder, "TcpMapServer_" + server.Id + ".json");
			File.WriteAllText(jsonfilepath, JsonSerializer.Serialize(server));
			TcpMapServerWorker serverWorker = null;
			lock (_clients)
			{
				serverWorker = _servers.Where(v => v.Server.Id == server.Id).FirstOrDefault();
				if (serverWorker != null) _servers.Remove(serverWorker);
			}
			if (serverWorker != null)
			{
				serverWorker.Stop();
			}
			AddStartServer(server);
		}

		static public TcpMapConnectorWorker CreateConnectorWorker(int port)
		{
			CheckPortAvailable(port);

			TcpMapConnector conn = new TcpMapConnector();
			conn.Id = DateTime.Now.ToString("yyyyMMddHHmmssfff");
			conn.LocalPort = port;
			conn.ServerHost = "servername";
			conn.ServerPort = port;
			conn.IsDisabled = true;
			string jsonfilepath = Path.Combine(DataFolder, "TcpMapConnector_" + conn.Id + ".json");
			File.WriteAllText(jsonfilepath, JsonSerializer.Serialize(conn));
			return AddStartConnector(conn);
		}

		static public void ReAddConnector(TcpMapConnector connector)
		{
			string jsonfilepath = Path.Combine(DataFolder, "TcpMapConnector_" + connector.Id + ".json");
			File.WriteAllText(jsonfilepath, JsonSerializer.Serialize(connector));
			TcpMapConnectorWorker connectorWorker = null;
			lock (_clients)
			{
				connectorWorker = _connectors.Where(v => v.Connector.Id == connector.Id).FirstOrDefault();
				if (connectorWorker != null) _connectors.Remove(connectorWorker);
			}
			if (connectorWorker != null)
			{
				connectorWorker.Stop();
			}
			AddStartConnector(connector);
		}
	}


}